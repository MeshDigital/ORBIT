using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Models.Musical;
using SLSKDONET.Services.AI;
using SLSKDONET.Utils;

namespace SLSKDONET.Services.Musical;

/// <summary>
/// Phase 5.0 — DJ-Intelligent Sonic Match Engine.
///
/// Replaces the old flat Euclidean distance with a multi-dimensional,
/// feature-specific similarity pipeline:
///
///   ① Harmonic Score  — Camelot Wheel distance (±1 step logic, 12-key wraparound)
///   ② Rhythm Score    — Bell-curve BPM penalty (±3 BPM = 95%, ±10 BPM = 0%)
///                       with half/double-time rescue (90% for 2× delta)
///   ③ Vibe Score      — Cosine Similarity across the 6-element mood probability vector
///                       (Happy, Aggressive, Sad, Relaxed, Party, Electronic)
///   ④ Timbre Score    — 128D AI embedding cosine similarity when available,
///                       falling back to ElectronicSubgenre string match
///   ⑤ Energy Weight   — Intensity-weighted linear distance:
///                       source energy ≥ 0.8 → heavy penalty if candidate drops below 0.65
///   ⑥ Vocal Clash     — LeadVocal × LeadVocal → −30% TotalConfidence
///                       LeadVocal source + Instrumental/VocalChops candidate → +10% boost
///
/// Uses <see cref="MatchProfile"/> to adjust dimensional weights:
///   Mixable   — Harmonic 40% + Rhythm 40% + Vibe 20%
///   VibeMatch — Vibe 30% + Timbre 65% + Rhythm 5%, Key ignored
///
/// Returns <see cref="SonicMatchResult"/> which now carries a full
/// <see cref="SimilarityBreakdown"/> alongside legacy Score/VibeMatch fields
/// so existing UI consumers continue to work without changes.
/// </summary>
public class SonicMatchService
{
    private readonly ILogger<SonicMatchService> _logger;
    private readonly AppDbContext _dbContext;
    private readonly PersonalClassifierService _vibeClassifier;

    public SonicMatchService(
        ILogger<SonicMatchService> logger,
        AppDbContext dbContext,
        PersonalClassifierService vibeClassifier)
    {
        _logger = logger;
        _dbContext = dbContext;
        _vibeClassifier = vibeClassifier;
    }

    // ====================================================================
    // Public API
    // ====================================================================

    /// <summary>
    /// Finds professional-grade matches for a source track using the full
    /// breakdown pipeline. Uses <see cref="MatchProfile.Mixable"/> by default.
    /// </summary>
    public Task<List<SonicMatchResult>> GetMatchesAsync(LibraryEntryEntity source, int limit = 15)
        => GetMatchesAsync(source, MatchProfile.Mixable, limit);

