using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data.Essentia; // Needed for EssentiaOutput
using SLSKDONET.Models;
using SLSKDONET.Services.Tagging;
using SLSKDONET.Services;
using SLSKDONET.Configuration;

namespace SLSKDONET.Services.Musical;

/// <summary>
/// Phase 10.4: Industrial Prep - Process specific tracks.
/// Uses BulkOperationCoordinator pattern internally or externally.
/// </summary>
public class ManualCueGenerationService
{
    private readonly ILogger<ManualCueGenerationService> _logger;
    private readonly IAudioIntelligenceService _essentiaService;
    private readonly DropDetectionEngine _dropEngine;
    private readonly CueGenerationEngine _cueEngine;
    private readonly IUniversalCueService _universalCueService;
    private readonly AppConfig _appConfig;
    private readonly DatabaseService _databaseService;
    private readonly ILibraryService _libraryService; // Added for playlist track retrieval

    public ManualCueGenerationService(
        ILogger<ManualCueGenerationService> logger,
        IAudioIntelligenceService essentiaService,
        DropDetectionEngine dropEngine,
        CueGenerationEngine cueEngine,
        IUniversalCueService universalCueService,
        AppConfig appConfig,
        DatabaseService databaseService,
        ILibraryService libraryService)
    {
        _logger = logger;
        _essentiaService = essentiaService;
        _dropEngine = dropEngine;
        _cueEngine = cueEngine;
        _universalCueService = universalCueService;
        _appConfig = appConfig;
        _databaseService = databaseService;
        _libraryService = libraryService;
    }

    /// <summary>
    /// Phase 4.2: Generate cues for all tracks in a playlist.
    /// </summary>
    public async Task<CueGenerationResult> GenerateCuesForPlaylistAsync(Guid playlistId, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var tracks = await _libraryService.LoadPlaylistTracksAsync(playlistId);
        if (tracks == null || tracks.Count == 0)
        {
            return new CueGenerationResult { TotalTracks = 0 };
        }

        return await ProcessTracksAsync(tracks, progress, cancellationToken);
    }

    public async Task<CueGenerationResult> ProcessTracksAsync(List<PlaylistTrack> tracks, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var result = new CueGenerationResult { TotalTracks = tracks.Count };
        int processed = 0;

        foreach (var track in tracks)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                bool success = await ProcessSingleTrackAsync(track, cancellationToken);
                if (success) result.Success++;
                else result.Failed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process track {Id}: {FilePath}", track.Id, track.ResolvedFilePath);
                result.Failed++;
            }

            processed++;
            progress?.Report((processed * 100) / tracks.Count);
        }

        return result;
    }

    public async Task<bool> ProcessSingleTrackAsync(PlaylistTrack track, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(track.ResolvedFilePath) || !System.IO.File.Exists(track.ResolvedFilePath))
        {
            return false;
        }

        try
        {
            // 1. Analyze Audio Features (Essentia)
            var features = await _essentiaService.AnalyzeTrackAsync(
                track.ResolvedFilePath, 
                track.TrackUniqueHash, 
                track.Id.ToString(), 
                cancellationToken, 
                generateCues: true);
            if (features == null) return false;

            // 2. Map Cues from Features (populated by analyze engine)
            var cues = new List<OrbitCue>();
            cues.Add(new OrbitCue { Timestamp = features.CueIntro, Name = "Intro", Role = CueRole.Intro });
            if (features.CueBuild.HasValue) 
                cues.Add(new OrbitCue { Timestamp = (double)features.CueBuild.Value, Name = "Build", Role = CueRole.Build });
            if (features.CueDrop.HasValue) 
                cues.Add(new OrbitCue { Timestamp = (double)features.CueDrop.Value, Name = "The Drop", Role = CueRole.Drop, Color = "#FF0000", Confidence = features.DropConfidence });
            if (features.CuePhraseStart.HasValue) 
                cues.Add(new OrbitCue { Timestamp = (double)features.CuePhraseStart.Value, Name = "Phrase Start", Role = CueRole.PhraseStart });
            
            // 3. Construct ephemeral technical entity (stubs for prep check)
            var techEntity = new SLSKDONET.Data.Entities.TrackTechnicalEntity 
            {
                PlaylistTrackId = track.Id,
                IsPrepared = features.DropConfidence > 0.7f,
                CuePointsJson = System.Text.Json.JsonSerializer.Serialize(cues)
            };
            // 4. Update Provenance / Confidence
            track.IsReviewNeeded = features.DropConfidence < 0.7f;
            // Note: CurationConfidence enum property is not available on PlaylistTrack, relying on IsReviewNeeded.

            // 5. Write Tags
            try
            {
                // Only write if we have cues
                if (cues.Any())
                {
                    await _universalCueService.SyncToTagsAsync(track.ResolvedFilePath, cues);
                }
            }
            catch (Exception tagEx)
            {
                _logger.LogWarning(tagEx, "Failed to write tags for {FilePath}", track.ResolvedFilePath);
                // Continue despite tagging failure? Yes.
            }

            // 6. Save to Database 
            // Stub: In real imp, we would update AudioFeaturesEntity with drop data.
            // await SaveTrackDataAsync(track, features, dropResult, cues);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing track logic");
            return false;
        }
    }

    private async Task SaveTrackDataAsync(PlaylistTrack track, AudioFeaturesEntity features, DropDetectionResult drop, List<OrbitCue> cues)
    {
        // Stub: Implementation for DB persistence
        await Task.CompletedTask;
    }
}

public class CueGenerationResult
{
    public int TotalTracks { get; set; }
    public int Success { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
}

public struct DropDetectionResult
{
    public float? DropTime;
    public float Confidence;
}
