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

    /// <summary>
    /// Pillar A: Discover a "Bridge" track that connects Track A to Track B.
    /// Uses 128D vector cosine similarity between Source and Candidate.
    /// Target Threshold: > 0.85
    /// </summary>
    public async Task<List<SonicMatchResult>> FindBridgeAsync(LibraryEntryEntity trackA, LibraryEntryEntity trackB, int limit = 5)
    {
        try
        {
            var featuresA = await _dbContext.AudioFeatures.FirstOrDefaultAsync(af => af.TrackUniqueHash == trackA.UniqueHash);
            var featuresB = await _dbContext.AudioFeatures.FirstOrDefaultAsync(af => af.TrackUniqueHash == trackB.UniqueHash);

            if (featuresA?.VectorEmbedding == null || featuresB?.VectorEmbedding == null)
            {
                _logger.LogWarning("Cannot find bridge: Missing vector embeddings for {A} or {B}", trackA.UniqueHash, trackB.UniqueHash);
                return new();
            }

            // Ideal Bridge: A track that is "between" A and B in the vector space
            // For now, we search for tracks that are highly similar to both or satisfy the 0.85 threshold
            var results = new List<SonicMatchResult>();
            var candidates = await _dbContext.LibraryEntries
                .Include(le => le.AudioFeatures)
                .Where(le => le.UniqueHash != trackA.UniqueHash && le.UniqueHash != trackB.UniqueHash)
                .Where(le => le.AudioFeatures != null && le.AudioFeatures.VectorEmbeddingBytes != null)
                .ToListAsync();

            foreach (var cand in candidates)
            {
                var candFeatures = cand.AudioFeatures;
                if (candFeatures?.VectorEmbedding == null) continue;

                var simA = CalculateCosineSimilarity(featuresA.VectorEmbedding, candFeatures.VectorEmbedding);
                var simB = CalculateCosineSimilarity(candFeatures.VectorEmbedding, featuresB.VectorEmbedding);

                // Threshold Check (User requested > 0.85)
                if (simA > 0.85f && simB > 0.85f)
                {
                    results.Add(new SonicMatchResult
                    {
                        Track = cand,
                        Score = (simA + simB) / 2.0f * 100f,
                        VibeMatch = true
                    });
                }
            }

            return results.OrderByDescending(r => r.Score).Take(limit).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FindBridgeAsync failed");
            return new();
        }
    }

    /// <summary>
    /// SIMD-Accelerated Cosine Similarity.
    /// Uses System.Numerics.Vector for high-performance dot product.
    /// </summary>
    public static float CalculateCosineSimilarity(float[]? vecA, float[]? vecB)
    {
        if (vecA == null || vecB == null || vecA.Length != vecB.Length) return 0;

        float dot = 0;
        float magA = 0;
        float magB = 0;

        int n = vecA.Length;
        int vectorSize = System.Numerics.Vector<float>.Count;
        int i = 0;

        // Use SIMD for dot product and magnitudes
        var vDot = System.Numerics.Vector<float>.Zero;
        var vMagA = System.Numerics.Vector<float>.Zero;
        var vMagB = System.Numerics.Vector<float>.Zero;

        for (; i <= n - vectorSize; i += vectorSize)
        {
            var va = new System.Numerics.Vector<float>(vecA, i);
            var vb = new System.Numerics.Vector<float>(vecB, i);

            vDot += va * vb;
            vMagA += va * va;
            vMagB += vb * vb;
        }

        // Horizontal sum of SIMD vectors
        for (int j = 0; j < vectorSize; j++)
        {
            dot += vDot[j];
            magA += vMagA[j];
            magB += vMagB[j];
        }

        // Remainder
        for (; i < n; i++)
        {
            dot += vecA[i] * vecB[i];
            magA += vecA[i] * vecA[i];
            magB += vecB[i] * vecB[i];
        }

        float denominator = (float)(Math.Sqrt(magA) * Math.Sqrt(magB));
        return denominator > 0 ? dot / denominator : 0;
    }

    /// <summary>
    /// Pillar A: Contextual Discovery.
    /// Analyzes the momentum/trajectory of the last 3 tracks to suggest the next move.
    /// </summary>
    public async Task<List<SonicMatchResult>> GetContextualMatchesAsync(List<LibraryEntryEntity> history, int limit = 10)
    {
        if (history == null || history.Count == 0) return new();

        var lastTrack = history.Last();
        var baselineMatches = await GetMatchesAsync(lastTrack, limit * 2);

        if (history.Count < 3) return baselineMatches.Take(limit).ToList();

        // Analyze Trajectory (Momentum)
        // Energy Trajectory
        float energyA = (float)(history[^3].Energy ?? 0.5);
        float energyB = (float)(history[^2].Energy ?? 0.5);
        float energyC = (float)(history[^1].Energy ?? 0.5);
        
        float energyTrend = (energyC - energyB) + (energyB - energyA); // Positive = Rising, Negative = Falling

        // BPM Trajectory
        float bpmA = (float)(history[^3].Bpm ?? 128);
        float bpmB = (float)(history[^2].Bpm ?? 128);
        float bpmC = (float)(history[^1].Bpm ?? 128);
        
        float bpmTrend = (bpmC - bpmB) + (bpmB - bpmA);

        var contextualResults = new List<SonicMatchResult>();

        foreach (var match in baselineMatches)
        {
            float scoreBonus = 0;

            // Maintain Momentum: If energy is rising, prefer tracks that continue to rise or hold.
            float candEnergy = (float)(match.Track.Energy ?? 0.5);
            float candBpm = (float)(match.Track.Bpm ?? 128);

            if (energyTrend > 0.05f) // Rising Energy
            {
                if (candEnergy >= energyC) scoreBonus += 10;
            }
            else if (energyTrend < -0.05f) // Falling Energy
            {
                if (candEnergy <= energyC) scoreBonus += 10;
            }

            if (bpmTrend > 1) // Rising BPM
            {
                if (candBpm >= bpmC) scoreBonus += 5;
            }

            match.Score = Math.Clamp(match.Score + scoreBonus, 0, 100);
            contextualResults.Add(match);
        }

        return contextualResults.OrderByDescending(r => r.Score).Take(limit).ToList();
    }
}

public class SonicMatchResult
{
    public LibraryEntryEntity Track { get; set; } = null!;
    public float Score { get; set; }
    public bool VibeMatch { get; set; }
}
