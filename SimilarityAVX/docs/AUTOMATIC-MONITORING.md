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

The monitoring system has been optimized for real-time performance:

### Memory Optimization
- **In-Memory Hash Cache**: All file hashes permanently cached in RAM after first load
- **Incremental Vector Updates**: No full index rebuilds - only affected vectors updated
- **Smart Capacity Management**: Vector arrays grow by 50% when needed
- **Lazy Deletion**: Deleted vectors marked but not removed until 25% threshold

### I/O Optimization  
- **Single File Scan**: Only changed files are hashed (not all 763 files)
- **Memory-Only Operations**: After initial load, all operations from cache
- **Batch State Saves**: State files written from memory without rescanning

### Measured Performance (763 files, single file change)
- **Before**: 2-3 seconds, 2,289 file operations, 130MB memory ops
- **After**: 0.4 seconds, 1 file operation, 0.1MB memory ops
- **Improvement**: 86% faster, 99.96% fewer file ops, 99.92% less memory

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

## Technical Architecture

### Core Components

1. **ProjectMonitor** (`Core/ProjectMonitor.cs`)
   - Manages FileSystemWatcher instances per unique directory
   - Tracks changed files in `_pendingFileChanges` dictionary
   - Implements proper debouncing with timer reset on each change
   - Passes `FileChanges` object to CodeIndexer to avoid duplicate scanning
   - Handles projects sharing directories gracefully

2. **FileSynchronizer** (`Core/FileSynchronizer.cs`)
   - **In-Memory Cache**: `ConcurrentDictionary<project, Dictionary<file, hash>>`
   - **Incremental Scanning**: `GetFileHashesIncremental()` only processes changed files
   - **Project-Specific State**: State files include project name to prevent collisions
   - **Smart Caching**: Loads state into memory once, all subsequent ops are memory-only

3. **VectorMemoryStore** (`Storage/VectorMemoryStore.cs`)
   - **Incremental Updates**: `AppendVectors()` doesn't rebuild entire index
   - **Lazy Deletion**: `RemoveVectorsByPath()` marks as deleted, compacts at 25%
   - **Dynamic Arrays**: Capacity grows by 50% to minimize reallocations
   - **Index Mapping**: Maintains `_idToIndex` and `_vectorIndexToEntryIndex`

4. **CodeIndexer** (`Core/CodeIndexer.cs`)
   - Accepts optional `FileChanges` parameter to skip duplicate detection
   - Uses precomputed changes from ProjectMonitor when available
   - Properly implements `IDisposable` for resource cleanup

### Key Design Decisions

1. **No Shared FileSynchronizer**: Each component creates its own instance to avoid coupling
2. **FileChanges Passing**: ProjectMonitor computes changes once, passes to CodeIndexer
3. **State Consistency**: State saved immediately after change detection
4. **Memory First**: All hot paths operate from memory, disk only for persistence