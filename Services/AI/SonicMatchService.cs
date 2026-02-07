using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.AI;

/// <summary>
/// AI-powered track similarity engine using weighted Euclidean distance in vibe space.
/// 
/// The "Vibe Space" is a 3-dimensional coordinate system:
/// - X: Arousal (1-9) - Energy/Intensity (Calm→Energetic)
/// - Y: Valence (1-9) - Mood (Dark→Uplifting)  
/// - Z: Danceability (0-1) - Rhythm (Static→Danceable)
/// 
/// Enhanced with:
/// - BPM Penalty: Tracks with >15% BPM difference get pushed down
/// - Genre Penalty: Cross-genre matches get slight penalty
/// - Match Reasons: "Twin Vibe", "Energy Match", "Rhythmic Match"
/// </summary>
public class SonicMatchService : ISonicMatchService
{
    private readonly ILogger<SonicMatchService> _logger;
    private readonly DatabaseService _databaseService;

    // === DIMENSION WEIGHTS ===
    // Energy is King in EDM - a sad banger still works on a dancefloor
    private const double WeightArousal = 2.0;      // Energy is most important
    private const double WeightValence = 1.0;      // Mood is secondary
    private const double WeightDanceability = 1.5; // Rhythm is crucial
    
    // === PENALTY THRESHOLDS ===
    private const double BpmPenaltyThreshold = 0.15; // 15% BPM difference
    private const double BpmPenaltyValue = 5.0;      // Large penalty to push to bottom
    private const double GenrePenaltyValue = 0.5;    // Small nudge for cross-genre
    
    // Normalization - Arousal/Valence are 1-9, Danceability is 0-1
    private const double DanceabilityScale = 8.0;

    public SonicMatchService(
        ILogger<SonicMatchService> logger,
        DatabaseService databaseService)
    {
        _logger = logger;
        _databaseService = databaseService;
    }

    public async Task<List<SonicMatch>> FindSonicMatchesAsync(string sourceTrackHash, int limit = 20)
    {
        if (string.IsNullOrEmpty(sourceTrackHash))
        {
            _logger.LogWarning("FindSonicMatchesAsync called with empty source hash");
            return new List<SonicMatch>();
        }

        try
        {
            // 1. Get source track's audio features AND track metadata (for BPM, ISRC)
            var sourceFeatures = await _databaseService.GetAudioFeaturesByHashAsync(sourceTrackHash);
            var sourceTrack = await _databaseService.FindTrackAsync(sourceTrackHash);
            var sourceIsrc = sourceTrack?.ISRC;
            
            if (sourceFeatures == null)
            {
                _logger.LogWarning("No audio features found for source track: {Hash}", sourceTrackHash);
                return new List<SonicMatch>();
            }

            // Validate source has the required features
            if (sourceFeatures.Arousal == 0 && sourceFeatures.Valence == 0 && sourceFeatures.Danceability == 0 && sourceFeatures.VectorEmbedding == null)
            {
                _logger.LogWarning("Source track has no vibe data or embeddings: {Hash}", sourceTrackHash);
                return new List<SonicMatch>();
            }

            // 2. Get all analyzed tracks
            var allFeatures = await _databaseService.LoadAllAudioFeaturesAsync();
            
            if (allFeatures == null || !allFeatures.Any())
            {
                _logger.LogWarning("No audio features found in database");
                return new List<SonicMatch>();
            }

            // 3. Calculate distances with advanced algorithm
            var matchCandidates = new List<SonicMatch>();
            
            foreach (var candidate in allFeatures)
            {
                if (candidate.TrackUniqueHash == sourceTrackHash) continue;
                
                // Get candidate track metadata for BPM, ISRC
                var candidateTrack = await _databaseService.FindTrackAsync(candidate.TrackUniqueHash);
                var candidateIsrc = candidateTrack?.ISRC;

                // Priority 0: ISRC Exact Match (The "True Mirror")
                if (!string.IsNullOrEmpty(sourceIsrc) && !string.IsNullOrEmpty(candidateIsrc) && sourceIsrc == candidateIsrc)
                {
                    matchCandidates.Add(new SonicMatch
                    {
                        TrackUniqueHash = candidate.TrackUniqueHash,
                        Artist = candidateTrack?.Artist ?? "Unknown",
                        Title = candidateTrack?.Title ?? "Unknown",
                        Distance = 0,
                        MatchReason = "🆔 Exact Match (ISRC)",
                        MatchSource = "ISRC",
                        Confidence = 1.0,
                        Arousal = candidate.Arousal ?? 0f,
                        Valence = candidate.Valence,
                        Danceability = candidate.Danceability,
                        MoodTag = candidate.MoodTag,
                        Bpm = candidate.Bpm
                    });
                    continue;
                }

                if (candidate.Arousal == 0 && candidate.Valence == 0 && candidate.Danceability == 0 && candidate.VectorEmbedding == null) continue;
                
                var (distance, matchReason, matchSource, confidence) = CalculateAdvancedDistance(
                    sourceFeatures, candidate,
                    sourceFeatures.Bpm, candidate.Bpm,
                    sourceFeatures.ElectronicSubgenre, candidate.ElectronicSubgenre
                );
                
                if (distance < double.MaxValue)
                {
                    matchCandidates.Add(new SonicMatch
                    {
                        TrackUniqueHash = candidate.TrackUniqueHash,
                        Artist = candidateTrack?.Artist ?? "Unknown",
                        Title = candidateTrack?.Title ?? "Unknown",
                        Distance = distance,
                        MatchReason = matchReason,
                        MatchSource = matchSource,
                        Confidence = confidence,
                        Arousal = candidate.Arousal ?? 0f,
                        Valence = candidate.Valence,
                        Danceability = candidate.Danceability,
                        MoodTag = candidate.MoodTag,
                        Bpm = candidate.Bpm
                    });
                }
            }

            // 4. Sort and limit
            var matches = matchCandidates
                .OrderBy(m => m.Distance)
                .Take(limit)
                .ToList();

            _logger.LogInformation(
                "🎵 Sonic Match: Found {Count} matches for {Hash} (A:{A:F1} V:{V:F1} D:{D:F2})",
                matches.Count, sourceTrackHash, 
                sourceFeatures.Arousal, sourceFeatures.Valence, sourceFeatures.Danceability);

            return matches;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find sonic matches for {Hash}", sourceTrackHash);
            return new List<SonicMatch>();
        }
    }

