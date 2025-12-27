using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Services.Musical;

namespace SLSKDONET.Services.Musical;

/// <summary>
/// Phase 4.2: Manual Cue Generation Service.
/// Provides user-initiated batch processing for DJ cue point generation.
/// </summary>
public class ManualCueGenerationService
{
    private readonly ILogger<ManualCueGenerationService> _logger;
    private readonly IAudioIntelligenceService _essentiaService;
    private readonly DropDetectionEngine _dropEngine;
    private readonly CueGenerationEngine _cueEngine;

    public ManualCueGenerationService(
        ILogger<ManualCueGenerationService> logger,
        IAudioIntelligenceService essentiaService,
        DropDetectionEngine dropEngine,
        CueGenerationEngine cueEngine)
    {
        _logger = logger;
        _essentiaService = essentiaService;
        _dropEngine = dropEngine;
        _cueEngine = cueEngine;
    }

    /// <summary>
    /// Generates cues for all tracks in a playlist.
    /// Safe to call multiple times - only processes tracks without existing cues.
    /// </summary>
    public async Task<CueGenerationResult> GenerateCuesForPlaylistAsync(Guid playlistId, IProgress<int>? progress = null)
    {
        var result = new CueGenerationResult();
        
        using var db = new AppDbContext();
        
        // Get all tracks in playlist
        var tracks = await db.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId && pt.ResolvedFilePath != null)
            .ToListAsync();

        if (tracks.Count == 0)
        {
            _logger.LogWarning("No tracks found in playlist {PlaylistId}", playlistId);
            return result;
        }

        result.TotalTracks = tracks.Count;
        _logger.LogInformation("Starting cue generation for {Count} tracks in playlist {PlaylistId}", 
            tracks.Count, playlistId);

        for (int i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            
            try
            {
                // Check if cues already exist
                var existingFeatures = await db.AudioFeatures
                    .FirstOrDefaultAsync(f => f.TrackUniqueHash == track.TrackUniqueHash);

                if (existingFeatures?.DropTimeSeconds != null)
                {
                    _logger.LogDebug("Track {Title} already has cues - skipping", track.Title);
                    result.Skipped++;
                    continue;
                }

                // Trigger analysis with cue generation enabled
                if (!string.IsNullOrEmpty(track.ResolvedFilePath) && System.IO.File.Exists(track.ResolvedFilePath))
                {
                    var features = await _essentiaService.AnalyzeTrackAsync(
                        track.ResolvedFilePath, 
                        track.TrackUniqueHash, 
                        generateCues: true);

                    if (features != null && features.DropTimeSeconds.HasValue)
                    {
                        // Save to DB
                        db.AudioFeatures.Add(features);
                        await db.SaveChangesAsync();
                        
                        result.Success++;
                        _logger.LogInformation("✅ Generated cues for {Title}: Drop={Drop:F1}s", 
                            track.Title, features.DropTimeSeconds);
                    }
                    else
                    {
                        result.Failed++;
                        _logger.LogWarning("Failed to generate cues for {Title}", track.Title);
                    }
                }
            }
            catch (Exception ex)
            {
                result.Failed++;
                _logger.LogError(ex, "Error generating cues for track {Title}", track.Title);
            }

            // Report progress
            progress?.Report((i + 1) * 100 / tracks.Count);
        }

        _logger.LogInformation("Cue generation complete: {Success} success, {Failed} failed, {Skipped} skipped",
            result.Success, result.Failed, result.Skipped);

        return result;
    }
}

/// <summary>
/// Result of batch cue generation operation.
/// </summary>
public class CueGenerationResult
{
    public int TotalTracks { get; set; }
    public int Success { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
}
