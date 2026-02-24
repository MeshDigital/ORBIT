using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models.Musical;

namespace SLSKDONET.Services.AI;

/// <summary>
/// Implementation of ISonicMatchService using the Phase 5 Transparent Match Engine.
/// Provides rich breakdowns and DJ-intelligent matching logic.
/// </summary>
public class SonicMatchService : ISonicMatchService
{
    private readonly ILogger<SonicMatchService> _logger;
    private readonly DatabaseService _databaseService;
    private readonly SLSKDONET.Services.Musical.SonicMatchService _internalEngine;

    public SonicMatchService(
        ILogger<SonicMatchService> logger,
        DatabaseService databaseService,
        SLSKDONET.Services.Musical.SonicMatchService internalEngine)
    {
        _logger = logger;
        _databaseService = databaseService;
        _internalEngine = internalEngine;
    }

    public async Task<List<SonicMatch>> FindSonicMatchesAsync(string sourceTrackHash, int limit = 20)
    {
        if (string.IsNullOrEmpty(sourceTrackHash)) return new();

        try
        {
            var sourceTrack = await _databaseService.GetLibraryEntryAsync(sourceTrackHash);
            if (sourceTrack == null) return new();

            // Delegate to the new high-fidelity internal engine
            var results = await _internalEngine.GetMatchesAsync(sourceTrack, limit);

            return results.Select(r => new SonicMatch
            {
                TrackUniqueHash = r.Track.UniqueHash,
                Artist = r.Track.Artist,
                Title = r.Track.Title,
                Breakdown = r.Breakdown,
                Distance = r.Breakdown != null ? 1.0 - r.Breakdown.TotalConfidence : 1.0,
                Arousal = r.Track.AudioFeatures?.Arousal ?? 0,
                Valence = r.Track.AudioFeatures?.Valence ?? 0,
                Danceability = r.Track.AudioFeatures?.Danceability ?? 0,
                MoodTag = r.Track.AudioFeatures?.MoodTag,
                Bpm = r.Track.AudioFeatures?.Bpm ?? 0
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FindSonicMatchesAsync failed for {Hash}", sourceTrackHash);
            return new();
        }
    }

    public double CalculateSonicDistance(AudioFeaturesEntity a, AudioFeaturesEntity b)
    {
        // For distance-based legacy callers, we still use the internal logic
        // but return a distance derived from confidence.
        // This is used for sorting in some parts of the system.
        // We simulate a GetMatches call or just use a simplified version.
        
        // Since we need to fulfill the interface:
        if (a == null || b == null) return double.MaxValue;
        
        // Return a raw distance based on Vibe/Embeddings
        if (a.VectorEmbedding != null && b.VectorEmbedding != null && a.VectorEmbedding.Length == b.VectorEmbedding.Length)
        {
             return 1.0 - SLSKDONET.Services.Musical.SonicMatchService.CalculateCosineSimilarity(a.VectorEmbedding, b.VectorEmbedding);
        }
        
        return Math.Abs(a.Energy - b.Energy) + Math.Abs(a.Bpm - b.Bpm) / 20.0;
    }

    public async Task<List<SonicMatch>> FindBridgeAsync(LibraryEntryEntity trackA, LibraryEntryEntity trackB, int limit = 5)
    {
        var results = await _internalEngine.FindBridgeAsync(trackA, trackB, limit);
        return results.Select(r => new SonicMatch
        {
            TrackUniqueHash = r.Track.UniqueHash,
            Artist = r.Track.Artist,
            Title = r.Track.Title,
            Breakdown = r.Breakdown,
            Distance = r.Breakdown != null ? 1.0 - r.Breakdown.TotalConfidence : 1.0
        }).ToList();
    }
}
