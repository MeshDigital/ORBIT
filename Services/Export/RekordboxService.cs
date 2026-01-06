using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data; // Entity namespace
using SLSKDONET.Models;
using SLSKDONET.Services.Models.Export;
using SLSKDONET.Services.IO; // For FileVerificationHelper
using SLSKDONET.Utils; // For KeyConverter
using System.Text.Json;

namespace SLSKDONET.Services.Export;

/// <summary>
/// Pro DJ Service: Handles export of library content to Rekordbox XML format.
/// Features:
/// - Streaming XML generation (Memory Efficient)
/// - URI Normalization (file://localhost/)
/// - Key Conversion (Standard -> Camelot)
/// - "Monthly Drop" logic
/// </summary>
public class RekordboxService
{
    private readonly ILogger<RekordboxService> _logger;
    private readonly ILibraryService _libraryService;
    private readonly DatabaseService _databaseService;

    public RekordboxService(
        ILogger<RekordboxService> logger, 
        ILibraryService libraryService,
        DatabaseService databaseService)
    {
        _logger = logger;
        _libraryService = libraryService;
        _databaseService = databaseService;
    }

    /// <summary>
    /// Exports a specific playlist to a Rekordbox XML file.
    /// </summary>
    public async Task<int> ExportPlaylistAsync(PlaylistJob job, string outputPath)
    {
        _logger.LogInformation("Starting Rekordbox export for playlist: {PlaylistName}", job.SourceTitle);

        var tracks = await _libraryService.LoadPlaylistTracksAsync(job.Id);
        
        // Filter: Only tracks with ResolvedPaths
        var validTracks = tracks
            .Where(t => !string.IsNullOrEmpty(t.ResolvedFilePath))
            .ToList();

        if (validTracks.Count == 0)
        {
            _logger.LogWarning("No valid tracks found to export.");
            return 0;
        }

        return await WriteXmlAsync(validTracks, job.SourceTitle, outputPath);
    }

    /// <summary>
    /// Exports "Monthly Drop" - Tracks added in the last N days.
    /// </summary>
    public async Task<int> ExportMonthlyDropAsync(int days, string outputPath)
    {
        _logger.LogInformation("Starting Monthly Drop export (Last {Days} days)", days);

        var cutoff = DateTime.UtcNow.AddDays(-days);
        
        // We need to fetch LibraryEntries from DB
        // Assuming DatabaseService has a method or we add one.
        // For now, let's query via DatabaseService helper if possible, 
        // or use a direct context if exposed (It's not usually).
        // EDIT: DatabaseService usually wraps context. 
        // Let's assume we can fetch all and filter, or add a specific query method later.
        
        // WORKAROUND: For this iteration, we'll fetch all library entries and filter in memory
        // (Not ideal for 100k tracks, but fine for v1).
        var allEntries = await _databaseService.GetAllLibraryEntriesAsync();
        var recentEntries = allEntries
            .Where(e => e.AddedAt >= cutoff && !string.IsNullOrEmpty(e.FilePath))
            .ToList();

        return await WriteLibraryEntriesXmlAsync(recentEntries, $"Orbit Drop {DateTime.Now:MMM yyyy}", outputPath);
    }

    private async Task<int> WriteXmlAsync(List<PlaylistTrack> tracks, string collectionName, string outputPath)
    {
        // Convert PlaylistTracks to RekordboxTracks
        var exportTracks = new List<RekordboxTrack>();
        int idCounter = 1;

        foreach (var t in tracks)
        {
            // Verify file exists
            if (!File.Exists(t.ResolvedFilePath)) continue;

            var fileInfo = new FileInfo(t.ResolvedFilePath);
            
            // Normalize Key
            string finalKey = KeyConverter.ToCamelot(t.MusicalKey); // Prefer analyzed key
            if (string.IsNullOrEmpty(finalKey) && !string.IsNullOrEmpty(t.SpotifyKey))
            {
                finalKey = KeyConverter.ToCamelot(t.SpotifyKey);
            }
            if (string.IsNullOrEmpty(finalKey) && !string.IsNullOrEmpty(t.ManualKey))
            {
                finalKey = KeyConverter.ToCamelot(t.ManualKey);
            }

            var rTrack = new RekordboxTrack
            {
                TrackID = idCounter++,
                Name = XmlSanitizer.Sanitize(t.Title) ?? "Unknown",
                Artist = XmlSanitizer.Sanitize(t.Artist) ?? "Unknown",
                Album = XmlSanitizer.Sanitize(t.Album) ?? "Unknown",
                Genre = "SLSK", // Could use t.Genres if parsed
                TotalTime = 0, 
                Size = fileInfo.Length,
                DateAdded = t.AddedAt.ToString("yyyy-MM-dd"),
                BitRate = t.Bitrate ?? 0,
                AverageBpm = t.BPM ?? (t.SpotifyBPM ?? 0),
                Tonality = finalKey,
                Location = FormatPathForRekordbox(t.ResolvedFilePath)
            };

            if (!string.IsNullOrEmpty(t.CuePointsJson))
            {
                try { rTrack.CuePoints = System.Text.Json.JsonSerializer.Deserialize<List<OrbitCue>>(t.CuePointsJson) ?? new(); } catch { }
            }

            exportTracks.Add(rTrack);
        }

        return await GenerateXmlFile(exportTracks, collectionName, outputPath);
    }
    
