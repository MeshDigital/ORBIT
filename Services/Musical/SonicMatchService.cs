using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.AI;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Musical;

/// <summary>
/// Phase 25/MIK Parity: AI-driven track matching engine.
/// Uses a weighted Euclidean distance calculation to find harmonically and energetically compatible tracks.
/// </summary>
public class SonicMatchService
{
    private readonly ILogger<SonicMatchService> _logger;
    private readonly AppDbContext _dbContext;
    private readonly PersonalClassifierService _vibeClassifier;

    // Weights for matching distance
    private const double KeyWeight = 0.5;
    private const double EnergyWeight = 0.3;
    private const double BpmWeight = 0.2;

    public SonicMatchService(
        ILogger<SonicMatchService> logger, 
        AppDbContext dbContext,
        PersonalClassifierService vibeClassifier)
    {
        _logger = logger;
        _dbContext = dbContext;
        _vibeClassifier = vibeClassifier;
    }

    /// <summary>
    /// Finds professional-grade matches for a source track.
    /// Algorithm: Weighted Euclidean distance + Vibe Gating.
    /// </summary>
    public async Task<List<SonicMatchResult>> GetMatchesAsync(LibraryEntryEntity source, int limit = 10)
    {
        try
        {
            if (source == null || string.IsNullOrEmpty(source.UniqueHash)) return new();

            // 1. Get source features
            var sourceFeatures = await _dbContext.AudioFeatures
                .FirstOrDefaultAsync(af => af.TrackUniqueHash == source.UniqueHash);

            if (sourceFeatures == null) return new();

            // 2. Identify Vibe for gating (concurrently if possible, but let's keep it simple first)
            var sourceVibe = "Unknown";
            if (!string.IsNullOrEmpty(sourceFeatures.AiEmbeddingJson))
            {
                var embedding = System.Text.Json.JsonSerializer.Deserialize<float[]>(sourceFeatures.AiEmbeddingJson);
                if (embedding != null)
                {
                    var (vibe, _) = _vibeClassifier.Predict(embedding);
                    sourceVibe = vibe;
                }
            }

            // 3. Load candidate pool (pre-filter by BPM ±10% for basic performance)
            var minBpm = (double)sourceFeatures.Bpm * 0.90;
            var maxBpm = (double)sourceFeatures.Bpm * 1.10;

            var candidates = await _dbContext.LibraryEntries
                .Include(le => le.AudioFeatures)
                .Where(le => le.UniqueHash != source.UniqueHash)
                .Where(le => le.AudioFeatures != null && le.AudioFeatures.Bpm >= minBpm && le.AudioFeatures.Bpm <= maxBpm)
                .ToListAsync();

            var results = new List<SonicMatchResult>();

            foreach (var cand in candidates)
            {
                var candFeatures = cand.AudioFeatures;
                if (candFeatures == null) continue;

                // A. Key Compatibility (±1 Camelot Step)
                var keyScore = CalculateKeyCompatibility(sourceFeatures.Key, candFeatures.Key);
                if (keyScore < 0.5) continue; // Skip incompatible keys

                // B. Energy Proximity (±1 on 1-10 scale)
                var energyDist = Math.Abs(sourceFeatures.EnergyScore - candFeatures.EnergyScore);
                var energyScore = Math.Max(0, 1.0 - (energyDist / 10.0));

                // C. BPM Distance (±6% industry standard)
                var bpmDist = Math.Abs(sourceFeatures.Bpm - candFeatures.Bpm) / (sourceFeatures.Bpm > 0 ? sourceFeatures.Bpm : 120f);
                var bpmScore = Math.Max(0, 1.0 - (bpmDist / 0.06));

                // D. Vibe Gating (Style Compatibility)
                bool vibeMatch = false;
                if (sourceVibe != "Unknown" && !string.IsNullOrEmpty(candFeatures.AiEmbeddingJson))
                {
                    var candEmbedding = System.Text.Json.JsonSerializer.Deserialize<float[]>(candFeatures.AiEmbeddingJson);
                    if (candEmbedding != null)
                    {
                        var (candVibe, _) = _vibeClassifier.Predict(candEmbedding);
                        vibeMatch = (sourceVibe == candVibe);
                    }
                }

                // Weighted Total
                var totalScore = (keyScore * KeyWeight) + (energyScore * EnergyWeight) + (bpmScore * BpmWeight);
                if (vibeMatch) totalScore += 0.1; // Bonus for vibe match

                results.Add(new SonicMatchResult
                {
                    Track = cand,
                    Score = (float)Math.Clamp(totalScore * 100, 0, 100),
                    VibeMatch = vibeMatch
                });
            }

            return results
                .OrderByDescending(r => r.Score)
                .Take(limit)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SonicMatch failed for {Artist} - {Title}", source.Artist, source.Title);
            return new();
        }
    }

    private double CalculateKeyCompatibility(string? sourceKey, string? candKey)
    {
        if (string.IsNullOrEmpty(sourceKey) || string.IsNullOrEmpty(candKey)) return 0;
        if (sourceKey == candKey) return 1.0;

        // Relative major/minor (8A <-> 8B)
        if (sourceKey[..^1] == candKey[..^1]) return 0.9;

        // Adjacent numbers (8A <-> 7A or 9A)
        try 
        {
            var sNum = int.Parse(sourceKey[..^1]);
            var cNum = int.Parse(candKey[..^1]);
            var sLet = sourceKey[^1];
            var cLet = candKey[^1];

            if (sLet == cLet && (Math.Abs(sNum - cNum) == 1 || (sNum == 12 && cNum == 1) || (sNum == 1 && cNum == 12)))
                return 0.8;
        }
        catch { }

        return 0;
    }

    /// <summary>
    /// Specific logic for MIK Pro Mashups.
    /// Finds tracks that have an "Inverted Energy Profile" relative to the source.
    /// </summary>
    public async Task<List<SonicMatchResult>> GetMashupIdeasAsync(LibraryEntryEntity source, int limit = 5)
    {
        var sourceFeatures = await _dbContext.AudioFeatures.FirstOrDefaultAsync(af => af.TrackUniqueHash == source.UniqueHash);
        if (sourceFeatures == null) return new();

        var baseMatches = await GetMatchesAsync(source, limit * 3);
        var mashupResults = new List<SonicMatchResult>();
        var sourceEnergyArr = System.Text.Json.JsonSerializer.Deserialize<int[]>(sourceFeatures.SegmentedEnergyJson ?? "[]") ?? new int[8];

        foreach (var match in baseMatches)
        {
            var candFeatures = await _dbContext.AudioFeatures.FirstOrDefaultAsync(af => af.TrackUniqueHash == match.Track.UniqueHash);
            if (candFeatures == null) continue;

            var candEnergyArr = System.Text.Json.JsonSerializer.Deserialize<int[]>(candFeatures.SegmentedEnergyJson ?? "[]") ?? new int[8];
            double inverseCorrelation = 0;
            for (int i = 0; i < Math.Min(sourceEnergyArr.Length, candEnergyArr.Length); i++)
            {
                inverseCorrelation += Math.Abs(sourceEnergyArr[i] - candEnergyArr[i]);
            }
            
            double inverseScore = inverseCorrelation / 72.0;
            match.Score = (float)Math.Clamp((match.Score * 0.7) + (inverseScore * 30), 0, 100);
            mashupResults.Add(match);
        }

        return mashupResults.OrderByDescending(r => r.Score).Take(limit).ToList();
    }
}

public class SonicMatchResult
{
    public LibraryEntryEntity Track { get; set; } = null!;
    public float Score { get; set; }
    public bool VibeMatch { get; set; }
}
