using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Export;

public class HardwareExportService : IHardwareExportService
{
    private readonly ILogger<HardwareExportService> _logger;
    private readonly RekordboxXmlExporter _rekordboxExporter;

    public event EventHandler<ExportProgressEventArgs>? ProgressChanged;

    public HardwareExportService(ILogger<HardwareExportService> logger, RekordboxXmlExporter rekordboxExporter)
    {
        _logger = logger;
        _rekordboxExporter = rekordboxExporter;
    }


    public IEnumerable<ExportDriveInfo> GetAvailableDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => new ExportDriveInfo
                {
                    Name = string.IsNullOrEmpty(d.VolumeLabel) ? d.Name : d.VolumeLabel,
                    RootPath = d.RootDirectory.FullName,
                    FreeSpaceBytes = d.AvailableFreeSpace,
                    Format = d.DriveFormat,
                    IsRemovable = d.DriveType == DriveType.Removable
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get available drives");
            return Enumerable.Empty<ExportDriveInfo>();
        }
    }

    public async Task ExportProjectAsync(
        PlaylistJob project, 
        ExportDriveInfo targetDrive, 
        HardwarePlatform platform, 
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting export of project '{Project}' to {Drive} (Platform: {Platform})", 
            project.SourceTitle, targetDrive.RootPath, platform);

        var tracks = project.PlaylistTracks.Where(t => t.Status == TrackStatus.Downloaded).ToList();
        if (tracks.Count == 0)
        {
            _logger.LogWarning("No completed tracks to export");
            return;
        }

        string musicFolder = platform switch
        {
            HardwarePlatform.Pioneer => Path.Combine(targetDrive.RootPath, "Contents"),
            HardwarePlatform.Denon => Path.Combine(targetDrive.RootPath, "Music"),
            _ => Path.Combine(targetDrive.RootPath, "Exported Music")
        };

        if (!Directory.Exists(musicFolder))
            Directory.CreateDirectory(musicFolder);

        int count = 0;
        foreach (var track in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;

            try
            {
                if (string.IsNullOrEmpty(track.ResolvedFilePath) || !File.Exists(track.ResolvedFilePath))
                {
                    _logger.LogWarning("Track file missing: {Title}", track.Title);
                    continue;
                }

                // 1. Sanitize folder names
                string artist = SanitizeForFat32(track.Artist ?? "Unknown Artist");
                string album = SanitizeForFat32(track.Album ?? "Unknown Album");
                string fileName = SanitizeForFat32(Path.GetFileName(track.ResolvedFilePath));

                string targetDir = Path.Combine(musicFolder, artist, album);
                if (!Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);

                string targetPath = Path.Combine(targetDir, fileName);

                // 2. Report Progress
                ProgressChanged?.Invoke(this, new ExportProgressEventArgs
                {
                    CurrentTrack = count,
                    TotalTracks = tracks.Count,
                    Status = $"Copying: {track.Artist} - {track.Title}"
                });

                // 3. Copy with sync logic (if file already exists and size matches, skip)
                bool skip = false;
                if (File.Exists(targetPath))
                {
                    var sourceInfo = new FileInfo(track.ResolvedFilePath);
                    var targetInfo = new FileInfo(targetPath);
                    if (sourceInfo.Length == targetInfo.Length)
                        skip = true;
                }

                if (!skip)
                {
                    // Copy file
                    using (var sourceStream = new FileStream(track.ResolvedFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
                    using (var destinationStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
                    {
                        await sourceStream.CopyToAsync(destinationStream, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to copy track: {Title}", track.Title);
            }
        }

        // 4. Platform-specific metadata export
        if (platform == HardwarePlatform.Pioneer)
        {
            ProgressChanged?.Invoke(this, new ExportProgressEventArgs
            {
                CurrentTrack = tracks.Count,
                TotalTracks = tracks.Count,
                Status = "Generating Rekordbox XML..."
            });
            
            // Generate Rekordbox XML on the drive root
            string xmlPath = Path.Combine(targetDrive.RootPath, "rekordbox_export.xml");
            
            // Map original paths to final paths on the USB
            await _rekordboxExporter.ExportAsync(project, xmlPath, originalPath => 
            {
                var track = tracks.FirstOrDefault(t => t.ResolvedFilePath == originalPath);
                if (track == null) return originalPath;

                string artist = SanitizeForFat32(track.Artist ?? "Unknown Artist");
                string album = SanitizeForFat32(track.Album ?? "Unknown Album");
                string fileName = SanitizeForFat32(Path.GetFileName(track.ResolvedFilePath));

                // Path on the hardware: /Contents/Artist/Album/File.mp3
                // We use '/' for Rekordbox cross-platform compatibility
                return $"/Contents/{artist}/{album}/{fileName}";
            });

            _logger.LogInformation("Rekordbox XML generated at {Path}", xmlPath);
        }

        else if (platform == HardwarePlatform.Denon)
        {
             _logger.LogInformation("Denon Engine database generation not yet implemented. Tracks copied to /Music/");
        }

        ProgressChanged?.Invoke(this, new ExportProgressEventArgs
        {
            CurrentTrack = tracks.Count,
            TotalTracks = tracks.Count,
            Status = "Export Complete!"
        });
    }

    private string SanitizeForFat32(string input)
    {
        if (string.IsNullOrEmpty(input)) return "Unknown";
        
        // Invalid FAT32 characters: \ / : * ? " < > |
        char[] invalidChars = new char[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
        
        string sanitized = input;
        foreach (char c in invalidChars)
        {
            sanitized = sanitized.Replace(c, '_');
        }

        // Limit length to 100 for path safety
        if (sanitized.Length > 100)
            sanitized = sanitized.Substring(0, 100);

        return sanitized.Trim();
    }
}
