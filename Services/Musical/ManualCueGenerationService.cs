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
    private readonly TrackForensicLogger _forensicLogger; // Phase 4.7: Forensic Timeline
    private readonly IAudioIntelligenceService _essentiaService;
    private readonly DropDetectionEngine _dropEngine;
    private readonly CueGenerationEngine _cueEngine;

    public ManualCueGenerationService(
        ILogger<ManualCueGenerationService> logger,
        TrackForensicLogger forensicLogger, // Phase 4.7
        IAudioIntelligenceService essentiaService,
        DropDetectionEngine dropEngine,
        CueGenerationEngine cueEngine)
    {
        _logger = logger;
        _forensicLogger = forensicLogger;
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
        
        // Phase 4.7: Create correlation ID for this batch operation
        var batchCorrelationId = CorrelationIdExtensions.NewCorrelationId();
        
        using var db = new AppDbContext();
        
        // Get all tracks in playlist
        var tracks = await db.PlaylistTracks
            .Where(pt => pt.PlaylistId == playlistId && pt.ResolvedFilePath != null)
            .ToListAsync();

        if (tracks.Count == 0)
        {
            _logger.LogWarning("No tracks found in playlist {PlaylistId}", playlistId);
            _forensicLogger.Warning(batchCorrelationId, Data.Entities.ForensicStage.CueGeneration, 
                $"No tracks found in playlist {playlistId}");
            return result;
        }

        result.TotalTracks = tracks.Count;
        _logger.LogInformation("Starting cue generation for {Count} tracks in playlist {PlaylistId}", 
            tracks.Count, playlistId);
        
        // Phase 4.7: Log user action initiation
        _forensicLogger.Info(batchCorrelationId, Data.Entities.ForensicStage.CueGeneration,
            $"User triggered batch cue generation for {tracks.Count} tracks",
            new { PlaylistId = playlistId, TrackCount = tracks.Count });

        for (int i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            
            // Phase 4.7: Use TrackUniqueHash as CorrelationId to link logs to the track across the system
            var trackCorrelationId = track.TrackUniqueHash;
            if (string.IsNullOrEmpty(trackCorrelationId)) trackCorrelationId = CorrelationIdExtensions.NewCorrelationId();
            
            try
            {
                // Check if cues already exist
                var existingFeatures = await db.AudioFeatures
                    .FirstOrDefaultAsync(f => f.TrackUniqueHash == track.TrackUniqueHash);

                if (existingFeatures?.DropTimeSeconds != null)
                {
                    _logger.LogDebug("Track {Title} already has cues - skipping", track.Title);
                    _forensicLogger.Info(trackCorrelationId, Data.Entities.ForensicStage.CueGeneration,
                        "Track already has cues - skipped",
                        new { Title = track.Title, ExistingDrop = existingFeatures.DropTimeSeconds });
                    result.Skipped++;
                    continue;
                }

                // Trigger analysis with cue generation enabled
                if (!string.IsNullOrEmpty(track.ResolvedFilePath) && System.IO.File.Exists(track.ResolvedFilePath))
                {
                    // Phase 4.7: Log analysis start
                    _forensicLogger.Info(trackCorrelationId, Data.Entities.ForensicStage.MusicalAnalysis,
                        "Starting musical analysis with cue generation",
                        new { TrackIdentifier = track.TrackUniqueHash, Title = track.Title });
                    
                    using var timedOp = _forensicLogger.TimedOperation(trackCorrelationId, 
                        Data.Entities.ForensicStage.MusicalAnalysis, "Essentia Analysis");
                    
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
                        
                        // Phase 4.7: Log success with cue details
                        _forensicLogger.Info(trackCorrelationId, Data.Entities.ForensicStage.Persistence,
                            "Cues generated and saved to database",
                            new { 
                                BPM = features.Bpm,
                                Key = features.CamelotKey,
                                DropTime = features.DropTimeSeconds,
                                CueIntro = features.CueIntro,
                                CueBuild = features.CueBuild,
                                CuePhraseStart = features.CuePhraseStart
                            });
                    }
                    else
                    {
                        result.Failed++;
                        _logger.LogWarning("Failed to generate cues for {Title}", track.Title);
                        
                        // Phase 4.7: Log failure
                        _forensicLogger.Warning(trackCorrelationId, Data.Entities.ForensicStage.MusicalAnalysis,
                            "Cue generation failed - no drop detected or analysis returned null",
                            new { Title = track.Title });
                    }
                }
            }
            catch (Exception ex)
            {
                result.Failed++;
                _logger.LogError(ex, "Error generating cues for track {Title}", track.Title);
                
                // Phase 4.7: Log exception with stack trace
                _forensicLogger.Error(trackCorrelationId, Data.Entities.ForensicStage.CueGeneration,
                    $"Exception during cue generation for {track.Title}", ex);
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