    // Overload for LibraryEntryEntity (Monthly Drop)
    private async Task<int> WriteLibraryEntriesXmlAsync(List<LibraryEntryEntity> entries, string collectionName, string outputPath)
    {
        var exportTracks = new List<RekordboxTrack>();
        int idCounter = 1;

        foreach (var e in entries)
        {
            if (!File.Exists(e.FilePath)) continue;
            var fileInfo = new FileInfo(e.FilePath);

            // Normalize Key logic
            string finalKey = KeyConverter.ToCamelot(e.MusicalKey);
            if (string.IsNullOrEmpty(finalKey)) finalKey = KeyConverter.ToCamelot(e.SpotifyKey);

            var rTrack = new RekordboxTrack
            {
                TrackID = idCounter++,
                Name = XmlSanitizer.Sanitize(e.Title),
                Artist = XmlSanitizer.Sanitize(e.Artist),
                Album = XmlSanitizer.Sanitize(e.Album),
                Genre = "SLSK",
                TotalTime = e.DurationSeconds ?? 0,
                Size = fileInfo.Length,
                DateAdded = e.AddedAt.ToString("yyyy-MM-dd"),
                BitRate = e.Bitrate,
                AverageBpm = e.BPM ?? (e.SpotifyBPM ?? 0),
                Tonality = finalKey,
                Location = FormatPathForRekordbox(e.FilePath)
            };

            if (!string.IsNullOrEmpty(e.CuePointsJson))
            {
                try { rTrack.CuePoints = System.Text.Json.JsonSerializer.Deserialize<List<OrbitCue>>(e.CuePointsJson) ?? new(); } catch { }
            }

            exportTracks.Add(rTrack);
        }

        return await GenerateXmlFile(exportTracks, collectionName, outputPath);
    }

