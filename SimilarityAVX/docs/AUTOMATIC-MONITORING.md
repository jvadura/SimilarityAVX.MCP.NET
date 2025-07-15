# Automatic Project Monitoring and Reindexing

The C# MCP Server now includes automatic monitoring capabilities that keep your semantic search index synchronized with your codebase without manual intervention.

## Overview

The monitoring system provides two key features:

1. **Startup Verification**: Automatically checks all indexed projects when the MCP server starts, detecting any changes made while the server was offline
2. **Real-time Monitoring**: Watches project directories for file changes and automatically reindexes after a configurable delay

## How It Works

### Startup Verification
When the MCP server starts, it:
1. Discovers all indexed projects from existing database files
2. Retrieves the stored project directory for each project
3. Performs SHA256 verification of all tracked files
4. Automatically reindexes projects with detected changes
5. Sets up file system watchers for continuous monitoring

### Real-time File Monitoring
While the server is running:
1. FileSystemWatcher monitors all indexed project directories
2. Changes to `.cs`, `.razor`, `.cshtml`, `.c`, and `.h` files are detected
3. Multiple rapid changes are batched using a debounce mechanism (default: 60 seconds)
4. After the delay, SHA256 verification confirms actual content changes
5. Only changed files are reindexed incrementally

### Debouncing Logic
The debounce mechanism prevents excessive reindexing during active development:
- Timer starts when first change is detected
- Additional changes reset the timer
- Reindexing only occurs after 60 seconds of no changes
- This allows for save-all operations and rapid edits without triggering multiple reindexes

## Configuration

Add the `monitoring` section to your `config.json`:

```json
{
  "monitoring": {
    "enableAutoReindex": true,      // Enable/disable all monitoring features
    "verifyOnStartup": true,        // Check projects when server starts
    "debounceDelaySeconds": 60,     // Delay after last change before reindexing
    "enableFileWatching": true      // Enable real-time file system monitoring
  }
}
```

### Configuration Options

- **enableAutoReindex**: Master switch for all monitoring features. Set to `false` to disable completely.
- **verifyOnStartup**: When `true`, verifies all projects on server startup. Useful for catching changes made while offline.
- **debounceDelaySeconds**: How long to wait after the last file change before reindexing. Default is 60 seconds.
- **enableFileWatching**: Enables FileSystemWatcher for real-time monitoring. Set to `false` for manual-only indexing.

## Performance Considerations

The monitoring system is designed to be lightweight:
- SHA256 hashing uses parallel processing (16 threads by default)
- Only changed files are re-parsed and re-embedded
- Vectors for unchanged files remain in memory
- File watching uses OS-level notifications (minimal CPU usage)
- Debouncing prevents excessive reindexing

## Logging

The monitoring system provides detailed logging:
```
[ProjectMonitor] Starting project verification...
[ProjectMonitor] Verifying project 'frontend'...
[ProjectMonitor] Project 'frontend' has 2 added, 1 modified, 0 removed files.
[ProjectMonitor] Started monitoring 'frontend' at E:\Projects\Frontend
[ProjectMonitor] File change detected in 'frontend': Changed - E:\Projects\Frontend\src\App.cs
[ProjectMonitor] Starting automatic reindex for 'frontend'...
[ProjectMonitor] Automatic reindex complete for 'frontend': 3 files, 15 chunks in 2.3s
```

## Disabling Monitoring

To disable automatic monitoring while keeping manual indexing:

1. **Disable all monitoring**:
   ```json
   "monitoring": {
     "enableAutoReindex": false
   }
   ```

2. **Disable only file watching** (keeps startup verification):
   ```json
   "monitoring": {
     "enableAutoReindex": true,
     "verifyOnStartup": true,
     "enableFileWatching": false
   }
   ```

3. **Disable only startup verification** (keeps file watching):
   ```json
   "monitoring": {
     "enableAutoReindex": true,
     "verifyOnStartup": false,
     "enableFileWatching": true
   }
   ```

## Limitations

- Only monitors file types configured for indexing (`.cs`, `.razor`, `.cshtml`, `.c`, `.h`)
- Requires the original project directory to still exist
- File renames are treated as delete + create operations
- Very large projects may experience brief CPU spikes during reindexing

## Benefits

1. **Always Up-to-Date**: Search results always reflect the current codebase
2. **Zero Manual Effort**: No need to remember to reindex after changes
3. **Efficient**: Only processes actual changes, not entire codebase
4. **Configurable**: Adjust timing and features to match your workflow
5. **Transparent**: Detailed logging shows exactly what's happening

## Technical Details

The monitoring system consists of:
- `ProjectMonitor`: Core monitoring logic with SHA256 verification
- `ProjectMonitorService`: ASP.NET Core hosted service wrapper
- `FileSynchronizer`: Existing change detection with persistent state
- Project directory stored in SQLite metadata for persistence
- Concurrent dictionary tracking pending reindexes with timestamps
- Timer-based processing of pending reindexes after debounce delay