using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace CSharpMcpServer.Utils;

/// <summary>
/// Converts WSL paths to Windows paths when running on Windows.
/// This enables the MCP server to work correctly when running on Windows
/// while Claude Desktop provides WSL paths.
/// </summary>
public static class PathConverter
{
    /// <summary>
    /// Converts a WSL path like /mnt/c/folder to Windows path like C:\folder
    /// Only performs conversion when running on Windows OS.
    /// </summary>
    public static string ConvertPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        // Only convert if we're running on Windows and the path looks like a WSL path
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && path.StartsWith("/mnt/"))
        {
            // WSL paths are in format: /mnt/[drive_letter]/path/to/file
            // We need at least "/mnt/x/" to be valid (7 characters)
            if (path.Length > 6 && path[6] == '/')
            {
                // Extract the drive letter (position 5)
                var driveLetter = char.ToUpper(path[5]);
                
                // Validate it's a valid drive letter
                if (driveLetter >= 'A' && driveLetter <= 'Z')
                {
                    // Get the remaining path after /mnt/x/
                    var remainingPath = path.Substring(7);
                    
                    // Replace forward slashes with backslashes
                    remainingPath = remainingPath.Replace('/', '\\');
                    
                    // Construct the Windows path
                    var windowsPath = $"{driveLetter}:\\{remainingPath}";
                    
                    Console.Error.WriteLine($"[PathConverter] Converted WSL path: {path} -> {windowsPath}");
                    return windowsPath;
                }
            }
        }
        
        // Return original path if not on Windows or not a WSL path
        return path;
    }
    
    /// <summary>
    /// Converts multiple paths at once
    /// </summary>
    public static string[] ConvertPaths(params string[] paths)
    {
        return paths.Select(ConvertPath).ToArray();
    }
    
    /// <summary>
    /// Checks if we're running on Windows
    /// </summary>
    public static bool IsRunningOnWindows()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    }
}