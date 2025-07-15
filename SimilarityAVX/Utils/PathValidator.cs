using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CSharpMcpServer.Utils;

/// <summary>
/// Validates file paths against configured allowed directories to prevent path traversal attacks
/// </summary>
public static class PathValidator
{
    /// <summary>
    /// Validates that a path is within the allowed directories
    /// </summary>
    /// <param name="path">Path to validate</param>
    /// <param name="allowedDirectories">List of allowed root directories</param>
    /// <returns>True if path is allowed, false otherwise</returns>
    public static bool IsPathAllowed(string path, List<string> allowedDirectories)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (allowedDirectories == null || allowedDirectories.Count == 0)
            return false;

        try
        {
            // Get the full path to resolve any relative paths or symbolic links
            var fullPath = Path.GetFullPath(path);
            
            // Normalize the path for comparison
            var normalizedPath = NormalizePath(fullPath);

            // Check if the path starts with any of the allowed directories
            foreach (var allowedDir in allowedDirectories)
            {
                if (string.IsNullOrWhiteSpace(allowedDir))
                    continue;

                var normalizedAllowedDir = NormalizePath(Path.GetFullPath(allowedDir));
                
                // Ensure the allowed directory ends with a separator to prevent partial matches
                if (!normalizedAllowedDir.EndsWith(Path.DirectorySeparatorChar))
                    normalizedAllowedDir += Path.DirectorySeparatorChar;

                if (normalizedPath.StartsWith(normalizedAllowedDir, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            // If we can't resolve the path, it's not allowed
            Console.Error.WriteLine($"[PathValidator] Error validating path '{path}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Validates a project name to prevent directory traversal in project names
    /// </summary>
    public static string ValidateProjectName(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            throw new ArgumentException("Project name cannot be empty");

        // Remove any path separators or parent directory references
        var invalid = Path.GetInvalidFileNameChars()
            .Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' })
            .Concat(new[] { '.', ' ' }) // Also remove dots and spaces to prevent ".." and trailing spaces
            .Distinct()
            .ToArray();

        var sanitized = projectName;
        foreach (var c in invalid)
        {
            sanitized = sanitized.Replace(c, '_');
        }

        // Remove any sequences that could be interpreted as parent directory references
        while (sanitized.Contains("__"))
        {
            sanitized = sanitized.Replace("__", "_");
        }

        // Trim underscores from start and end
        sanitized = sanitized.Trim('_');

        if (string.IsNullOrWhiteSpace(sanitized))
            throw new ArgumentException("Project name contains only invalid characters");

        return sanitized.ToLowerInvariant();
    }

    /// <summary>
    /// Normalizes a path for consistent comparison
    /// </summary>
    private static string NormalizePath(string path)
    {
        // Replace all backslashes with forward slashes for consistent comparison
        return path.Replace('\\', Path.DirectorySeparatorChar)
                   .Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Gets a safe display path for error messages (doesn't reveal full path)
    /// </summary>
    public static string GetSafeDisplayPath(string path)
    {
        try
        {
            var fileName = Path.GetFileName(path);
            var dirName = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
            return string.IsNullOrEmpty(dirName) ? fileName : $".../{dirName}/{fileName}";
        }
        catch
        {
            return "[invalid path]";
        }
    }
}