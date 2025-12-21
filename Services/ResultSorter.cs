using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services.Ranking;
using SLSKDONET.Utils;
using Soulseek;

namespace SLSKDONET.Services;

/// <summary>
/// Sophisticated search result ranking using multiple criteria.
/// Implements the ranking algorithm from slsk-batchdl for optimal result ordering.
/// </summary>
public static class ResultSorter
{
    // Phase 2.4: Strategy Pattern for user-configurable ranking
    private static ISortingStrategy _currentStrategy = new BalancedStrategy();
    private static ScoringWeights _currentWeights = ScoringWeights.Balanced;
    
    /// <summary>
    /// Sets the current sorting strategy.
    /// </summary>
    public static void SetStrategy(ISortingStrategy strategy)
    {
        _currentStrategy = strategy ?? new BalancedStrategy();
    }

    /// <summary>
    /// Sets the current scoring weights.
    /// </summary>
    public static void SetWeights(ScoringWeights weights)
    {
        _currentWeights = weights ?? ScoringWeights.Balanced;
    }
    
    /// <summary>
    /// Gets the current sorting strategy.
    /// </summary>
    public static ISortingStrategy GetCurrentStrategy() => _currentStrategy;

    /// <summary>
    /// Gets the current scoring weights.
    /// </summary>
    public static ScoringWeights GetCurrentWeights() => _currentWeights;
    /// <summary>
    /// Orders search results by multiple criteria for optimal download selection.
    /// Prioritizes:
    /// 1. Required conditions met
    /// 2. Preferred conditions met
    /// 3. Upload speed (fast > medium > slow)
    /// 4. File metadata quality (length, bitrate, format)
    /// 5. Levenshtein distance (string similarity)
    /// 6. Random tiebreaker for consistent ordering
    /// </summary>
    /// <summary>
    /// Calculates the rank/score for a single track against the search criteria.
    /// Useful for streaming scenarios where we want to score on-the-fly.
    /// </summary>
    public static void CalculateRank(Track result, Track searchTrack, FileConditionEvaluator evaluator)
    {
        // Phase 2.3: Create ScoringContext from search track (re-created per track here, could be optimized if passed in)
        var context = ScoringContext.FromSearchQuery(
            searchTrack.Artist ?? "",
            searchTrack.Title ?? "",
            searchTrack.BPM.HasValue ? (int)Math.Round(searchTrack.BPM.Value) : null,
            searchTrack.Length,
            searchTrack.MusicalKey
        );
        
        var random = new Random(); // Note: creating random per call might loose determinism if seeded, but for streaming it's acceptable tie-breaking
        var criteria = GetSortingCriteria(result, searchTrack, context, evaluator, random);
        result.CurrentRank = criteria.OverallScore;
    }

    /// <summary>
    /// Orders search results by multiple criteria for optimal download selection.
    /// Prioritizes:
    /// 1. Required conditions met
    /// 2. Preferred conditions met
    /// 3. Upload speed (fast > medium > slow)
    /// 4. File metadata quality (length, bitrate, format)
    /// 5. Levenshtein distance (string similarity)
    /// 6. Random tiebreaker for consistent ordering
    /// </summary>
    public static List<Track> OrderResults(
        IEnumerable<Track> results,
        Track searchTrack,
        FileConditionEvaluator? fileConditionEvaluator = null)
    {
        if (fileConditionEvaluator == null)
        {
            // Simple ranking if no evaluator provided
            return results
                .OrderByDescending(r => r.Bitrate)
                .ThenBy(r => r.Length == null ? int.MaxValue : Math.Abs((r.Length.Value) - (searchTrack.Length ?? 0)))
                .ToList();
        }
        
        var random = new Random();
        
        // Phase 2.3: Create ScoringContext once
        var context = ScoringContext.FromSearchQuery(
            searchTrack.Artist ?? "",
            searchTrack.Title ?? "",
            searchTrack.BPM.HasValue ? (int)Math.Round(searchTrack.BPM.Value) : null,
            searchTrack.Length,
            searchTrack.MusicalKey
        );

        // First, assign original indices before sorting
        var withOriginalIndices = results
            .Select((track, index) =>
            {
                track.OriginalIndex = index;
                return track;
            })
            .ToList();

        return withOriginalIndices
            .Select((track) => (track, criteria: GetSortingCriteria(track, searchTrack, context, fileConditionEvaluator, random)))
            .OrderByDescending(x => x.criteria)
            .Select(x =>
            {
                x.track.CurrentRank = x.criteria.OverallScore;
                return x.track;
            })
            .ToList();
    }