    private async Task<int> GenerateXmlFile(List<RekordboxTrack> tracks, string playlistName, string outputPath)
    {
        // ATOMIC WRITE PATTERN: Write to temp file, then atomic rename
        // This prevents corruption if Rekordbox reads while we're writing
        var tempPath = outputPath + ".tmp";
        
        try
        {
            var settings = new XmlWriterSettings
            {
                Async = true,
                Indent = true,
                Encoding = Encoding.UTF8
            };

            await using var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
            await using var writer = XmlWriter.Create(stream, settings);

            await writer.WriteStartDocumentAsync();
            
            // <DJ_PLAYLISTS Version="1.0.0">
            await writer.WriteStartElementAsync(null, "DJ_PLAYLISTS", null);
            await writer.WriteAttributeStringAsync(null, "Version", null, "1.0.0");
            
            // <PRODUCT Name="SLSK.NET" Version="1.0.0" />
            await writer.WriteStartElementAsync(null, "PRODUCT", null);
            await writer.WriteAttributeStringAsync(null, "Name", null, "ORBIT");
            await writer.WriteAttributeStringAsync(null, "Version", null, "1.0.0");
            await writer.WriteEndElementAsync(); // PRODUCT

            // <COLLECTION Entries="N">
            await writer.WriteStartElementAsync(null, "COLLECTION", null);
            await writer.WriteAttributeStringAsync(null, "Entries", null, tracks.Count.ToString());

            foreach (var t in tracks)
            {
                await writer.WriteStartElementAsync(null, "TRACK", null);
                await writer.WriteAttributeStringAsync(null, "TrackID", null, t.TrackID.ToString());
                await writer.WriteAttributeStringAsync(null, "Name", null, t.Name);
                await writer.WriteAttributeStringAsync(null, "Artist", null, t.Artist);
                await writer.WriteAttributeStringAsync(null, "Album", null, t.Album);
                await writer.WriteAttributeStringAsync(null, "Genre", null, t.Genre);
                await writer.WriteAttributeStringAsync(null, "Kind", null, t.Kind);
                await writer.WriteAttributeStringAsync(null, "Size", null, t.Size.ToString());
                await writer.WriteAttributeStringAsync(null, "TotalTime", null, t.TotalTime.ToString());
                await writer.WriteAttributeStringAsync(null, "DateAdded", null, t.DateAdded);
                await writer.WriteAttributeStringAsync(null, "BitRate", null, t.BitRate.ToString());
                await writer.WriteAttributeStringAsync(null, "SampleRate", null, t.SampleRate.ToString());
                
                if (t.AverageBpm > 0)
                    await writer.WriteAttributeStringAsync(null, "AverageBpm", null, t.AverageBpm.ToString(CultureInfo.InvariantCulture));
                
                if (!string.IsNullOrEmpty(t.Tonality))
                    await writer.WriteAttributeStringAsync(null, "Tonality", null, t.Tonality);
                    
                await writer.WriteAttributeStringAsync(null, "Location", null, t.Location);
                
                // Export Cue Points (Phase 11.5)
                foreach (var cue in t.CuePoints)
                {
                    await writer.WriteStartElementAsync(null, "POSITION_MARK", null);
                    await writer.WriteAttributeStringAsync(null, "Name", null, XmlSanitizer.Sanitize(cue.Name));
                    await writer.WriteAttributeStringAsync(null, "Type", null, "0"); // 0 = Hot Cue
                    await writer.WriteAttributeStringAsync(null, "Start", null, cue.Timestamp.ToString("F3", CultureInfo.InvariantCulture));
                    await writer.WriteAttributeStringAsync(null, "Num", null, cue.SlotIndex >= 0 ? cue.SlotIndex.ToString() : "-1");
                    await writer.WriteEndElementAsync(); // POSITION_MARK
                }
                
                await writer.WriteEndElementAsync(); // TRACK
            }

            await writer.WriteEndElementAsync(); // COLLECTION

            // <PLAYLISTS> (Optional: Create a playlist node for this specific export)
            await writer.WriteStartElementAsync(null, "PLAYLISTS", null);
            await writer.WriteStartElementAsync(null, "NODE", null);
            await writer.WriteAttributeStringAsync(null, "Type", null, "0"); // 0=Root/Folder
            await writer.WriteAttributeStringAsync(null, "Name", null, "ROOT");
            
            // Actual Playlist
            await writer.WriteStartElementAsync(null, "NODE", null);
            await writer.WriteAttributeStringAsync(null, "Name", null, playlistName);
            await writer.WriteAttributeStringAsync(null, "Type", null, "1"); // 1=Playlist
            await writer.WriteAttributeStringAsync(null, "KeyType", null, "0");
            await writer.WriteAttributeStringAsync(null, "Entries", null, tracks.Count.ToString());

            for (int i = 0; i < tracks.Count; i++)
            {
                await writer.WriteStartElementAsync(null, "TRACK", null);
                await writer.WriteAttributeStringAsync(null, "Key", null, tracks[i].TrackID.ToString());
                await writer.WriteEndElementAsync(); // TRACK
            }

            await writer.WriteEndElementAsync(); // NODE (Playlist)
            await writer.WriteEndElementAsync(); // NODE (Root)
            await writer.WriteEndElementAsync(); // PLAYLISTS

            await writer.WriteEndElementAsync(); // DJ_PLAYLISTS
            await writer.WriteEndDocumentAsync();
            
            // Close streams before moving
            await writer.FlushAsync();
            
            // ATOMIC OPERATION: Rename temp file to final destination
            // This is an OS-level atomic operation that prevents corruption
            File.Move(tempPath, outputPath, overwrite: true);
            
            _logger.LogInformation("Atomic XML write completed: {OutputPath}", outputPath);
            
            return tracks.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate Rekordbox XML file");
            
            // Cleanup temp file if it exists
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* Best effort cleanup */ }
            }
            
            throw;
        }
    }

    /// <summary>
    /// Transforms local path to Rekordbox-compatible URI.
    /// Pattern: file://localhost/C:/Path/To/File.mp3 (URL Encoded)
    /// </summary>
    private string FormatPathForRekordbox(string localPath)
    {
        // 1. Convert to Uri to get proper encoding
        var uri = new Uri(localPath);
        var absoluteUri = uri.AbsoluteUri;

        // 2. Replace local file header with localhost version
        // Standard: file:///C:/Music...
        // Rekordbox: file://localhost/C:/Music...
        return absoluteUri.Replace("file:///", "file://localhost/");
    }
}
