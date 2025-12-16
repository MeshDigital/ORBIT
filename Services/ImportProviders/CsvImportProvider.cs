using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services.InputParsers;

namespace SLSKDONET.Services.ImportProviders;

/// <summary>
/// Import provider for CSV files.
/// </summary>
public class CsvImportProvider : IImportProvider
{
    private readonly ILogger<CsvImportProvider> _logger;
    private readonly CsvInputSource _csvInputSource;
    private readonly ISpotifyMetadataService _metadataService;

    public string Name => "CSV";
    public string IconGlyph => "📄";

    public CsvImportProvider(
        ILogger<CsvImportProvider> logger,
        CsvInputSource csvInputSource,
        ISpotifyMetadataService metadataService)
    {
        _logger = logger;
        _csvInputSource = csvInputSource;
        _metadataService = metadataService;
    }

    public bool CanHandle(string input)
    {
        return !string.IsNullOrWhiteSpace(input) && 
               (input.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || 
                File.Exists(input));
    }

    public async Task<ImportResult> ImportAsync(string filePath)
    {
        try
        {
            _logger.LogInformation("Importing from CSV: {FilePath}", filePath);

            if (!File.Exists(filePath))
            {
                return new ImportResult
                {
                    Success = false,
                    ErrorMessage = $"File not found: {filePath}"
                };
            }

            var tracks = await _csvInputSource.ParseAsync(filePath);

            if (!tracks.Any())
            {
                return new ImportResult
                {
                    Success = false,
                    ErrorMessage = "No tracks found in the CSV file"
                };
            }

            // Extract a meaningful title from the filename
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var sourceTitle = string.IsNullOrWhiteSpace(fileName) ? "CSV Import" : fileName;

            _logger.LogInformation("Successfully imported {Count} tracks from CSV file '{Title}'", 
                tracks.Count, sourceTitle);

            // Phase 0: Enrich tracks with Spotify metadata
            try
            {
                _logger.LogInformation("Fetching Spotify metadata for {Count} tracks...", tracks.Count);
                
                // Convert SearchQuery to PlaylistTrack for enrichment
                var playlistTracks = tracks.Select(q => new PlaylistTrack
                {
                    Artist = q.Artist ?? string.Empty,
                    Title = q.Title ?? string.Empty,
                    Album = q.Album ?? string.Empty
                }).ToList();

                var enrichedCount = await _metadataService.EnrichTracksAsync(playlistTracks);
                
                // Copy metadata back to SearchQuery objects
                for (int i = 0; i < tracks.Count && i < playlistTracks.Count; i++)
                {
                    var track = playlistTracks[i];
                    tracks[i].SpotifyTrackId = track.SpotifyTrackId;
                    tracks[i].SpotifyAlbumId = track.SpotifyAlbumId;
                    tracks[i].SpotifyArtistId = track.SpotifyArtistId;
                    tracks[i].AlbumArtUrl = track.AlbumArtUrl;
                    tracks[i].Genres = track.Genres;
                    tracks[i].Popularity = track.Popularity;
                    tracks[i].CanonicalDuration = track.CanonicalDuration;
                    tracks[i].ReleaseDate = track.ReleaseDate;
                }

                _logger.LogInformation("Enriched {EnrichedCount}/{TotalCount} tracks with Spotify metadata", 
                    enrichedCount, tracks.Count);
            }
            catch (Exception ex)
            {
                // Don't fail the import if metadata enrichment fails
                _logger.LogWarning(ex, "Failed to enrich tracks with Spotify metadata, continuing with import");
            }

            return new ImportResult
            {
                Success = true,
                SourceTitle = sourceTitle,
                Tracks = tracks
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import from CSV: {FilePath}", filePath);
            return new ImportResult
            {
                Success = false,
                ErrorMessage = $"CSV import failed: {ex.Message}"
            };
        }
    }
}