    /// <summary>
    /// Advanced distance calculation with BPM and genre penalties.
    /// Returns (distance, matchReason) tuple.
    /// </summary>
    private (double Distance, string MatchReason, string MatchSource, double Confidence) CalculateAdvancedDistance(
        AudioFeaturesEntity source, AudioFeaturesEntity target,
        double sourceBpm, double targetBpm,
        string? sourceGenre, string? targetGenre)
    {
        double finalDistance;
        string matchReason;
        string matchSource = "AI";
        double confidence = 0;
        double vibeDistance;

        // 1. Core Similarity: Universal AI Embedding (128D) or Fallback to Vibe Space (3D)
        if (source.VectorEmbedding != null && target.VectorEmbedding != null && 
            source.VectorEmbedding.Length > 0 && target.VectorEmbedding.Length == source.VectorEmbedding.Length)
        {
            // Use High-Fidelity SIMD Cosine Similarity
            float similarity = CalculateCosineSimilaritySIMD(source.VectorEmbedding, target.VectorEmbedding);
            confidence = similarity;
            
            // Convert similarity (1.0 = identical) to distance (0.0 = identical) for the sorting logic
            vibeDistance = (1.0 - similarity) * 10.0; // Scale to match existing 0-10 range
            matchReason = DetermineSonicMatchReason(similarity);
            matchSource = "AI (Embedding)";
        }
        else
        {
            // Fallback: Core Vibe Distance (Weighted Euclidean 3D)
            var aDance = source.Danceability * DanceabilityScale;
            var bDance = target.Danceability * DanceabilityScale;
            
            var dArousal = ((source.Arousal ?? 0f) - (target.Arousal ?? 0f)) * WeightArousal;
            var dValence = (source.Valence - target.Valence) * WeightValence;
            var dDance = (aDance - bDance) * WeightDanceability;
            
            vibeDistance = Math.Sqrt(dArousal * dArousal + dValence * dValence + dDance * dDance);
            confidence = Math.Max(0, 1.0 - (vibeDistance / 10.0));
            
            matchReason = DetermineMatchReason(
                Math.Abs((source.Arousal ?? 0) - (target.Arousal ?? 0)),
                Math.Abs(source.Valence - target.Valence),
                Math.Abs(source.Danceability - target.Danceability),
                vibeDistance
            );
            matchSource = "Vibe Space (3D)";
        }

        // 2. BPM Penalty (The "Tempo Drift" Problem)
        double bpmPenalty = 0;
        if (sourceBpm > 0 && targetBpm > 0)
        {
            double bpmDiff = Math.Abs(sourceBpm - targetBpm);
            double bpmRatio = bpmDiff / sourceBpm;
            
            // If ratio > 15%, add massive penalty
            if (bpmRatio > BpmPenaltyThreshold)
            {
                bpmPenalty = BpmPenaltyValue;
                confidence *= 0.5; // Half confidence on tempo mismatch
            }
        }

        // 3. Genre Penalty (The "Genre Gap" Problem)
        double genrePenalty = 0;
        if (!string.IsNullOrEmpty(sourceGenre) && !string.IsNullOrEmpty(targetGenre))
        {
            if (!sourceGenre.Equals(targetGenre, StringComparison.OrdinalIgnoreCase))
            {
                // If it's a vector match but different genre, light penalty
                genrePenalty = GenrePenaltyValue;
                confidence *= 0.9; // Slight confidence drop
            }
        }

        finalDistance = vibeDistance + bpmPenalty + genrePenalty;

        return (finalDistance, matchReason, matchSource, confidence);
    }