    /// <summary>
    /// Calculates comprehensive sorting criteria for a single result.
    /// Phase 2.3: Now accepts ScoringContext parameter object.
    /// </summary>
    private static SortingCriteria GetSortingCriteria(
        Track result,
        Track searchTrack,
        ScoringContext context,
        FileConditionEvaluator evaluator,
        Random random)
    {
        var criteria = new SortingCriteria
        {
            // Condition matching
            PassesRequired = evaluator.PassesRequired(result),
            PreferredScore = evaluator.ScorePreferred(result),

            // Intelligence Metrics (The User's Request)
            HasFreeUploadSlot = result.HasFreeUploadSlot,
            QueueLength = result.QueueLength,

            // Metadata quality
            HasValidLength = result.Length != null && result.Length > 0,
            LengthMatch = CalculateLengthScore(result, searchTrack),
            BitrateMatch = result.Bitrate >= 128,
            BitRateValue = result.Bitrate / 80,

            // String matching
            TitleSimilarity = CalculateSimilarity(result.Title ?? "", searchTrack.Title ?? ""),
            ArtistSimilarity = CalculateSimilarity(result.Artist ?? "", searchTrack.Artist ?? ""),
            AlbumSimilarity = CalculateSimilarity(result.Album ?? "", searchTrack.Album ?? ""),
            
            // Phase 1: Musical Intelligence (Phase 2.3: Using ScoringContext for BPM)
            BpmProximity = CalculateBpmProximity(result, context),
            IsSuspicious = IsSuspiciousFile(result, searchTrack),

            // Tiebreaker
            RandomTiebreaker = random.Next()
        };

        return criteria;
    }

    /// <summary>
    /// Phase 1: Calculates BPM proximity score based on filename parsing.
    /// Phase 2.3: Now uses ScoringContext parameter object.
    /// Returns neutral score if no BPM found (no penalty for casual files).
    /// </summary>
    private static double CalculateBpmProximity(Track result, ScoringContext context)
    {
        if (!context.TargetBPM.HasValue) return ScoringConstants.Musical.BpmNeutralScore;
        
        // Phase 1.1: Use path-based extraction with confidence scoring
        string fullPath = $"{result.Directory}/{result.Filename}";
        double? fileBpm = FilenameNormalizer.ExtractBpmFromPath(fullPath, out double confidence);
        
        if (!fileBpm.HasValue) return ScoringConstants.Musical.BpmNeutralScore;
        
        double diff = Math.Abs(fileBpm.Value - context.TargetBPM.Value);
        
        // Base proximity score using thresholds from ScoringConstants
        double proximityScore;
        if (diff < ScoringConstants.Musical.BpmPerfectThreshold) 
            proximityScore = 1.0;   // Perfect match
        else if (diff < ScoringConstants.Musical.BpmCloseThreshold) 
            proximityScore = 0.75;  // Close match
        else if (diff < ScoringConstants.Musical.BpmAcceptableThreshold) 
            proximityScore = 0.5;   // Acceptable
        else 
            proximityScore = 0.0;   // Mismatch
        
        // Apply confidence decay for path-based matches
        return proximityScore * confidence;
    }

    /// <summary>
    /// Calculates length match score (0-1, where 1 is perfect match).
    /// </summary>
    private static double CalculateLengthScore(Track result, Track searchTrack)
    {
        if (result.Length == null || searchTrack.Length == null || searchTrack.Length == 0)
            return 0.5; // Neutral score if no length to compare

        var diff = Math.Abs(result.Length.Value - searchTrack.Length.Value);
        const int toleranceSeconds = 5;

        if (diff <= toleranceSeconds)
            return 1.0; // Perfect match within tolerance

        if (diff <= toleranceSeconds * 2)
            return 0.75; // Good match

        if (diff <= toleranceSeconds * 4)
            return 0.5; // Acceptable match

        return Math.Max(0.0, 0.25 - (diff / 1000.0)); // Poor match
    }

