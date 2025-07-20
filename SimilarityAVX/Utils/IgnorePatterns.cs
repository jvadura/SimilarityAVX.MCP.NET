using System;
using System.IO;

namespace CSharpMcpServer.Utils;

/// <summary>
/// Centralized ignore patterns for consistent file filtering across the application
/// </summary>
public static class IgnorePatterns
{
    /// <summary>
    /// Standard patterns for files and directories that should be ignored during indexing
    /// </summary>
    public static readonly string[] DefaultPatterns = 
    {
        // Build and cache directories
        "bin/", "obj/", ".vs/", "packages/", "TestResults/",
        
        // Binary and cache files
        "*.dll", "*.exe", "*.pdb", "*.cache", "*.user",
        
        // Version control and package managers
        ".git/", "node_modules/", "dist/", "build/",
        
        // Minified assets and IDE files
        "*.min.js", "*.min.css", "_ReSharper*/", "*.suo",
        
        // Database migrations (auto-generated)
        "Migrations/"
    };
    
    /// <summary>
    /// Check if a file should be ignored based on the default patterns
    /// </summary>
    /// <param name="filePath">Full path to the file</param>
    /// <param name="baseDirectory">Base directory for relative path calculation</param>
    /// <returns>True if the file should be ignored</returns>
    public static bool ShouldIgnore(string filePath, string baseDirectory)
    {
        // Normalize paths
        var relativePath = Path.GetRelativePath(baseDirectory, filePath).Replace('\\', '/');
        var normalizedPath = filePath.Replace('\\', '/');
        
        foreach (var pattern in DefaultPatterns)
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
        
        // Also ignore very large files (>1MB)
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > 1024 * 1024)
            {
                Console.Error.WriteLine($"[IgnorePatterns] Ignoring large file: {filePath} ({fileInfo.Length / 1024}KB)");
                return true;
            }
        }
        catch { }
        
        return false;
    }
}