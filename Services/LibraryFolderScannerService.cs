using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SLSKDONET.Services;

public class ScanProgress
{
    public int FilesDiscovered { get; set; }
    public int FilesImported { get; set; }
    public int FilesSkipped { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
}

public class ScanResult
{
    public int TotalFilesFound { get; set; }
    public int FilesImported { get; set; }
    public int FilesSkipped { get; set; }
    public List<Guid> ImportedLibraryEntryIds { get; set; } = new();
}

public class LibraryFolderScannerService
{
    private readonly ILogger<LibraryFolderScannerService> _logger;
    private readonly DatabaseService _databaseService;
    private readonly LibraryService _libraryService;
    
    private static readonly string[] SupportedExtensions = new[] { ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg" };

    public LibraryFolderScannerService(
        ILogger<LibraryFolderScannerService> logger,
        DatabaseService databaseService,
        LibraryService libraryService)
    {
        _logger = logger;
        _databaseService = databaseService;
        _libraryService = libraryService;
    }

    /// <summary>
    /// Scans a specific library folder and imports new audio files
    /// </summary>
    public async Task<ScanResult> ScanFolderAsync(Guid folderId, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        using var context = new AppDbContext();
        var folder = await context.LibraryFolders.FindAsync(new object[] { folderId }, ct);
        
        if (folder == null)
        {
            _logger.LogWarning("Folder {FolderId} not found", folderId);
            return new ScanResult();
        }

        if (!folder.IsEnabled)
        {
            _logger.LogInformation("Folder {Path} is disabled, skipping scan", folder.FolderPath);
            return new ScanResult();
        }

        if (!Directory.Exists(folder.FolderPath))
        {
            _logger.LogWarning("Folder path does not exist: {Path}", folder.FolderPath);
            return new ScanResult();
        }

        _logger.LogInformation("📁 Scanning folder: {Path}", folder.FolderPath);
        
        var result = new ScanResult();
        var scanProgress = new ScanProgress();
        
        try
        {
            // Discover all audio files
            var audioFiles = await DiscoverAudioFilesAsync(folder.FolderPath, ct);
            result.TotalFilesFound = audioFiles.Count;
            scanProgress.FilesDiscovered = audioFiles.Count;
            progress?.Report(scanProgress);
            
            _logger.LogInformation("Found {Count} audio files in {Path}", audioFiles.Count, folder.FolderPath);

            // Import each file
            foreach (var filePath in audioFiles)
            {
                if (ct.IsCancellationRequested) break;
                
                scanProgress.CurrentFile = Path.GetFileName(filePath);
                progress?.Report(scanProgress);
                
                try
                {
                    // Check if already imported
                    if (await IsFileAlreadyImportedAsync(filePath, ct))
                    {
                        result.FilesSkipped++;
                        scanProgress.FilesSkipped++;
                        continue;
                    }

                    // Import file
                    var libraryEntryId = await ImportFileToLibraryAsync(filePath, ct);
                    if (libraryEntryId != Guid.Empty)
                    {
                        result.FilesImported++;
                        result.ImportedLibraryEntryIds.Add(libraryEntryId);
                        scanProgress.FilesImported++;
                        progress?.Report(scanProgress);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to import file: {Path}", filePath);
                    result.FilesSkipped++;
                    scanProgress.FilesSkipped++;
                }
            }

            // Update folder metadata
            folder.LastScannedAt = DateTime.UtcNow;
            folder.TracksFound = result.TotalFilesFound;
            await context.SaveChangesAsync(ct);

            _logger.LogInformation("✅ Scan complete: {Imported} imported, {Skipped} skipped", 
                result.FilesImported, result.FilesSkipped);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning folder {Path}", folder.FolderPath);
        }

        return result;
    }

    /// <summary>
    /// Recursively discovers all audio files in a folder
    /// </summary>
    private async Task<List<string>> DiscoverAudioFilesAsync(string folderPath, CancellationToken ct)
    {
        var audioFiles = new List<string>();
        
        await Task.Run(() =>
        {
            try
            {
                var allFiles = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories);
                audioFiles.AddRange(allFiles.Where(f => 
                    SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())));
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Access denied to folder: {Path}", folderPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error discovering files in {Path}", folderPath);
            }
        }, ct);

        return audioFiles;
    }

    /// <summary>
    /// Checks if a file is already imported by path
    /// </summary>
    private async Task<bool> IsFileAlreadyImportedAsync(string filePath, CancellationToken ct)
    {
        using var context = new AppDbContext();
        
        // Check by FilePath first (most reliable)
        return await context.LibraryEntries
            .AnyAsync(e => e.FilePath == filePath, ct);
    }

    /// <summary>
    /// Imports a single audio file to the library
    /// </summary>
    private async Task<Guid> ImportFileToLibraryAsync(string filePath, CancellationToken ct)
    {
        try
        {
            // Read basic metadata from file using TagLib
            var fileInfo = new FileInfo(filePath);
            
            string artist = "Unknown Artist";
            string title = Path.GetFileNameWithoutExtension(filePath);
            string album = string.Empty;
            int? year = null;
            int duration = 0;

            try
            {
                var file = TagLib.File.Create(filePath);
                artist = string.IsNullOrWhiteSpace(file.Tag.FirstPerformer) ? artist : file.Tag.FirstPerformer;
                title = string.IsNullOrWhiteSpace(file.Tag.Title) ? title : file.Tag.Title;
                album = file.Tag.Album ?? string.Empty;
                year = (int?)file.Tag.Year;
                duration = (int)file.Properties.Duration.TotalSeconds;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read tags from {File}, using filename", filePath);
            }

            // Create library entry
            var entry = new LibraryEntryEntity
            {
                Id = Guid.NewGuid(),
                UniqueHash = Guid.NewGuid().ToString(), // Temporary, will be replaced with actual hash later
                Artist = artist,
                Title = title,
                Album = album,
                DurationSeconds = duration,
                FilePath = filePath,
                AddedAt = DateTime.UtcNow,
                Bitrate = 0, // Will be analyzed later
                Format = Path.GetExtension(filePath).TrimStart('.')
            };

            using var context = new AppDbContext();
            context.LibraryEntries.Add(entry);
            await context.SaveChangesAsync(ct);

            _logger.LogInformation("Imported: {Artist} - {Title}", artist, title);
            
            return entry.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import {File}", filePath);
            return Guid.Empty;
        }
    }

    /// <summary>
    /// Scans all enabled library folders
    /// </summary>
    public async Task<Dictionary<Guid, ScanResult>> ScanAllFoldersAsync(IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        using var context = new AppDbContext();
        var folders = await context.LibraryFolders
            .Where(f => f.IsEnabled)
            .ToListAsync(ct);

        _logger.LogInformation("Scanning {Count} enabled library folders", folders.Count);

        var results = new Dictionary<Guid, ScanResult>();

        foreach (var folder in folders)
        {
            if (ct.IsCancellationRequested) break;
            
            var result = await ScanFolderAsync(folder.Id, progress, ct);
            results[folder.Id] = result;
        }

        return results;
    }
}
