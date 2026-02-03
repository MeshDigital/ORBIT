using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SLSKDONET.Services.Export
{
    /// <summary>
    /// Normalizes file paths for cross-platform Rekordbox compatibility.
    /// Handles Windows/macOS path differences and URI encoding.
    /// </summary>
    public static class PathNormalizer
    {
        /// <summary>
        /// Converts a file path to Rekordbox-compatible URI format.
        /// Windows: file://localhost/C:/path/to/file.wav
        /// macOS: file://localhost/Users/path/to/file.wav
        /// </summary>
        public static string ToRekordboxUri(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return string.Empty;

            // Ensure absolute path
            string absolutePath = Path.GetFullPath(filePath);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows: C:\path\to\file.wav → file://localhost/C:/path/to/file.wav
                string normalized = absolutePath.Replace('\\', '/');
                // Escape characters like spaces but keep slashes and colons
                string escaped = Uri.EscapeUriString(normalized);
                return $"file://localhost/{escaped}";
            }
            else
            {
                // macOS/Linux: /Users/path/to/file.wav → file://localhost/Users/path/to/file.wav
                string escaped = Uri.EscapeUriString(absolutePath);
                return $"file://localhost{escaped}";
            }
        }

        /// <summary>
        /// Converts a Rekordbox URI back to a local file path.
        /// </summary>
        public static string FromRekordboxUri(string uri)
        {
            if (string.IsNullOrEmpty(uri))
                return string.Empty;

            // Remove file://localhost/ prefix
            string path = uri.Replace("file://localhost/", "");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Convert forward slashes back to backslashes
                return path.Replace('/', '\\');
            }
            else
            {
                // macOS/Linux: add leading slash if missing
                return path.StartsWith("/") ? path : "/" + path;
            }
        }

        /// <summary>
        /// Makes a path relative to a base directory (for portable exports).
        /// </summary>
        public static string MakeRelativePath(string basePath, string targetPath)
        {
            if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(targetPath))
                return targetPath;

            Uri baseUri = new Uri(Path.GetFullPath(basePath) + Path.DirectorySeparatorChar);
            Uri targetUri = new Uri(Path.GetFullPath(targetPath));

            Uri relativeUri = baseUri.MakeRelativeUri(targetUri);
            string relativePath = Uri.UnescapeDataString(relativeUri.ToString());

            // Convert forward slashes to platform-specific separator
            return relativePath.Replace('/', Path.DirectorySeparatorChar);
        }

        /// <summary>
        /// Validates that a file path is safe for export.
        /// </summary>
        public static bool IsValidExportPath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            try
            {
                // Check for invalid characters
                string fileName = Path.GetFileName(filePath);
                if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    return false;

                // Check path length (Windows has 260 char limit)
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && filePath.Length > 260)
                    return false;

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
