using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Utils;

namespace SLSKDONET.Services;

/// <summary>
/// Exports a PlaylistJob to Rekordbox-compatible XML format.
/// Uses PlaylistTrack entries to include both downloaded and missing tracks.
/// Each track's ResolvedFilePath is used (either actual or expected path).
/// </summary>
public class RekordboxXmlExporter
{
    private readonly ILogger<RekordboxXmlExporter> _logger;
    private readonly ILibraryService _libraryService;
    private readonly Musical.CueGenerationEngine _cueEngine;

    public RekordboxXmlExporter(
        ILogger<RekordboxXmlExporter> logger, 
        ILibraryService libraryService,
        Musical.CueGenerationEngine cueEngine)
    {
        _logger = logger;
        _libraryService = libraryService;
        _cueEngine = cueEngine;
    }

    /// <summary>
    /// Exports a PlaylistJob to Rekordbox XML format.
    /// Loads PlaylistTrack entries from persistent storage and uses their ResolvedFilePath.
    /// </summary>
    public async Task ExportAsync(PlaylistJob job, string exportPath)
    {
        try
        {
            _logger.LogInformation("Exporting playlist '{PlaylistName}' to Rekordbox XML: {ExportPath}", 
                job.SourceTitle, exportPath);

            // Load PlaylistTrack entries for this playlist
            var playlistTracks = await _libraryService.LoadPlaylistTracksAsync(job.Id);
            
            if (!playlistTracks.Any())
            {
                _logger.LogWarning("No tracks found for playlist {PlaylistId}", job.Id);
                return;
            }

            // Create root XML structure
            var doc = new XDocument(
                new XElement("DJ_PLAYLISTS",
                    new XAttribute("Version", "1.0.0"),
                    new XElement("PRODUCT", 
                        new XAttribute("Name", "SLSK.NET"), 
                        new XAttribute("Version", "1.0.0")),
                    new XElement("COLLECTION")
                )
            );

            var collection = doc.Root?.Element("COLLECTION");
            if (collection == null)
                throw new InvalidOperationException("Failed to create COLLECTION element");

            var trackIdCounter = 1;

            using var db = new Data.AppDbContext();

            // Add each PlaylistTrack to the collection
            foreach (var track in playlistTracks)
            {
                // Skip tracks without a ResolvedFilePath (shouldn't happen, but be safe)
                if (string.IsNullOrEmpty(track.ResolvedFilePath))
                {
                    _logger.LogDebug("Skipping track without ResolvedFilePath: {Artist} - {Title}", 
                        track.Artist, track.Title);
                    continue;
                }

                // Convert file path to Rekordbox URL format
                var locationUrl = FileFormattingUtils.ToRekordboxUrl(track.ResolvedFilePath);

                var trackEntry = new XElement("TRACK",
                    new XAttribute("TrackID", trackIdCounter++),
                    new XAttribute("Name", track.Title ?? "Unknown"),
                    new XAttribute("Artist", track.Artist ?? "Unknown"),
                    new XAttribute("Album", track.Album ?? "Unknown"),
                    new XAttribute("Genre", "SLSK"),
                    new XAttribute("Location", locationUrl)
                );

                // Add optional metadata if available
                if (track.TrackNumber > 0)
                    trackEntry.Add(new XAttribute("TrackNumber", track.TrackNumber));
                
                // [NEW] Phase 17/25: AI Metadata Injection
                var features = await db.AudioFeatures.FirstOrDefaultAsync(f => f.TrackUniqueHash == track.TrackUniqueHash);
                if (features != null)
                {
                    trackEntry.Add(new XAttribute("InitialKey", features.CamelotKey ?? ""));
                    trackEntry.Add(new XAttribute("AverageBpm", features.Bpm));
                    
                    int energy = features.EnergyScore > 0 
                        ? features.EnergyScore 
                        : (int)Math.Clamp(Math.Round(features.Energy * 10), 1, 10);
                    
                    trackEntry.Add(new XAttribute("Comments", $"[ORBIT] Energy {energy} | {track.Comments}"));

                    // Inject Structural Cues
                    var cues = await _cueEngine.GenerateMikStandardCues(track.TrackUniqueHash);
                    int cueIdx = 0;
                    foreach (var cue in cues)
                    {
                        var mark = new XElement("POSITION_MARK",
                            new XAttribute("Name", cue.Name ?? ""),
                            new XAttribute("Type", "0"),
                            new XAttribute("Start", cue.Timestamp.ToString("F3").Replace(",", ".")),
                            new XAttribute("Num", cueIdx)); // Map to Hot Cue index (0-7)

                        // Convert Hex to RGB for Rekordbox
                        if (!string.IsNullOrEmpty(cue.Color))
                        {
                            try {
                                var col = Avalonia.Media.Color.Parse(cue.Color);
                                mark.Add(new XAttribute("Red", col.R));
                                mark.Add(new XAttribute("Green", col.G));
                                mark.Add(new XAttribute("Blue", col.B));
                            } catch { }
                        }
                        trackEntry.Add(mark);
                        cueIdx++;
                        if (cueIdx >= 8) break; // Rekordbox generally supports 8 hot cues
                    }
                }

                trackEntry.Add(new XAttribute("Status", track.Status.ToString()));

                collection.Add(trackEntry);
            }

            // Create Playlist structure (optional but common)
            var playlistNode = new XElement("PLAYLISTS",
                new XElement("NODE",
                    new XAttribute("Name", "ROOT"),
                    new XAttribute("Type", "root"),
                    new XElement("NODE",
                        new XAttribute("Name", job.SourceTitle),
                        new XAttribute("Type", "playlist"),
                        playlistTracks
                            .Where(t => !string.IsNullOrEmpty(t.ResolvedFilePath))
                            .Select((t, idx) => new XElement("TRACK", 
                                new XAttribute("Key", idx + 1)))
                    )
                )
            );
            doc.Root?.Add(playlistNode);

            // Write to file
            await File.WriteAllTextAsync(exportPath, doc.ToString());

            _logger.LogInformation("Successfully exported {Count} tracks to {ExportPath}", 
                playlistTracks.Count, exportPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export playlist to Rekordbox XML");
            throw;
        }
    }
}