    /// <summary>
    /// Finds matches with an explicit <see cref="MatchProfile"/>.
    /// </summary>
    public async Task<List<SonicMatchResult>> GetMatchesAsync(
        LibraryEntryEntity source,
        MatchProfile profile,
        int limit = 15)
    {
        try
        {
            if (source == null || string.IsNullOrEmpty(source.UniqueHash)) return new();

            var sourceFeatures = await _dbContext.AudioFeatures
                .FirstOrDefaultAsync(af => af.TrackUniqueHash == source.UniqueHash);

            if (sourceFeatures == null) return new();

            // Deserialise mood probability vector once for performance
            float[] sourceMoodVec = BuildMoodVector(sourceFeatures);

            // BPM pre-filter: relaxed for intelligent matching
            // Mixable (DJ) still needs a window, but VibeMatch (Playlist) should be very broad
            double srcBpm   = sourceFeatures.Bpm;
            double bpmWindow = profile == MatchProfile.Mixable ? 0.15 : 0.40; // 15% for DJ, 40% for Vibe
            
            double minBpm   = srcBpm > 0 ? srcBpm * (1.0 - bpmWindow) : 0;
            double maxBpm   = srcBpm > 0 ? srcBpm * (1.0 + bpmWindow) : double.MaxValue;
            double minHalfT = srcBpm > 0 ? srcBpm * 0.45 : 0;
            double maxHalfT = srcBpm > 0 ? srcBpm * 0.55 : 0;
            double minDoubleT = srcBpm > 0 ? srcBpm * 1.90 : 0;
            double maxDoubleT = srcBpm > 0 ? srcBpm * 2.10 : 0;

            IQueryable<LibraryEntryEntity> query = _dbContext.LibraryEntries
                .Include(le => le.AudioFeatures)
                .Where(le => le.UniqueHash != source.UniqueHash)
                .Where(le => le.AudioFeatures != null && le.AudioFeatures.Bpm > 0);

            // Only apply BPM pre-filter if source has a valid BPM
            if (srcBpm > 0)
            {
                query = query.Where(le =>
                    // Main window
                    (le.AudioFeatures!.Bpm >= minBpm && le.AudioFeatures.Bpm <= maxBpm) ||
                    // Half-time rescue
                    (le.AudioFeatures!.Bpm >= minHalfT && le.AudioFeatures.Bpm <= maxHalfT) ||
                    // Double-time rescue
                    (le.AudioFeatures!.Bpm >= minDoubleT && le.AudioFeatures.Bpm <= maxDoubleT));
            }

            var candidates = await query.ToListAsync();

            var results = new List<SonicMatchResult>();

            foreach (var cand in candidates)
            {
                var cf = cand.AudioFeatures;
                if (cf == null) continue;

                var breakdown = ScoreCandidate(sourceFeatures, sourceMoodVec, cf, profile);

                // Reject extremely poor matches (< 20% confidence) to avoid polluting results
                if (breakdown.TotalConfidence < 0.20) continue;

                results.Add(new SonicMatchResult
                {
                    Track      = cand,
                    Score      = (float)(breakdown.TotalConfidence * 100.0),
                    VibeMatch  = breakdown.VibeScore >= 0.70,
                    Breakdown  = breakdown
                });
            }

            return results
                .OrderByDescending(r => r.Score)
                .Take(limit)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SonicMatch failed for {Artist} – {Title}", source.Artist, source.Title);
            return new();
        }
    }

    /// <summary>
    /// Finds tracks with an "Inverted Energy Profile" relative to the source (Mashup mode).
    /// </summary>
    public async Task<List<SonicMatchResult>> GetMashupIdeasAsync(LibraryEntryEntity source, int limit = 5)
    {
        var sourceFeatures = await _dbContext.AudioFeatures
            .FirstOrDefaultAsync(af => af.TrackUniqueHash == source.UniqueHash);
        if (sourceFeatures == null) return new();

        var baseMatches = await GetMatchesAsync(source, MatchProfile.Mixable, limit * 3);
        var mashupResults = new List<SonicMatchResult>();
        var sourceEnergyArr = JsonSerializer.Deserialize<int[]>(sourceFeatures.SegmentedEnergyJson ?? "[]") ?? Array.Empty<int>();

        foreach (var match in baseMatches)
        {
            var candFeatures = await _dbContext.AudioFeatures
                .FirstOrDefaultAsync(af => af.TrackUniqueHash == match.Track.UniqueHash);
            if (candFeatures == null) continue;

            var candEnergyArr = JsonSerializer.Deserialize<int[]>(candFeatures.SegmentedEnergyJson ?? "[]") ?? Array.Empty<int>();
            double inverseCorrelation = 0;
            for (int i = 0; i < Math.Min(sourceEnergyArr.Length, candEnergyArr.Length); i++)
                inverseCorrelation += Math.Abs(sourceEnergyArr[i] - candEnergyArr[i]);

            double inverseScore = inverseCorrelation / 72.0;
            match.Score = (float)Math.Clamp((match.Score * 0.7) + (inverseScore * 30), 0, 100);
            mashupResults.Add(match);
        }

        return mashupResults.OrderByDescending(r => r.Score).Take(limit).ToList();
    }