    /// <summary>
    /// Determines reason based on high-dimensional vector similarity.
    /// </summary>
    private string DetermineSonicMatchReason(float similarity)
    {
        if (similarity > 0.98f) return "🔮 Sonic Twin";
        if (similarity > 0.90f) return "🌊 Deep Vibe Match";
        if (similarity > 0.80f) return "🎵 Close Texture";
        if (similarity > 0.70f) return "🔄 Compatible";
        return "📶 Weak Match";
    }

    /// <summary>
    /// Determines a human-readable reason for the match.
    /// </summary>
    private string DetermineMatchReason(
        double arousalDelta, double valenceDelta, double danceDelta, double vibeDistance)
    {
        // Twin Vibe: Almost identical in all dimensions
        if (vibeDistance < 0.5)
            return "🔮 Twin Vibe";

        // Energy Match: Arousal very close, others may differ
        if (arousalDelta < 0.5 && (valenceDelta > 1.0 || danceDelta > 0.1))
            return "⚡ Energy Match";

        // Mood Match: Valence very close, others may differ  
        if (valenceDelta < 0.5 && (arousalDelta > 1.0 || danceDelta > 0.1))
            return "🎭 Mood Match";

        // Rhythmic Match: Danceability very close
        if (danceDelta < 0.05)
            return "💃 Rhythmic Match";

        // Close Vibe: Generally similar
        if (vibeDistance < 2.0)
            return "🎵 Close Vibe";

        // Compatible: Mixable but different
        return "🔄 Compatible";
    }

    public double CalculateSonicDistance(AudioFeaturesEntity a, AudioFeaturesEntity b)
    {
        if (a == null || b == null) return double.MaxValue;

        var (distance, _, _, _) = CalculateAdvancedDistance(a, b, 0, 0, null, null);
        return distance;
    }

