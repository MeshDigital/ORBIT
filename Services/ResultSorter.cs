using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Models;
using Soulseek;

namespace SLSKDONET.Services;

/// <summary>
/// Sophisticated search result ranking using multiple criteria.
/// Implements the ranking algorithm from slsk-batchdl for optimal result ordering.
/// </summary>
public static class ResultSorter
{
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

        // First, assign original indices before sorting
        var withOriginalIndices = results
            .Select((track, index) =>
            {
                track.OriginalIndex = index;
                return track;
            })
            .ToList();

        return withOriginalIndices
            .Select((track) => (track, criteria: GetSortingCriteria(track, searchTrack, fileConditionEvaluator, random)))
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
    /// </summary>
    private static SortingCriteria GetSortingCriteria(
        Track result,
        Track searchTrack,
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

            // Tiebreaker
            RandomTiebreaker = random.Next()
        };

        return criteria;
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

    /// <summary>
    /// Calculated overall score for sorting.
    /// </summary>
    public double OverallScore
    {
        get
        {
            double score = 0.0;
            
            // 0. Availability (The most critical factor for speed)
            if (HasFreeUploadSlot) score += 2000;
            score -= QueueLength; // Penalize long queues
            if (QueueLength == 0) score += 10; // Bonus for empty queue even if no slot explicitly advertised (rare)

            // 1. Required conditions 
            if (PassesRequired) score += 1000;

            // 2. Preferred conditions
            score += PreferredScore * 500;

            // 3. Metadata quality
            if (HasValidLength) score += 100;
            score += LengthMatch * 100;
            if (BitrateMatch) score += 50;
            score += Math.Min(BitRateValue, 50); // Cap at 50 points

            // 4. String matching
            score += TitleSimilarity * 200;
            score += ArtistSimilarity * 100;
            score += AlbumSimilarity * 50;

            // 5. Tiebreaker
            score += RandomTiebreaker / 1_000_000_000.0; // Small contribution

            return score;
        }
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
