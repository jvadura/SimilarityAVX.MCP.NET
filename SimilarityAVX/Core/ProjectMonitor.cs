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
    private readonly Timer _reindexTimer;
    private readonly TimeSpan _debounceDelay;
    private readonly object _reindexLock = new();
    private bool _disposed;

    public ProjectMonitor(Configuration configuration)
    {
        _configuration = configuration;
        _debounceDelay = TimeSpan.FromSeconds(configuration.Monitoring.DebounceDelaySeconds);
        _reindexTimer = new Timer(ProcessPendingReindexes, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
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

        foreach (var dbFile in dbFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(dbFile);
            var projectName = fileName.Substring("codesearch-".Length);
            
            verificationTasks.Add(VerifyProjectAsync(projectName));
        }

        await Task.WhenAll(verificationTasks);
        
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

            // Check for changes using FileSynchronizer
            var synchronizer = new FileSynchronizer();
            var changes = synchronizer.GetChanges(projectDir);
            
            if (changes.HasChanges)
            {
                Console.Error.WriteLine($"[ProjectMonitor] Project '{projectName}' has {changes.Added.Count} added, {changes.Modified.Count} modified, {changes.Removed.Count} removed files.");
                
                // Schedule immediate reindex
                _pendingReindexes[projectName] = DateTime.UtcNow;
            }
            else
            {
                Console.Error.WriteLine($"[ProjectMonitor] Project '{projectName}' is up to date.");
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

        lock (_reindexLock)
        {
            // Update or add pending reindex with current time
            _pendingReindexes[projectName] = DateTime.UtcNow;
            Console.Error.WriteLine($"[ProjectMonitor] File change detected in '{projectName}': {changeType} - {filePath}");
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

            // Verify changes with SHA256
            var synchronizer = new FileSynchronizer();
            var changes = synchronizer.GetChanges(projectDir);
            
            if (!changes.HasChanges)
            {
                Console.Error.WriteLine($"[ProjectMonitor] No actual changes detected for '{projectName}' after SHA256 verification");
                return;
            }

            // Create indexer and perform incremental update
            var indexer = new CodeIndexer(_configuration, projectName);
            var stats = await indexer.IndexDirectoryAsync(projectDir, false);
            
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
        
        foreach (var watcher in _watchers.Values)
        {
            watcher.Dispose();
        }
        
        _watchers.Clear();
    }

    /// <summary>
    /// Internal class that wraps FileSystemWatcher for a single project.
    /// </summary>
    private class ProjectWatcher : IDisposable
    {
        private readonly string _projectName;
        private readonly FileSystemWatcher _watcher;
        private readonly Action<string, string, WatcherChangeTypes> _onChanged;

        public ProjectWatcher(string projectName, string directory, Action<string, string, WatcherChangeTypes> onChanged)
        {
            _projectName = projectName;
            _onChanged = onChanged;
            
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