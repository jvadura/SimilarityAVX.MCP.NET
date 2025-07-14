using System;
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
    
    private static readonly string[] IgnorePatterns = 
    {
        "bin/", "obj/", ".vs/", "packages/", "TestResults/",
        "*.dll", "*.exe", "*.pdb", "*.cache", "*.user",
        ".git/", "node_modules/", "dist/", "build/",
        "*.min.js", "*.min.css", "_ReSharper*/", "*.suo"
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
    
    public FileChanges GetChanges(string directory)
    {
        var stateFile = GetStateFile(directory);
        var currentFiles = GetFileHashes(directory);
        var previousFiles = LoadState(stateFile);
        
        var added = currentFiles.Keys.Except(previousFiles.Keys).ToList();
        var removed = previousFiles.Keys.Except(currentFiles.Keys).ToList();
        var modified = currentFiles
            .Where(kvp => previousFiles.ContainsKey(kvp.Key) && 
                          previousFiles[kvp.Key] != kvp.Value)
            .Select(kvp => kvp.Key)
            .ToList();
        
        var changes = new FileChanges(added, modified, removed);
        
        if (changes.HasChanges)
        {
            Console.Error.WriteLine($"[FileSynchronizer] Changes detected: +{added.Count} ~{modified.Count} -{removed.Count}");
        }
        
        return changes;
    }
    
    public void SaveState(string directory)
    {
        var stateFile = GetStateFile(directory);
        var currentFiles = GetFileHashes(directory);
        
        var json = JsonSerializer.Serialize(currentFiles, new JsonSerializerOptions 
        { 
            WriteIndented = true 
        });
        
        File.WriteAllText(stateFile, json);
        Console.Error.WriteLine($"[FileSynchronizer] State saved for {currentFiles.Count} files");
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
        var hashes = new Dictionary<string, string>();
        
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
                    
                    lock (hashes)
                    {
                        hashes[file] = hash;
                    }
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
        
        return hashes;
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
    
    private string GetStateFile(string directory)
    {
        // Create a unique filename based on directory path
        using var sha = SHA256.Create();
        var hashBytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(directory.ToLowerInvariant()));
        var hash = Convert.ToBase64String(hashBytes)
            .Replace('/', '_')
            .Replace('+', '-')
            .Replace('=', '_');
        
        return Path.Combine(_stateDirectory, $"state_{hash}.json");
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
}