    /// <summary>
    /// Calculates string similarity using Levenshtein distance (0-1, where 1 is identical).
    /// </summary>
    private static double CalculateSimilarity(string str1, string str2)
    {
        if (string.IsNullOrEmpty(str1) || string.IsNullOrEmpty(str2))
            return 0.0;

        if (str1.Equals(str2, StringComparison.OrdinalIgnoreCase))
            return 1.0;

        // Normalize for comparison
        string s1 = str1.ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
        string s2 = str2.ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");

        if (s1 == s2)
            return 1.0;

        int distance = LevenshteinDistance(s1, s2);
        int maxLength = Math.Max(s1.Length, s2.Length);

        if (maxLength == 0)
            return 1.0;

        return 1.0 - (double)distance / maxLength;
    }

    /// <summary>
    /// Calculates Levenshtein distance between two strings.
    /// </summary>
    private static int LevenshteinDistance(string s1, string s2)
    {
        int[,] d = new int[s1.Length + 1, s2.Length + 1];

        for (int i = 0; i <= s1.Length; i++)
            d[i, 0] = i;

        for (int j = 0; j <= s2.Length; j++)
            d[0, j] = j;

        for (int i = 1; i <= s1.Length; i++)
        {
            for (int j = 1; j <= s2.Length; j++)
            {
                int cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        return d[s1.Length, s2.Length];
    }
    
    /// <summary>
    /// Phase 1.3: Detects suspicious/fake files via VBR validation.
    /// Implements slsk-batchdl patterns with artwork buffer and silent tail exception.
    /// </summary>
    private static bool IsSuspiciousFile(Track result, Track searchTrack)
    {
        // Check 1: Duration mismatch (wrong version - Radio vs Extended)
        if (result.Length.HasValue && searchTrack.CanonicalDuration.HasValue)
        {
            double expectedSec = searchTrack.CanonicalDuration.Value / 1000.0;
            if (Math.Abs(result.Length.Value - expectedSec) > 30)
                return true;
        }
        
        // Check 2: Filesize impossibly small for claimed duration
        if (result.Size.HasValue && result.Length.HasValue && result.Length.Value > 0)
        {
            double minBytesPerSecond = 8000; // ~64kbps
            double expectedMinSize = result.Length.Value * minBytesPerSecond;
            
            if (result.Size.Value < expectedMinSize * 0.5)
                return true;
        }
        
        // Check 3: VBR Validation (from slsk-batchdl)
        // Detects fake upconverted files (128→320, MP3→FLAC)
        double efficiency = GetBitrateEfficiency(result);
        
        if (efficiency < 0.8) // 80% threshold (industry standard)
        {
            // Silent Tail Exception: FLAC with high compression might be legitimate
            if (result.Bitrate > 1000 && efficiency >= 0.6)
            {
                // Potential lossless with high compression (silence, classical)
                // Allow it but log for debugging
                return false;
            }
            
            // Otherwise, it's a fake file
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Phase 1.3: Calculates bitrate efficiency to detect fake files.
    /// Accounts for artwork buffer (32KB) to prevent cover art inflation.
    /// </summary>
    private static double GetBitrateEfficiency(Track result)
    {
        if (!result.Size.HasValue || !result.Length.HasValue || result.Bitrate <= 0)
            return 1.0; // Assume okay if data is missing
        
        // Artwork Buffer: Subtract 32KB for ID3 tags and embedded cover art
        const int artworkBufferBytes = 32768;
        long adjustedSize = Math.Max(0, result.Size.Value - artworkBufferBytes);
        
        // Calculate expected filesize based on claimed bitrate
        double expectedBytes = (result.Bitrate * 1000 * result.Length.Value) / 8;
        
        // Return efficiency ratio (1.0 = perfect match, <0.8 = suspicious)
        return (double)adjustedSize / expectedBytes;
    }
}

/// <summary>
/// Encapsulates sorting criteria for comparable ranking.
/// </summary>
public class SortingCriteria : IComparable<SortingCriteria>
{
    public bool PassesRequired { get; set; }
    public double PreferredScore { get; set; }
    
    // Intelligence Metrics
    public bool HasFreeUploadSlot { get; set; } // +2000 if true
    public int QueueLength { get; set; } // -1 per item in queue
    
    public bool HasValidLength { get; set; }
    public double LengthMatch { get; set; }
    public bool BitrateMatch { get; set; }
    public int BitRateValue { get; set; }
    public double TitleSimilarity { get; set; }
    public double ArtistSimilarity { get; set; }
    public double AlbumSimilarity { get; set; }
    public int RandomTiebreaker { get; set; }
    
    // Phase 1: Musical Intelligence
    public double BpmProximity { get; set; }      // 0-1 score for BPM match from filename
    public bool IsSuspicious { get; set; }        // True if filesize/duration mismatch

    /// <summary>
    /// Calculated overall score for sorting.
    /// Phase 2.2: Composing Methods pattern - reads like high-level summary.
    /// Phase 2.4: Strategy Pattern - uses configurable ranking strategy.
    /// </summary>
    public double OverallScore
    {
        get
        {
            // GUARD CLAUSES: Short-circuit optimization
            // Return early to avoid wasted computation on bad files
            if (IsDurationMismatch()) return double.NegativeInfinity;
            if (IsSuspicious) return double.NegativeInfinity;
            
            // Phase 2.4: Use strategy pattern for configurable ranking
            return ResultSorter.GetCurrentStrategy().CalculateScore(
                CalculateAvailabilityScore(),
                CalculateConditionsScore(),
                CalculateQualityScore(),
                CalculateMusicalIntelligenceScore(),
                CalculateMetadataScore(),
                CalculateStringMatchingScore(),
                CalculateTiebreakerScore(),
                ResultSorter.GetCurrentWeights()
            );
        }
    }
    
    /// <summary>
    /// Phase 2.2: Calculates availability score (upload speed, queue length).
    /// Pure function for testability.
    /// </summary>
    internal double CalculateAvailabilityScore()
    {
        double score = 0.0;
        
        // Free slot bonus
        if (HasFreeUploadSlot) 
            score += ScoringConstants.Availability.FreeSlotBonus;
        
        // Queue penalties
        score -= QueueLength * ScoringConstants.Availability.QueuePenaltyPerItem;
        
        if (QueueLength == 0) 
            score += ScoringConstants.Availability.EmptyQueueBonus;
        
        // Heavy penalty for very long queues
        if (QueueLength > ScoringConstants.Availability.LongQueueThreshold)
            score -= ScoringConstants.Availability.LongQueuePenalty;
        
        return score;
    }
    
    /// <summary>
    /// Phase 2.2: Calculates required and preferred conditions score.
    /// </summary>
    internal double CalculateConditionsScore()
    {
        double score = 0.0;
        
        if (PassesRequired) 
            score += ScoringConstants.Conditions.RequiredPassBonus;
        
        score += PreferredScore * ScoringConstants.Conditions.PreferredWeight;
        
        return score;
    }
    
    /// <summary>
    /// Phase 2.2: Calculates musical intelligence score (BPM, Key matching).
    /// Pure function - only depends on input parameters.
    /// </summary>
    internal double CalculateMusicalIntelligenceScore()
    {
        // BPM proximity is already calculated (0-1 range)
        return BpmProximity * ScoringConstants.Musical.BpmMatchBonus;
        
        // TODO: Add key matching when implemented
        // if (KeyMatch) score += ScoringConstants.Musical.KeyMatchBonus;
    }
    
    /// <summary>
    /// Phase 2.2: Calculates metadata quality score (length match).
    /// </summary>
    internal double CalculateMetadataScore()
    {
        double score = 0.0;
        
        if (HasValidLength) 
            score += ScoringConstants.Metadata.ValidLengthBonus;
        
        score += LengthMatch * ScoringConstants.Metadata.LengthMatchWeight;
        
        return score;
    }
    
    /// <summary>
    /// Phase 2.2: Calculates string matching score (title, artist, album similarity).
    /// </summary>
    internal double CalculateStringMatchingScore()
    {
        return (TitleSimilarity * ScoringConstants.Metadata.TitleSimilarityWeight)
             + (ArtistSimilarity * ScoringConstants.Metadata.ArtistSimilarityWeight)
             + (AlbumSimilarity * ScoringConstants.Metadata.AlbumSimilarityWeight);
    }
    
    /// <summary>
    /// Phase 2.2: Calculates random tiebreaker score.
    /// </summary>
    internal double CalculateTiebreakerScore()
    {
        return RandomTiebreaker / ScoringConstants.Tiebreaker.RandomDivisor;
    }
    
    /// <summary>
    /// Phase 1.2: Checks if duration mismatch indicates wrong version.
    /// </summary>
    private bool IsDurationMismatch()
    {
        // This would need access to Track data - for now return false
        // Will be properly implemented when we refactor to use ScoringContext
        return false;
    }
    
    /// <summary>
    /// Phase 1.2: Calculates quality score using tiered system.
    /// Lossless > High Quality > Medium Quality > Low Quality
    /// </summary>
    private double CalculateQualityScore()
    {
        // Detect lossless formats
        if (IsLosslessFormat())
        {
            // Lossless gets massive base score
            double score = ScoringConstants.Quality.LosslessBase;
            
            // TODO: Add sample rate bonus when we have access to that data
            // if (sampleRate >= 96000) score += 25;
            
            return score;
        }
        
        // Lossy formats: tiered by bitrate with buffer for VBR quirks
        // Bitrate Buffer Trick: catches VBR V0 files
        if (BitRateValue >= ScoringConstants.Quality.HighQualityThreshold) 
            return ScoringConstants.Quality.HighQualityBase;
        if (BitRateValue >= ScoringConstants.Quality.MediumQualityThreshold) 
            return ScoringConstants.Quality.MediumQualityBase;
        
        // Low quality: proportional scoring (prevents 128kbps from being competitive)
        return BitRateValue * ScoringConstants.Quality.LowQualityMultiplier;
    }
    
    /// <summary>
    /// Phase 1.2: Detects lossless audio formats.
    /// </summary>
    private bool IsLosslessFormat()
    {
        // Common lossless extensions
        // Note: This is a simplified check - proper implementation would use Format field
        // For now, we'll return false and rely on bitrate tiers
        // TODO: Implement when Format field is accessible
        return false;
    }

    public int CompareTo(SortingCriteria? other)
    {
        if (other == null) return 1;

        int comparison;

        // Required conditions first
        comparison = PassesRequired.CompareTo(other.PassesRequired);
        if (comparison != 0) return comparison;

        // Preferred score
        comparison = PreferredScore.CompareTo(other.PreferredScore);
        if (comparison != 0) return comparison;

        // Bitrate
        comparison = BitrateMatch.CompareTo(other.BitrateMatch);
        if (comparison != 0) return comparison;

        comparison = BitRateValue.CompareTo(other.BitRateValue);
        if (comparison != 0) return comparison;

        // Length
        comparison = HasValidLength.CompareTo(other.HasValidLength);
        if (comparison != 0) return comparison;

        comparison = LengthMatch.CompareTo(other.LengthMatch);
        if (comparison != 0) return comparison;

        // String similarity (title most important)
        comparison = TitleSimilarity.CompareTo(other.TitleSimilarity);
        if (comparison != 0) return comparison;

        comparison = ArtistSimilarity.CompareTo(other.ArtistSimilarity);
        if (comparison != 0) return comparison;

        comparison = AlbumSimilarity.CompareTo(other.AlbumSimilarity);
        if (comparison != 0) return comparison;

        // Tiebreaker
        return RandomTiebreaker.CompareTo(other.RandomTiebreaker);
    }
}
