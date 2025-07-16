using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using CSharpMcpServer.Models;

namespace CSharpMcpServer.Core;

public class FileSynchronizer
{
    private readonly string _stateDirectory;
    private readonly int _maxDegreeOfParallelism;
    
    // In-memory cache for file hashes - project -> (filepath -> hash)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _memoryCache = new();
    
    private static readonly string[] IgnorePatterns = 
    {
        "bin/", "obj/", ".vs/", "packages/", "TestResults/",
        "*.dll", "*.exe", "*.pdb", "*.cache", "*.user",
        ".git/", "node_modules/", "dist/", "build/",
        "*.min.js", "*.min.css", "_ReSharper*/", "*.suo",
        "Migrations/"
    };
    
    public FileSynchronizer(int maxDegreeOfParallelism = 16)
    {
        _maxDegreeOfParallelism = maxDegreeOfParallelism;
        _stateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "csharp-mcp-server",
            "state"
        );
        Directory.CreateDirectory(_stateDirectory);
    }
    
    public FileChanges GetChanges(string directory, string? projectName = null, HashSet<string>? changedFiles = null)
    {
        var cacheKey = projectName ?? directory;
        
        // Get or create memory cache for this project
        var cache = _memoryCache.GetOrAdd(cacheKey, _ => new ConcurrentDictionary<string, string>());
        
        // Load previous state if cache is empty
        if (cache.IsEmpty)
        {
            var stateFile = GetStateFile(directory, projectName);
            var savedState = LoadState(stateFile);
            foreach (var kvp in savedState)
            {
                cache.TryAdd(kvp.Key, kvp.Value);
            }
            Console.Error.WriteLine($"[FileSynchronizer] Loaded {cache.Count} file hashes into memory cache");
        }
        
        Dictionary<string, string> currentFiles;
        
        if (changedFiles != null && changedFiles.Count > 0)
        {
            // Incremental scan - only check the specific files that changed
            Console.Error.WriteLine($"[FileSynchronizer] Incremental scan of {changedFiles.Count} changed files...");
            currentFiles = GetFileHashesIncremental(directory, changedFiles, cache);
        }
        else
        {
            // Full scan - check all files
            currentFiles = GetFileHashes(directory);
            
            // DO NOT update cache here - we need to compare against the old state first!
        }
        
        // Compare against cache
        var added = currentFiles.Keys.Except(cache.Keys).ToList();
        var removed = cache.Keys.Except(currentFiles.Keys).ToList();
        var modified = currentFiles
            .Where(kvp => cache.ContainsKey(kvp.Key) && 
                          cache[kvp.Key] != kvp.Value)
            .Select(kvp => kvp.Key)
            .ToList();
        
        var changes = new FileChanges(added, modified, removed);
        
        if (changes.HasChanges)
        {
            Console.Error.WriteLine($"[FileSynchronizer] Changes detected: +{added.Count} ~{modified.Count} -{removed.Count}");
            
            // Update cache only for changed files
            foreach (var file in removed)
            {
                cache.TryRemove(file, out _);
            }
            foreach (var kvp in currentFiles.Where(kvp => added.Contains(kvp.Key) || modified.Contains(kvp.Key)))
            {
                cache.AddOrUpdate(kvp.Key, kvp.Value, (k, v) => kvp.Value);
            }
        }
        else if (changedFiles == null)
        {
            // For full scans with no changes detected, we still need to ensure cache is in sync
            // This handles cases where cache might be out of sync (e.g., manual cache file edits)
            // Remove files that no longer exist
            foreach (var file in cache.Keys.Except(currentFiles.Keys).ToList())
            {
                cache.TryRemove(file, out _);
            }
            // Add any new files that somehow weren't detected as changes
            foreach (var kvp in currentFiles.Where(kvp => !cache.ContainsKey(kvp.Key)))
            {
                cache.TryAdd(kvp.Key, kvp.Value);
            }
        }
        
        return changes;
    }
    
    public void SaveState(string directory, string? projectName = null)
    {
        var cacheKey = projectName ?? directory;
        
        // Get cache for this project
        if (!_memoryCache.TryGetValue(cacheKey, out var cache))
        {
            Console.Error.WriteLine($"[FileSynchronizer] Warning: No cache found for {cacheKey}, performing full scan");
            cache = new ConcurrentDictionary<string, string>(GetFileHashes(directory));
        }
        
        // Save cache to disk
        var stateFile = GetStateFile(directory, projectName);
        var stateDict = cache.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        
        var json = JsonSerializer.Serialize(stateDict, new JsonSerializerOptions 
        { 
            WriteIndented = true 
        });
        
        File.WriteAllText(stateFile, json);
        Console.Error.WriteLine($"[FileSynchronizer] State saved for {cache.Count} files (from memory cache)");
    }
    
    public void ClearState()
    {
        if (Directory.Exists(_stateDirectory))
        {
            Directory.Delete(_stateDirectory, true);
        }
        Directory.CreateDirectory(_stateDirectory);
        Console.Error.WriteLine("[FileSynchronizer] State cleared");
    }
    
    private Dictionary<string, string> GetFileHashes(string directory)
    {
        var hashes = new ConcurrentDictionary<string, string>();
        
        try
        {
            var csFiles = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories);
            var razorFiles = Directory.EnumerateFiles(directory, "*.razor", SearchOption.AllDirectories);
            var cshtmlFiles = Directory.EnumerateFiles(directory, "*.cshtml", SearchOption.AllDirectories);
            var cFiles = Directory.EnumerateFiles(directory, "*.c", SearchOption.AllDirectories);
            var hFiles = Directory.EnumerateFiles(directory, "*.h", SearchOption.AllDirectories);
            
            var files = csFiles.Concat(razorFiles).Concat(cshtmlFiles).Concat(cFiles).Concat(hFiles)
                .Where(f => !ShouldIgnore(f, directory))
                .ToList();
            
            Console.Error.WriteLine($"[FileSynchronizer] Scanning {files.Count} code files...");
            
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = _maxDegreeOfParallelism }, file =>
            {
                try
                {
                    using var sha = SHA256.Create();
                    using var stream = File.OpenRead(file);
                    var hash = Convert.ToBase64String(sha.ComputeHash(stream));
                    
                    hashes.TryAdd(file, hash);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[FileSynchronizer] Warning: Could not hash {file}: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FileSynchronizer] Error scanning directory: {ex.Message}");
        }
        
        return new Dictionary<string, string>(hashes);
    }
    
    private Dictionary<string, string> LoadState(string stateFile)
    {
        if (!File.Exists(stateFile))
            return new Dictionary<string, string>();
        
        try
        {
            var json = File.ReadAllText(stateFile);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) 
                ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FileSynchronizer] Error loading state: {ex.Message}");
            return new Dictionary<string, string>();
        }
    }
    
    private string GetStateFile(string directory, string? projectName = null)
    {
        // Create a unique filename based on directory path and optional project name
        var identifier = projectName != null ? $"{directory}|{projectName}" : directory;
        
        using var sha = SHA256.Create();
        var hashBytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(identifier.ToLowerInvariant()));
        var hash = Convert.ToBase64String(hashBytes)
            .Replace('/', '_')
            .Replace('+', '-')
            .Replace('=', '_');
        
        var prefix = projectName != null ? $"state_{projectName}_" : "state_";
        return Path.Combine(_stateDirectory, $"{prefix}{hash}.json");
    }
    
    private bool ShouldIgnore(string filePath, string baseDirectory)
    {
        // Normalize paths
        var relativePath = Path.GetRelativePath(baseDirectory, filePath).Replace('\\', '/');
        var normalizedPath = filePath.Replace('\\', '/');
        
        foreach (var pattern in IgnorePatterns)
        {
            if (pattern.EndsWith('/'))
            {
                // Directory pattern
                var dirPattern = pattern.TrimEnd('/');
                if (relativePath.Contains('/' + dirPattern + '/') || 
                    relativePath.StartsWith(dirPattern + '/'))
                    return true;
            }
            else if (pattern.StartsWith('*'))
            {
                // Extension pattern
                if (filePath.EndsWith(pattern.Substring(1), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else
            {
                // File pattern
                if (normalizedPath.Contains(pattern))
                    return true;
            }
        }
        
        // Also ignore very large files
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > 1024 * 1024) // > 1MB
            {
                Console.Error.WriteLine($"[FileSynchronizer] Ignoring large file: {filePath} ({fileInfo.Length / 1024}KB)");
                return true;
            }
        }
        catch { }
        
        return false;
    }
    
    private Dictionary<string, string> GetFileHashesIncremental(string directory, HashSet<string> filesToCheck, ConcurrentDictionary<string, string> cache)
    {
        var result = new Dictionary<string, string>();
        
        // First, copy all NON-CHANGED files from cache
        foreach (var kvp in cache)
        {
            // Only copy if it's not in the changed files list
            if (!filesToCheck.Contains(kvp.Key) && File.Exists(kvp.Key))
            {
                result[kvp.Key] = kvp.Value;
            }
        }
        
        // Then compute hashes for the changed files
        var relevantFiles = filesToCheck
            .Where(f => !ShouldIgnore(f, directory))
            .Where(f => 
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                return ext == ".cs" || ext == ".razor" || ext == ".cshtml" || ext == ".c" || ext == ".h";
            })
            .ToList();
        
        Console.Error.WriteLine($"[FileSynchronizer] Computing hashes for {relevantFiles.Count} changed files");
        
        Parallel.ForEach(relevantFiles, new ParallelOptions { MaxDegreeOfParallelism = _maxDegreeOfParallelism }, file =>
        {
            try
            {
                if (File.Exists(file))
                {
                    using var sha = SHA256.Create();
                    using var stream = File.OpenRead(file);
                    var hash = Convert.ToBase64String(sha.ComputeHash(stream));
                    
                    lock (result)
                    {
                        result[file] = hash;
                    }
                }
                else
                {
                    // File was deleted - don't add to result
                    Console.Error.WriteLine($"[FileSynchronizer] File deleted: {file}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FileSynchronizer] Warning: Could not hash {file}: {ex.Message}");
            }
        });
        
        return result;
    }
    
    public void ClearCache(string? projectName = null)
    {
        if (projectName != null)
        {
            _memoryCache.TryRemove(projectName, out _);
            Console.Error.WriteLine($"[FileSynchronizer] Cleared memory cache for project '{projectName}'");
        }
        else
        {
            _memoryCache.Clear();
            Console.Error.WriteLine("[FileSynchronizer] Cleared all memory caches");
        }
    }
}