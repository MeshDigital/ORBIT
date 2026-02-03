using System;
using System.IO;
using System.Threading.Tasks;

namespace SLSKDONET.Services.Export
{
    /// <summary>
    /// Organizes export files into a professional folder structure.
    /// Creates USB-ready export packs for DJ use.
    /// </summary>
    public class ExportPackOrganizer
    {
        /// <summary>
        /// Creates a professional export pack folder structure.
        /// </summary>
        /// <param name="baseFolder">Base export directory</param>
        /// <param name="setName">Name of the set (used for folder naming)</param>
        /// <returns>Export pack paths</returns>
        public ExportPackPaths CreateExportPack(string baseFolder, string setName)
        {
            // Sanitize set name for folder creation
            string safeName = SanitizeFolderName(setName);
            
            // Create main export folder
            string exportRoot = Path.Combine(baseFolder, $"{safeName} Export");
            Directory.CreateDirectory(exportRoot);

            // Create subfolders
            string audioFolder = Path.Combine(exportRoot, "Audio");
            string originalFolder = Path.Combine(exportRoot, "Original");
            string editedFolder = Path.Combine(exportRoot, "Edited");
            
            Directory.CreateDirectory(audioFolder);
            Directory.CreateDirectory(originalFolder);
            Directory.CreateDirectory(editedFolder);

            return new ExportPackPaths
            {
                RootFolder = exportRoot,
                AudioFolder = audioFolder,
                OriginalFolder = originalFolder,
                EditedFolder = editedFolder,
                XmlPath = Path.Combine(exportRoot, $"{safeName}.xml")
            };
        }

        /// <summary>
        /// Sanitizes a folder name by removing invalid characters.
        /// </summary>
        private string SanitizeFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Untitled Set";

            // Remove invalid path characters
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }

            // Trim and limit length
            name = name.Trim();
            if (name.Length > 50)
            {
                name = name.Substring(0, 50);
            }

            return name;
        }

        /// <summary>
        /// Copies a file to the export pack with optional renaming.
        /// </summary>
        public async Task<string> CopyToExportPackAsync(
            string sourceFile, 
            string destinationFolder, 
            string? newFileName = null)
        {
            if (!File.Exists(sourceFile))
            {
                throw new FileNotFoundException($"Source file not found: {sourceFile}");
            }

            string fileName = newFileName ?? Path.GetFileName(sourceFile);
            string destinationPath = Path.Combine(destinationFolder, fileName);

            // Handle file name conflicts
            if (File.Exists(destinationPath))
            {
                string fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                string extension = Path.GetExtension(fileName);
                int counter = 1;

                while (File.Exists(destinationPath))
                {
                    fileName = $"{fileNameWithoutExt} ({counter}){extension}";
                    destinationPath = Path.Combine(destinationFolder, fileName);
                    counter++;
                }
            }

            await Task.Run(() => File.Copy(sourceFile, destinationPath, overwrite: false));
            return destinationPath;
        }

        /// <summary>
        /// Creates a README file in the export pack.
        /// </summary>
        public async Task CreateReadmeAsync(ExportPackPaths paths, string setName, int trackCount)
        {
            string readmePath = Path.Combine(paths.RootFolder, "README.txt");
            
            string content = $@"ORBIT Export Pack
==================

Set Name: {setName}
Tracks: {trackCount}
Exported: {DateTime.Now:yyyy-MM-dd HH:mm}

Folder Structure:
- Audio/Original: Original track files
- Audio/Edited: Surgically edited tracks (if any)
- {Path.GetFileName(paths.XmlPath)}: Rekordbox XML playlist

Import Instructions:
1. Copy this entire folder to your USB drive
2. Open Rekordbox
3. File → Import Playlist → Select {Path.GetFileName(paths.XmlPath)}
4. Rekordbox will import all tracks and cue points

ORBIT Intelligence:
- Structural cues (Intro, Drop, Breakdown, Outro) are set as Memory Cues
- Transition points are set as Hot Cues
- Flow health and transition reasoning are in track comments

For more information: https://github.com/MeshDigital/ORBIT
";

            await File.WriteAllTextAsync(readmePath, content);
        }
    }

    /// <summary>
    /// Paths for an export pack folder structure.
    /// </summary>
    public class ExportPackPaths
    {
        public string RootFolder { get; set; } = string.Empty;
        public string AudioFolder { get; set; } = string.Empty;
        public string OriginalFolder { get; set; } = string.Empty;
        public string EditedFolder { get; set; } = string.Empty;
        public string XmlPath { get; set; } = string.Empty;
    }
}