    /// <summary>
    /// Calculates Cosine Similarity between two vectors using SIMD for high performance.
    /// Assumes vectors are of equal length (typically 128 for Discogs-EffNet).
    /// </summary>
    public static float CalculateCosineSimilaritySIMD(float[] vecA, float[] vecB)
    {
        if (vecA == null || vecB == null || vecA.Length != vecB.Length || vecA.Length == 0)
            return 0f;

        int size = vecA.Length;
        float dotProduct = 0f;
        float normA = 0f;
        float normB = 0f;

        int i = 0;
        if (Avx.IsSupported)
        {
            var vDot = Vector256<float>.Zero;
            var vNormA = Vector256<float>.Zero;
            var vNormB = Vector256<float>.Zero;

            for (; i <= size - 8; i += 8)
            {
                var va = Vector256.Create(vecA[i], vecA[i+1], vecA[i+2], vecA[i+3], vecA[i+4], vecA[i+5], vecA[i+6], vecA[i+7]);
                var vb = Vector256.Create(vecB[i], vecB[i+1], vecB[i+2], vecB[i+3], vecB[i+4], vecB[i+5], vecB[i+6], vecB[i+7]);

                vDot = Avx.Add(vDot, Avx.Multiply(va, vb));
                vNormA = Avx.Add(vNormA, Avx.Multiply(va, va));
                vNormB = Avx.Add(vNormB, Avx.Multiply(vb, vb));
            }

            // Horizontal sum
            dotProduct = VectorSum(vDot);
            normA = VectorSum(vNormA);
            normB = VectorSum(vNormB);
        }
        else if (Sse.IsSupported)
        {
            var vDot = Vector128<float>.Zero;
            var vNormA = Vector128<float>.Zero;
            var vNormB = Vector128<float>.Zero;

            for (; i <= size - 4; i += 4)
            {
                var va = Vector128.Create(vecA[i], vecA[i+1], vecA[i+2], vecA[i+3]);
                var vb = Vector128.Create(vecB[i], vecB[i+1], vecB[i+2], vecB[i+3]);

                vDot = Sse.Add(vDot, Sse.Multiply(va, vb));
                vNormA = Sse.Add(vNormA, Sse.Multiply(va, va));
                vNormB = Sse.Add(vNormB, Sse.Multiply(vb, vb));
            }

            dotProduct = VectorSum128(vDot);
            normA = VectorSum128(vNormA);
            normB = VectorSum128(vNormB);
        }

        // Remaining elements
        for (; i < size; i++)
        {
            dotProduct += vecA[i] * vecB[i];
            normA += vecA[i] * vecA[i];
            normB += vecB[i] * vecB[i];
        }

        if (normA <= 0 || normB <= 0) return 0f;

        return dotProduct / ((float)Math.Sqrt(normA) * (float)Math.Sqrt(normB));
    }

    private static float VectorSum(Vector256<float> v)
    {
        float sum = 0;
        for (int i = 0; i < 8; i++) sum += v.GetElement(i);
        return sum;
    }

    private static float VectorSum128(Vector128<float> v)
    {
        float sum = 0;
        for (int i = 0; i < 4; i++) sum += v.GetElement(i);
        return sum;
    }

    public async Task<List<SonicMatch>> FindBridgeAsync(LibraryEntryEntity trackA, LibraryEntryEntity trackB, int limit = 5)
    {
        if (trackA == null || trackB == null) return new List<SonicMatch>();

        // 1. Get features for both tracks
        var featuresA = await _databaseService.GetAudioFeaturesByHashAsync(trackA.UniqueHash);
        var featuresB = await _databaseService.GetAudioFeaturesByHashAsync(trackB.UniqueHash);

        if (featuresA == null || featuresB == null) return new List<SonicMatch>();

        // 2. Find candidates (similar to Track A to start with)
        // We get top 50 matches for A, then re-rank by closeness to B
        var candidates = await FindSonicMatchesAsync(trackA.UniqueHash, 50);

        // 3. Score candidates based on being a "Bridge"
        var bridgeCandidates = new List<SonicMatch>();

        foreach (var candidate in candidates)
        {
            if (candidate.TrackUniqueHash == trackB.UniqueHash) continue; // Don't suggest B itself

            // Calculate distance to B
            var distToB = CalculateDistanceToFeatures(candidate, featuresB);
            
            // Bridge Score: Minimized path deviation
            double distToA = candidate.Distance; // Already calculated
            double totalPath = distToA + distToB;
            double distAtoB = CalculateSonicDistance(featuresA, featuresB);
            double deviation = totalPath - distAtoB; // 0 means perfect line

            candidate.Distance = deviation; // Re-purpose Distance for sorting
            candidate.MatchReason = "🌉 Bridge Track";
            
            bridgeCandidates.Add(candidate);
        }

        return bridgeCandidates
            .OrderBy(c => c.Distance)
            .Take(limit)
            .ToList();
    }

    private double CalculateDistanceToFeatures(SonicMatch candidate, AudioFeaturesEntity target)
    {
         var aDance = candidate.Danceability * DanceabilityScale;
         var bDance = target.Danceability * DanceabilityScale;
         
         var dArousal = ((candidate.Arousal) - (target.Arousal ?? 0f)) * WeightArousal;
         var dValence = (candidate.Valence - target.Valence) * WeightValence;
         var dDance = (aDance - bDance) * WeightDanceability;
         
         return Math.Sqrt(dArousal * dArousal + dValence * dValence + dDance * dDance);
    }
}
