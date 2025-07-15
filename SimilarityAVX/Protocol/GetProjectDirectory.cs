using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Dapper;

namespace CSharpMcpServer.Protocol;

/// <summary>
/// Helper class to retrieve the project directory from an indexed database
/// by finding the common root path of all indexed files.
/// </summary>
public static class GetProjectDirectory
{
    /// <summary>
    /// Gets the project directory by analyzing indexed file paths.
    /// Returns null if no files are indexed or database doesn't exist.
    /// </summary>
    public static string? GetDirectory(string projectName)
    {
        var dbFileName = $"codesearch-{SanitizeProjectName(projectName)}.db";
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "csharp-mcp-server",
            dbFileName
        );

        if (!File.Exists(dbPath))
        {
            return null;
        }

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            
            // Get all unique file paths
            var filePaths = conn.Query<string>(
                "SELECT DISTINCT file_path FROM chunks"
            ).ToList();

            if (!filePaths.Any())
            {
                return null;
            }

            // Find common root directory
            return FindCommonRootPath(filePaths);
        }
        catch
        {
            return null;
        }
    }

    private static string? FindCommonRootPath(List<string> paths)
    {
        if (paths.Count == 0)
            return null;

        if (paths.Count == 1)
            return Path.GetDirectoryName(paths[0]);

        // Split all paths into segments
        var pathSegments = paths
            .Select(p => Path.GetDirectoryName(p)?.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Where(segments => segments != null)
            .ToList();

        if (!pathSegments.Any())
            return null;

        // Find the shortest path (it can't have more common segments than this)
        var shortestLength = pathSegments.Min(segments => segments!.Length);
        
        // Find common segments
        var commonSegments = new List<string>();
        
        for (int i = 0; i < shortestLength; i++)
        {
            var segment = pathSegments[0]![i];
            
            // Check if this segment is common to all paths
            if (pathSegments.All(segments => segments![i] == segment))
            {
                commonSegments.Add(segment);
            }
            else
            {
                break;
            }
        }

        if (commonSegments.Count == 0)
            return null;

        // Reconstruct the common path
        var commonPath = string.Join(Path.DirectorySeparatorChar, commonSegments);
        
        // Handle root paths (e.g., "C:" on Windows or "/" on Unix)
        if (Path.IsPathRooted(paths[0]))
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT && commonSegments.Count > 0)
            {
                // Windows: Add back the drive separator if needed
                if (!commonPath.EndsWith(":"))
                {
                    return commonPath;
                }
                return commonPath + Path.DirectorySeparatorChar;
            }
            else if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                // Unix: Ensure it starts with /
                if (!commonPath.StartsWith("/"))
                {
                    return "/" + commonPath;
                }
            }
        }

        return commonPath;
    }

    private static string SanitizeProjectName(string projectName)
    {
        // Replace invalid filename characters with underscore
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = projectName;
        foreach (var c in invalid)
        {
            sanitized = sanitized.Replace(c, '_');
        }
        return sanitized.ToLowerInvariant();
    }
}