    /// <summary>
    /// Pillar A: Discover a "Bridge" track that sits between Track A and Track B.
    /// </summary>
    public async Task<List<SonicMatchResult>> FindBridgeAsync(
        LibraryEntryEntity trackA, LibraryEntryEntity trackB, int limit = 5)
    {
        try
        {
            var featuresA = await _dbContext.AudioFeatures
                .FirstOrDefaultAsync(af => af.TrackUniqueHash == trackA.UniqueHash);
            var featuresB = await _dbContext.AudioFeatures
                .FirstOrDefaultAsync(af => af.TrackUniqueHash == trackB.UniqueHash);

            if (featuresA?.VectorEmbedding == null || featuresB?.VectorEmbedding == null)
            {
                _logger.LogWarning("Cannot find bridge: Missing vector embeddings for {A} or {B}",
                    trackA.UniqueHash, trackB.UniqueHash);
                return new();
            }

            var results = new List<SonicMatchResult>();
            var candidates = await _dbContext.LibraryEntries
                .Include(le => le.AudioFeatures)
                .Where(le => le.UniqueHash != trackA.UniqueHash && le.UniqueHash != trackB.UniqueHash)
                .Where(le => le.AudioFeatures != null && le.AudioFeatures.VectorEmbeddingBytes != null)
                .ToListAsync();

            foreach (var cand in candidates)
            {
                var cf = cand.AudioFeatures;
                if (cf?.VectorEmbedding == null) continue;

                var simA = CalculateCosineSimilarity(featuresA.VectorEmbedding, cf.VectorEmbedding);
                var simB = CalculateCosineSimilarity(cf.VectorEmbedding, featuresB.VectorEmbedding);

                if (simA > 0.85f && simB > 0.85f)
                {
                    results.Add(new SonicMatchResult
                    {
                        Track     = cand,
                        Score     = (simA + simB) / 2.0f * 100f,
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
    /// Pillar A: Contextual Discovery — analyses momentum of the last 3 tracks.
    /// </summary>
    public async Task<List<SonicMatchResult>> GetContextualMatchesAsync(
        List<LibraryEntryEntity> history, int limit = 10)
    {
        if (history == null || history.Count == 0) return new();

        var lastTrack = history.Last();
        var baselineMatches = await GetMatchesAsync(lastTrack, limit * 2);

        if (history.Count < 3) return baselineMatches.Take(limit).ToList();

        float energyA = (float)(history[^3].Energy ?? 0.5);
        float energyB = (float)(history[^2].Energy ?? 0.5);
        float energyC = (float)(history[^1].Energy ?? 0.5);
        float energyTrend = (energyC - energyB) + (energyB - energyA);

        float bpmC = (float)(history[^1].Bpm ?? 128);
        float bpmB = (float)(history[^2].Bpm ?? 128);
        float bpmA = (float)(history[^3].Bpm ?? 128);
        float bpmTrend = (bpmC - bpmB) + (bpmB - bpmA);

        foreach (var match in baselineMatches)
        {
            float candEnergy = (float)(match.Track.Energy ?? 0.5);
            float candBpm    = (float)(match.Track.Bpm    ?? 128);

            float scoreBonus = 0;
            if (energyTrend > 0.05f && candEnergy >= energyC) scoreBonus += 10;
            else if (energyTrend < -0.05f && candEnergy <= energyC) scoreBonus += 10;
            if (bpmTrend > 1 && candBpm >= bpmC) scoreBonus += 5;

            match.Score = Math.Clamp(match.Score + scoreBonus, 0, 100);
        }

        return baselineMatches.OrderByDescending(r => r.Score).Take(limit).ToList();
    }

    // ====================================================================
    // Core Scoring Pipeline
    // ====================================================================

    /// <summary>
    /// Scores a single candidate against the source and returns a full SimilarityBreakdown.
    /// </summary>
    private SimilarityBreakdown ScoreCandidate(
        AudioFeaturesEntity src,
        float[] srcMoodVec,
        AudioFeaturesEntity cand,
        MatchProfile profile)
    {
        var breakdown = new SimilarityBreakdown { ProfileUsed = profile };

        // ① Harmonic Score (Camelot Wheel)
        breakdown.HarmonicScore = profile == MatchProfile.VibeMatch
            ? 1.0  // Key is irrelevant in playlist/vibe mode
            : ScoreHarmonic(src.CamelotKey, cand.CamelotKey);

        // ② Rhythm Score (Bell-curve BPM)
        breakdown.RhythmScore = ScoreRhythm(src.Bpm, cand.Bpm);

        // ③ Vibe Score (Mood vector cosine similarity)
        float[] candMoodVec = BuildMoodVector(cand);
        breakdown.VibeScore = ScoreVibe(srcMoodVec, candMoodVec, src, cand);

        // ④ Timbre Score (AI embedding or genre fallback)
        breakdown.TimbreScore = ScoreTimbre(src, cand);

        // Raw diagnostics for UI
        breakdown.CandidateBpm    = cand.Bpm;
        breakdown.CandidateCamelot = cand.CamelotKey;
        breakdown.BpmDelta        = cand.Bpm - src.Bpm;
        breakdown.EnergyDelta     = cand.Energy - src.Energy;

        // ⑤ Weighted combination based on profile
        breakdown.TotalConfidence = ApplyProfileWeights(breakdown, profile);

        // ⑥ Vocal Clash Avoidance
        ApplyVocalAdjustments(src, cand, breakdown);

        // ⑥a Final clamp
        breakdown.TotalConfidence = Math.Clamp(breakdown.TotalConfidence, 0.0, 1.0);

        // Build human-readable tags
        BuildMatchTags(breakdown, src, cand);

        return breakdown;
    }

    // ====================================================================
    // ① Harmonic Scoring — Camelot Wheel Theory
    // ====================================================================

    /// <summary>
    /// Camelot Wheel distance. Both sets of rules are applied:
    ///   1. Same key (8A → 8A) = 1.00
    ///   2. Relative major/minor (8A ↔ 8B) = 0.90
    ///   3. Adjacent fifth, same mode (8A → 7A or 9A, wrapping 12→1) = 0.85
    ///   4. Energy jump: two steps away, same mode = 0.60
    ///   5. Anything else = 0.00 (incompatible)
    /// </summary>
    private static double ScoreHarmonic(string srcCamelot, string candCamelot)
    {
        if (string.IsNullOrEmpty(srcCamelot) || string.IsNullOrEmpty(candCamelot)) return 0.5; // Unknown → neutral

        if (srcCamelot.Equals(candCamelot, StringComparison.OrdinalIgnoreCase)) return 1.00;

        if (!ParseCamelot(srcCamelot, out int sNum, out char sLet) ||
            !ParseCamelot(candCamelot, out int cNum, out char cLet))
            return 0.5; // Parse failure → neutral

        // Relative major/minor (same number, different letter)
        if (sNum == cNum && sLet != cLet) return 0.90;

        // Circular distance on the 1–12 wheel
        int dist = Math.Abs(sNum - cNum);
        if (dist > 6) dist = 12 - dist; // wrap-around

        if (sLet == cLet)
        {
            return dist switch
            {
                1 => 0.85, // Perfect 5th — the DJ's best friend
                2 => 0.60, // Energy jump — creative risk
                _ => 0.00
            };
        }

        // Different mode (A vs B) + adjacent number = 0.70 (acceptable)
        return dist == 1 ? 0.70 : 0.00;
    }

    private static bool ParseCamelot(string camelot, out int number, out char letter)
    {
        number = 0; letter = ' ';
        if (camelot.Length < 2) return false;
        letter = char.ToUpper(camelot[^1]);
        return int.TryParse(camelot[..^1], out number);
    }

    // ====================================================================
    // ② Rhythm Scoring — Steep Bell Curve BPM
    // ====================================================================

    private static double ScoreRhythm(float srcBpm, float candBpm)
    {
        if (srcBpm <= 0 || candBpm <= 0) return 0.5; // Unknown

        double diff = Math.Abs(srcBpm - candBpm);

        // Exact (< 0.5 BPM) — perfectly locked
        if (diff < 0.5) return 1.00;

        // Half-time / double-time rescue (±5% of ×2 or ÷2)
        double halfDiff   = Math.Abs(srcBpm / 2.0 - candBpm);
        double doubleDiff = Math.Abs(srcBpm * 2.0 - candBpm);
        if (halfDiff < srcBpm * 0.05 || doubleDiff < srcBpm * 0.05) return 0.90;

        // Bell curve: 0–3 BPM → 0.95–1.00, 10+ BPM → 0.00
        // Uses a Gaussian: score = exp(−(diff / σ)²), σ ≈ 4.3
        // so that diff=3 → 0.95, diff=10 → ~0.007 ≈ 0
        double sigma = 4.3;
        double score = Math.Exp(-(diff * diff) / (2.0 * sigma * sigma));

        return Math.Clamp(score, 0.0, 1.0);
    }

    // ====================================================================
    // ③ Vibe Scoring — Mood + Intensity-Weighted Energy
    // ====================================================================

    private static double ScoreVibe(
        float[] srcMood, float[] candMood,
        AudioFeaturesEntity src, AudioFeaturesEntity cand)
    {
        // A. Cosine similarity of the 6-class mood probability vectors
        double moodSim = CosineSimilarity(srcMood, candMood);

        // B. Arousal/Valence distance (normalised to 1–9 scale → unit range)
        double arousalDist = Math.Abs(src.Arousal - cand.Arousal) / 8.0;
        double valenceDist = Math.Abs(src.Valence  - cand.Valence)  / 8.0;
        double avScore     = 1.0 - Math.Sqrt(arousalDist * arousalDist + valenceDist * valenceDist) / Math.Sqrt(2.0);

        // C. Intensity-weighted energy distance
        //    If source is very energetic (≥ 0.85), penalise heavy drops heavily.
        //    If source is neutral (≈ 0.5), energy shouldn't dominate the score.
        double energyScore;
        float  srcEnergy  = src.Energy;
        float  candEnergy = cand.Energy;
        float  energyDiff = Math.Abs(srcEnergy - candEnergy);

        if (srcEnergy >= 0.85f)
        {
            // Intense source: penalise anything that drops below 0.65 (< 0.20 diff is fine)
            energyScore = energyDiff <= 0.20f
                ? 1.0 - energyDiff
                : Math.Max(0, 0.80 - (energyDiff - 0.20) * 3.0);
        }
        else if (srcEnergy <= 0.35f)
        {
            // Calm/ambient source: similarly strict
            energyScore = energyDiff <= 0.20f
                ? 1.0 - energyDiff
                : Math.Max(0, 0.80 - (energyDiff - 0.20) * 3.0);
        }
        else
        {
            // Neutral source (0.35–0.85): energy is a soft factor
            energyScore = 1.0 - energyDiff * 0.8;
        }
        energyScore = Math.Clamp(energyScore, 0.0, 1.0);

        // Combine: weight mood/AV more than raw energy
        return (moodSim * 0.50) + (avScore * 0.35) + (energyScore * 0.15);
    }

    // ====================================================================
    // ④ Timbre Scoring — AI Embedding or Genre Fallback
    // ====================================================================

    private static double ScoreTimbre(AudioFeaturesEntity src, AudioFeaturesEntity cand)
    {
        // Use AI embedding cosine similarity when both tracks have been analysed.
        // Agnostic to dimensionality (supports 128D legacy and 512D Deep DNA).
        if (src.VectorEmbedding != null && cand.VectorEmbedding != null &&
            src.VectorEmbedding.Length > 0 &&
            src.VectorEmbedding.Length == cand.VectorEmbedding.Length)
        {
            return CosineSimilarity(src.VectorEmbedding, cand.VectorEmbedding);
        }

        // Genre string fallback
        if (!string.IsNullOrEmpty(src.ElectronicSubgenre) &&
            !string.IsNullOrEmpty(cand.ElectronicSubgenre))
        {
            return src.ElectronicSubgenre.Equals(cand.ElectronicSubgenre,
                StringComparison.OrdinalIgnoreCase) ? 0.75 : 0.30;
        }

        return 0.50; // Unknown — neutral penalty
    }

    // ====================================================================
    // ⑤ Profile Weights
    // ====================================================================

    private static double ApplyProfileWeights(SimilarityBreakdown b, MatchProfile profile)
    {
        return profile switch
        {
            MatchProfile.Mixable =>
                b.HarmonicScore * 0.30 +
                b.RhythmScore * 0.20 +
                b.VibeScore * 0.25 +
                b.TimbreScore * 0.25,

            MatchProfile.VibeMatch =>
                b.VibeScore * 0.30 +
                b.TimbreScore * 0.65 +
                b.RhythmScore * 0.05,

            _ => (b.HarmonicScore + b.RhythmScore + b.VibeScore + b.TimbreScore) / 4.0
        };
    }

    // ====================================================================
    // ⑥ Vocal Clash Avoidance
    // ====================================================================

    private static void ApplyVocalAdjustments(
        AudioFeaturesEntity src,
        AudioFeaturesEntity cand,
        SimilarityBreakdown b)
    {
        bool srcIsDense  = src.DetectedVocalType.IsDenseVocal();
        bool candIsDense = cand.DetectedVocalType.IsDenseVocal();
        bool candIsSafe  = cand.DetectedVocalType.IsVocalClashSafe();

        if (srcIsDense && candIsDense)
        {
            // LeadVocal × LeadVocal — vocal trainwreck warning
            b.VocalClashPenalty    = 0.30;
            b.TotalConfidence     -= b.VocalClashPenalty;
        }
        else if (srcIsDense && candIsSafe)
        {
            // Complementary — Instrumental or VocalChops over a vocal source = great for layering
            b.VocalComplementBoost = 0.10;
            b.TotalConfidence     += b.VocalComplementBoost;
        }
        // Instrumental source: no special vocal adjustments needed
    }

    // ====================================================================
    // ⑦ Match Tag Builder — Human-Readable UI Strings
    // ====================================================================

    private static void BuildMatchTags(
        SimilarityBreakdown b,
        AudioFeaturesEntity src,
        AudioFeaturesEntity cand)
    {
        var tags = b.MatchTags;

        // Harmonic
        if (b.HarmonicScore >= 1.0)
            tags.Add("🎵 Perfect Harmonic Match");
        else if (b.HarmonicScore >= 0.90)
            tags.Add("🎵 Relative Key Compatible");
        else if (b.HarmonicScore >= 0.85)
            tags.Add("🎵 Adjacent 5th — DJ Friendly");
        else if (b.ProfileUsed != MatchProfile.VibeMatch && b.HarmonicScore < 0.60)
            tags.Add("⛔ Harmonic Clash");

        // Rhythm
        if (b.RhythmScore >= 0.95)
            tags.Add($"🥁 BPM Locked ({cand.Bpm:F0} BPM)");
        else if (b.RhythmScore >= 0.90 && Math.Abs(b.BpmDelta) > 5)
            tags.Add($"🥁 Half/Double-Time ({cand.Bpm:F0} BPM)");
        else if (b.RhythmScore < 0.50)
            tags.Add($"🥁 BPM Gap ({b.BpmDelta:+0.0;−0.0} BPM)");
        else
            tags.Add($"🥁 {cand.Bpm:F0} BPM ({b.BpmDelta:+0.0;−0.0})");

        // Energy direction
        float energyPct = b.EnergyDelta * 100f;
        if (Math.Abs(energyPct) < 5f)
            tags.Add("⚡ Matched Energy");
        else if (energyPct > 0)
            tags.Add($"⚡ Energy Boost (+{energyPct:F0}%)");
        else
            tags.Add($"⚡ Energy Drop ({energyPct:F0}%)");

        // Vibe
        if (b.VibeScore >= 0.85)
            tags.Add($"🎭 Twin Vibe ({cand.MoodTag})");
        else if (b.VibeScore >= 0.65)
            tags.Add($"🎭 Compatible Mood ({cand.MoodTag})");

        // Camelot key for informational purposes
        if (!string.IsNullOrEmpty(b.CandidateCamelot))
            tags.Add($"🔑 {b.CandidateCamelot}");

        // Vocal clash / complement
        if (b.VocalClashPenalty > 0)
            tags.Add("⚠️ Vocal Clash Warning — Both tracks have Lead Vocals");
        else if (b.VocalComplementBoost > 0)
        {
            string label = cand.DetectedVocalType.ToDisplayLabel();
            tags.Add($"✅ {label} — Avoids Vocal Clash");
        }
        else if (src.DetectedVocalType.IsDenseVocal() && !cand.DetectedVocalType.IsDenseVocal())
        {
            tags.Add($"✅ {cand.DetectedVocalType.ToDisplayLabel()} — Safe to Mix");
        }
    }

    // ====================================================================
    // Math Utilities
    // ====================================================================

    /// <summary>Cosine similarity between two float vectors.</summary>
    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a == null || b == null || a.Length == 0 || a.Length != b.Length) return 0.5;

        double dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot  += a[i] * b[i];
            magA += a[i] * (double)a[i];
            magB += b[i] * (double)b[i];
        }
        double denom = Math.Sqrt(magA) * Math.Sqrt(magB);
        return denom <= 0 ? 0.5 : dot / denom;
    }

    /// <summary>Cosine similarity — double[] overload (used for mood vectors).</summary>
    private static double CosineSimilarity(double[] a, double[] b)
    {
        if (a == null || b == null || a.Length == 0 || a.Length != b.Length) return 0.5;

        double dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot  += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        double denom = Math.Sqrt(magA) * Math.Sqrt(magB);
        return denom <= 0 ? 0.5 : dot / denom;
    }

    // SIMD-accelerated cosine similarity — delegates to centralized VectorMathUtils
    public static float CalculateCosineSimilarity(float[]? vecA, float[]? vecB)
        => VectorMathUtils.CosineSimilarity(vecA, vecB);

    // ====================================================================
    // Mood Vector Builder
    // ====================================================================

    /// <summary>
    /// Extracts a fixed 6-element mood probability vector from an AudioFeaturesEntity.
    /// Order: [Happy, Aggressive, Sad, Relaxed, Party, Electronic]
    /// Used for cosine similarity comparison between tracks.
    /// </summary>
    private static float[] BuildMoodVector(AudioFeaturesEntity f)
    {
        // We only have single-point mood probabilities stored in the entity, not the full
        // probability array from Essentia's JSON. Derive best approximation:
        // MoodTag + pairwise probabilities from HighLevel (if stored), falling back to entity fields.

        // Best available: derive from discrete fields
        return new float[]
        {
            f.MoodTag?.Equals("Happy",      StringComparison.OrdinalIgnoreCase) == true ? f.MoodConfidence : 0f,
            f.MoodTag?.Equals("Aggressive", StringComparison.OrdinalIgnoreCase) == true ? f.MoodConfidence : 0f,
            f.MoodTag?.Equals("Sad",        StringComparison.OrdinalIgnoreCase) == true ? (f.Sadness ?? f.MoodConfidence) : (f.Sadness ?? 0f),
            f.MoodTag?.Equals("Relaxed",    StringComparison.OrdinalIgnoreCase) == true ? f.MoodConfidence : 0f,
            f.MoodTag?.Equals("Party",      StringComparison.OrdinalIgnoreCase) == true ? f.MoodConfidence : 0f,
            f.MoodTag?.Equals("Electronic", StringComparison.OrdinalIgnoreCase) == true ? f.MoodConfidence : 0f
        };
    }
}

// ==========================================================================
// Updated SonicMatchResult — preserves existing contract, adds Breakdown
// ==========================================================================

public class SonicMatchResult
{
    public LibraryEntryEntity Track { get; set; } = null!;
    public float Score { get; set; }
    public bool VibeMatch { get; set; }

    /// <summary>
    /// Full per-dimensional breakdown. Non-null after Phase 5.0.
    /// Null for matches produced by legacy code paths (e.g. half-time rescue injected directly).
    /// </summary>
    public SimilarityBreakdown? Breakdown { get; set; }
}
