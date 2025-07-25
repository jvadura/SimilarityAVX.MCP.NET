using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CSharpMcpServer.Models;
using CSharpMcpServer.Storage;

namespace CSharpMcpServer.Core;

/// <summary>
/// Monitors indexed projects for file changes and automatically reindexes them.
/// Provides startup verification and real-time file system monitoring with debouncing.
/// </summary>
public class ProjectMonitor : IDisposable
{
    private readonly Configuration _configuration;
    private readonly ConcurrentDictionary<string, ProjectWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, DateTime> _pendingReindexes = new();
    private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _pendingFileChanges = new(); // Track files changed per project
    private readonly ConcurrentDictionary<string, string> _projectDirectories = new(); // Track project -> directory mapping
    private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _directoryProjects = new(); // Track directory -> projects mapping
    private readonly Timer _reindexTimer;
    private readonly Timer? _periodicRescanTimer;
    private readonly ConcurrentDictionary<string, DateTime> _lastPeriodicRescan = new();
    private readonly TimeSpan _debounceDelay;
    private readonly object _reindexLock = new();
    private bool _disposed;

    public ProjectMonitor(Configuration configuration)
    {
        _configuration = configuration;
        _debounceDelay = TimeSpan.FromSeconds(configuration.Monitoring.DebounceDelaySeconds);
        _reindexTimer = new Timer(ProcessPendingReindexes, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        
        // Set up periodic rescan timer if enabled
        if (_configuration.Monitoring.EnablePeriodicRescan && _configuration.Monitoring.PeriodicRescanMinutes > 0)
        {
            Console.Error.WriteLine($"[ProjectMonitor] Periodic rescan enabled - will check every {_configuration.Monitoring.PeriodicRescanMinutes} minutes");
            _periodicRescanTimer = new Timer(ProcessPeriodicRescans, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }
    }

    /// <summary>
    /// Verifies all indexed projects on startup and starts monitoring for changes.
    /// </summary>
    public async Task VerifyAllProjectsAsync()
    {
        Console.Error.WriteLine("[ProjectMonitor] Starting project verification...");
        
        var dbDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "csharp-mcp-server"
        );

        if (!Directory.Exists(dbDir))
        {
            Console.Error.WriteLine("[ProjectMonitor] No database directory found. No projects to verify.");
            return;
        }

        var dbFiles = Directory.GetFiles(dbDir, "codesearch-*.db");
        if (dbFiles.Length == 0)
        {
            Console.Error.WriteLine("[ProjectMonitor] No project databases found.");
            return;
        }

        var verificationTasks = new List<Task>();
        var duplicateProjects = new Dictionary<string, List<string>>(); // directory -> list of projects

        foreach (var dbFile in dbFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(dbFile);
            var projectName = fileName.Substring("codesearch-".Length);
            
            verificationTasks.Add(VerifyProjectAsync(projectName));
        }

        await Task.WhenAll(verificationTasks);
        
        // Check for duplicate projects pointing to same directory
        foreach (var kvp in _directoryProjects)
        {
            if (kvp.Value.Count > 1)
            {
                Console.Error.WriteLine($"[ProjectMonitor] WARNING: Multiple projects point to same directory {kvp.Key}: {string.Join(", ", kvp.Value)}");
            }
        }
        
        Console.Error.WriteLine($"[ProjectMonitor] Verification complete. {_watchers.Count} projects being monitored.");
    }

    /// <summary>
    /// Verifies a single project and sets up monitoring if needed.
    /// </summary>
    private async Task VerifyProjectAsync(string projectName)
    {
        try
        {
            Console.Error.WriteLine($"[ProjectMonitor] Verifying project '{projectName}'...");
            
            // Get the project directory from stored metadata
            var projectDir = await GetStoredProjectDirectory(projectName);
            if (projectDir == null)
            {
                // Fall back to analyzing file paths in database
                projectDir = GetProjectDirectoryFromDatabase(projectName);
                if (projectDir == null)
                {
                    Console.Error.WriteLine($"[ProjectMonitor] Project '{projectName}' has no indexed files. Skipping.");
                    return;
                }
                
                // Store the directory for future use
                await StoreProjectDirectory(projectName, projectDir);
            }

            if (!Directory.Exists(projectDir))
            {
                Console.Error.WriteLine($"[ProjectMonitor] Project directory not found: {projectDir}. Skipping monitoring.");
                return;
            }

            // Track project directory mapping
            _projectDirectories[projectName] = projectDir;
            _directoryProjects.AddOrUpdate(projectDir, 
                key => { var bag = new ConcurrentBag<string>(); bag.Add(projectName); return bag; },
                (key, existing) => { existing.Add(projectName); return existing; });
            
            // Skip verification if another project already verified this directory
            if (_directoryProjects[projectDir].Count() > 1)
            {
                Console.Error.WriteLine($"[ProjectMonitor] Project '{projectName}' shares directory with another project, skipping verification.");
            }
            else
            {
                // Check for changes using FileSynchronizer with project-specific state
                var synchronizer = new FileSynchronizer();
                var changes = synchronizer.GetChanges(projectDir, projectName); // Pass project name for unique state
                
                if (changes.HasChanges)
                {
                    Console.Error.WriteLine($"[ProjectMonitor] Project '{projectName}' has {changes.Added.Count} added, {changes.Modified.Count} modified, {changes.Removed.Count} removed files.");
                    
                    // Save state immediately since we have changes to process
                    // This ensures the detected changes are recorded before reindexing
                    synchronizer.SaveState(projectDir, projectName);
                    
                    // Schedule immediate reindex for all projects in this directory
                    foreach (var proj in _directoryProjects[projectDir])
                    {
                        _pendingReindexes[proj] = DateTime.UtcNow;
                        
                        // Track specific files that changed for incremental processing
                        var fileSet = _pendingFileChanges.GetOrAdd(proj, _ => new ConcurrentBag<string>());
                        if (fileSet != null)
                        {
                            foreach (var file in changes.Added.Concat(changes.Modified).Concat(changes.Removed))
                            {
                                fileSet.Add(file);
                            }
                        }
                    }
                }
                else
                {
                    Console.Error.WriteLine($"[ProjectMonitor] Project '{projectName}' is up to date.");
                }
            }

            // Set up file system monitoring if enabled
            if (_configuration.Monitoring.EnableFileWatching)
            {
                StartMonitoring(projectName, projectDir);
            }
            else
            {
                Console.Error.WriteLine($"[ProjectMonitor] File watching is disabled for '{projectName}'");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProjectMonitor] Error verifying project '{projectName}': {ex.Message}");
        }
    }

    /// <summary>
    /// Starts file system monitoring for a project directory.
    /// </summary>
    private void StartMonitoring(string projectName, string projectDir)
    {
        if (_watchers.ContainsKey(projectName))
        {
            Console.Error.WriteLine($"[ProjectMonitor] Already monitoring project '{projectName}'");
            return;
        }
        
        // Check if we're already monitoring this directory with another project
        var existingWatcher = _watchers.Values.FirstOrDefault(w => w.Directory.Equals(projectDir, StringComparison.OrdinalIgnoreCase));
        if (existingWatcher != null)
        {
            Console.Error.WriteLine($"[ProjectMonitor] Directory already monitored by another project, skipping FileSystemWatcher for '{projectName}'");
            return;
        }

        try
        {
            var watcher = new ProjectWatcher(projectName, projectDir, OnFileChanged);
            _watchers[projectName] = watcher;
            
            Console.Error.WriteLine($"[ProjectMonitor] Started monitoring '{projectName}' at {projectDir}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProjectMonitor] Failed to start monitoring '{projectName}': {ex.Message}");
        }
    }

    /// <summary>
    /// Handles file change events from FileSystemWatcher.
    /// </summary>
    private void OnFileChanged(string projectName, string filePath, WatcherChangeTypes changeType)
    {
        // Filter for relevant file types
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (extension != ".cs" && extension != ".razor" && extension != ".cshtml" && 
            extension != ".c" && extension != ".h")
        {
            return;
        }

        // Early filtering of ignored paths to prevent unnecessary reindexing
        if (ShouldIgnorePath(filePath))
        {
            return;
        }

        lock (_reindexLock)
        {
            // Get the directory for this project
            if (_projectDirectories.TryGetValue(projectName, out var projectDir))
            {
                // Schedule reindex for all projects sharing this directory
                if (_directoryProjects.TryGetValue(projectDir, out var projects))
                {
                    foreach (var proj in projects)
                    {
                        // Reset the debounce timer by updating to current time
                        _pendingReindexes[proj] = DateTime.UtcNow;
                        
                        // Track the specific file that changed
                        var fileSet = _pendingFileChanges.GetOrAdd(proj, _ => new ConcurrentBag<string>());
                        fileSet.Add(filePath);
                        
                        Console.Error.WriteLine($"[ProjectMonitor] File change detected in '{proj}': {changeType} - {filePath}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Timer callback that processes pending reindexes after debounce delay.
    /// </summary>
    private void ProcessPendingReindexes(object? state)
    {
        if (_disposed) return;

        var now = DateTime.UtcNow;
        var projectsToReindex = new List<string>();

        lock (_reindexLock)
        {
            foreach (var kvp in _pendingReindexes.ToList())
            {
                var projectName = kvp.Key;
                var lastChangeTime = kvp.Value;
                
                // Check if enough time has passed since last change
                if (now - lastChangeTime >= _debounceDelay)
                {
                    projectsToReindex.Add(projectName);
                    _pendingReindexes.TryRemove(projectName, out _);
                }
            }
        }

        // Reindex projects outside of lock
        foreach (var projectName in projectsToReindex)
        {
            Task.Run(async () => await ReindexProjectAsync(projectName));
        }
    }

    /// <summary>
    /// Timer callback that processes periodic rescans for all projects.
    /// </summary>
    private void ProcessPeriodicRescans(object? state)
    {
        if (_disposed || !_configuration.Monitoring.EnablePeriodicRescan) return;

        var now = DateTime.UtcNow;
        var rescanInterval = TimeSpan.FromMinutes(_configuration.Monitoring.PeriodicRescanMinutes);
        var projectsToRescan = new List<string>();

        // Check which projects need periodic rescan
        foreach (var projectName in _projectDirectories.Keys)
        {
            if (!_lastPeriodicRescan.TryGetValue(projectName, out var lastRescan) || 
                now - lastRescan >= rescanInterval)
            {
                projectsToRescan.Add(projectName);
                _lastPeriodicRescan[projectName] = now;
            }
        }

        // Schedule rescans
        foreach (var projectName in projectsToRescan)
        {
            Console.Error.WriteLine($"[ProjectMonitor] Scheduling periodic rescan for '{projectName}'");
            Task.Run(async () => await ForceRescanProjectAsync(projectName));
        }
    }

    /// <summary>
    /// Forces a rescan of a project by scheduling it for immediate reindexing.
    /// </summary>
    private async Task ForceRescanProjectAsync(string projectName)
    {
        try
        {
            Console.Error.WriteLine($"[ProjectMonitor] Starting periodic rescan for '{projectName}'...");
            
            // Get the project directory
            var projectDir = await GetStoredProjectDirectory(projectName) ?? 
                           GetProjectDirectoryFromDatabase(projectName);
            
            if (projectDir == null || !Directory.Exists(projectDir))
            {
                Console.Error.WriteLine($"[ProjectMonitor] Cannot rescan '{projectName}': directory not found");
                return;
            }

            // Use FileSynchronizer to check for changes
            var synchronizer = new FileSynchronizer();
            var changes = synchronizer.GetChanges(projectDir, projectName);
            
            if (!changes.HasChanges)
            {
                Console.Error.WriteLine($"[ProjectMonitor] Periodic rescan of '{projectName}' found no changes");
                return;
            }

            Console.Error.WriteLine($"[ProjectMonitor] Periodic rescan of '{projectName}' found {changes.Added.Count} added, " +
                                  $"{changes.Modified.Count} modified, {changes.Removed.Count} removed files");

            // Schedule immediate reindex by bypassing debounce delay
            lock (_reindexLock)
            {
                _pendingReindexes[projectName] = DateTime.UtcNow.AddSeconds(-_configuration.Monitoring.DebounceDelaySeconds - 1);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProjectMonitor] Error during periodic rescan of '{projectName}': {ex.Message}");
        }
    }

    /// <summary>
    /// Reindexes a project after verifying changes with SHA256.
    /// </summary>
    private async Task ReindexProjectAsync(string projectName)
    {
        try
        {
            Console.Error.WriteLine($"[ProjectMonitor] Starting automatic reindex for '{projectName}'...");
            
            var projectDir = await GetStoredProjectDirectory(projectName) ?? 
                           GetProjectDirectoryFromDatabase(projectName);
            
            if (projectDir == null || !Directory.Exists(projectDir))
            {
                Console.Error.WriteLine($"[ProjectMonitor] Cannot reindex '{projectName}': directory not found");
                return;
            }

            // Get pending file changes for incremental scan
            HashSet<string>? changedFiles = null;
            if (_pendingFileChanges.TryRemove(projectName, out var fileSet))
            {
                changedFiles = new HashSet<string>(fileSet);
                Console.Error.WriteLine($"[ProjectMonitor] Processing {changedFiles.Count} specific file changes for '{projectName}'");
            }
            
            // Verify changes with SHA256 using project-specific state
            var synchronizer = new FileSynchronizer();
            var changes = synchronizer.GetChanges(projectDir, projectName, changedFiles);
            
            if (!changes.HasChanges)
            {
                Console.Error.WriteLine($"[ProjectMonitor] No actual changes detected for '{projectName}' after SHA256 verification");
                return;
            }
            
            // Create indexer and perform incremental update
            // Note: State will be saved by CodeIndexer after successful indexing
            using var indexer = new CodeIndexer(_configuration, projectName);
            var stats = await indexer.IndexDirectoryAsync(projectDir, false, null, changes);
            
            Console.Error.WriteLine($"[ProjectMonitor] Automatic reindex complete for '{projectName}': " +
                                  $"{stats.FilesProcessed} files, {stats.ChunksCreated} chunks in {stats.Duration.TotalSeconds:F1}s");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProjectMonitor] Error reindexing '{projectName}': {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the stored project directory from database metadata.
    /// </summary>
    private async Task<string?> GetStoredProjectDirectory(string projectName)
    {
        try
        {
            var storage = new SqliteStorage(projectName);
            return await Task.Run(() => storage.GetMetadata("project_directory"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Stores the project directory in database metadata for future use.
    /// </summary>
    private async Task StoreProjectDirectory(string projectName, string directory)
    {
        try
        {
            var storage = new SqliteStorage(projectName);
            await Task.Run(() => storage.SaveMetadata("project_directory", directory));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ProjectMonitor] Failed to store project directory: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets project directory by analyzing file paths in the database.
    /// </summary>
    private string? GetProjectDirectoryFromDatabase(string projectName)
    {
        try
        {
            return CSharpMcpServer.Protocol.GetProjectDirectory.GetDirectory(projectName);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        _reindexTimer?.Dispose();
        _periodicRescanTimer?.Dispose();
        
        foreach (var watcher in _watchers.Values)
        {
            watcher.Dispose();
        }
        
        _watchers.Clear();
    }

    /// <summary>
    /// Internal class that wraps FileSystemWatcher for a single project.
    /// </summary>
    /// <summary>
    /// Checks if a file path should be ignored based on common ignore patterns.
    /// This is a duplicate of FileSynchronizer.ShouldIgnore to enable early filtering.
    /// </summary>
    private bool ShouldIgnorePath(string filePath)
    {
        // Common ignore patterns - duplicated from FileSynchronizer for early filtering
        string[] ignorePatterns = 
        {
            "bin/", "obj/", ".vs/", "packages/", "TestResults/",
            "*.dll", "*.exe", "*.pdb", "*.cache", "*.user",
            ".git/", "node_modules/", "dist/", "build/",
            "*.min.js", "*.min.css", "_ReSharper*/", "*.suo",
            "Migrations/"
        };

        var normalizedPath = filePath.Replace('\\', '/');
        var fileName = Path.GetFileName(filePath);

        foreach (var pattern in ignorePatterns)
        {
            // Directory patterns (ending with /)
            if (pattern.EndsWith('/'))
            {
                var dirPattern = pattern.TrimEnd('/');
                if (normalizedPath.Contains($"/{dirPattern}/") || 
                    normalizedPath.Contains($"\\{dirPattern}\\"))
                {
                    return true;
                }
            }
            // Extension patterns (starting with *)
            else if (pattern.StartsWith('*'))
            {
                var extension = pattern.Substring(1);
                if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            // Wildcard patterns (containing *)
            else if (pattern.Contains('*'))
            {
                var parts = pattern.Split('*');
                if (parts.Length == 2 && normalizedPath.Contains(parts[0]) && 
                    normalizedPath.Contains(parts[1]))
                {
                    return true;
                }
            }
            // Simple contains check
            else if (normalizedPath.Contains(pattern))
            {
                return true;
            }
        }

        return false;
    }

    private class ProjectWatcher : IDisposable
    {
        private readonly string _projectName;
        private readonly FileSystemWatcher _watcher;
        private readonly Action<string, string, WatcherChangeTypes> _onChanged;
        
        public string Directory { get; }

        public ProjectWatcher(string projectName, string directory, Action<string, string, WatcherChangeTypes> onChanged)
        {
            _projectName = projectName;
            _onChanged = onChanged;
            Directory = directory;
            
            _watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true
            };

            _watcher.Changed += OnFileSystemEvent;
            _watcher.Created += OnFileSystemEvent;
            _watcher.Deleted += OnFileSystemEvent;
            _watcher.Renamed += OnFileRenamed;
            _watcher.Error += OnError;
        }

        private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
        {
            _onChanged(_projectName, e.FullPath, e.ChangeType);
        }

        private void OnFileRenamed(object sender, RenamedEventArgs e)
        {
            _onChanged(_projectName, e.OldFullPath, WatcherChangeTypes.Deleted);
            _onChanged(_projectName, e.FullPath, WatcherChangeTypes.Created);
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            Console.Error.WriteLine($"[ProjectWatcher] Error monitoring '{_projectName}': {e.GetException().Message}");
        }

        public void Dispose()
        {
            _watcher?.Dispose();
        }
    }
}