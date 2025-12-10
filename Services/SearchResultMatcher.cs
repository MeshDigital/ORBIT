using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Fuzzy matching service for Soulseek search results.
/// Uses Levenshtein Distance and duration tolerance to find the best matching track.
/// </summary>
public class SearchResultMatcher
{
    private readonly ILogger<SearchResultMatcher> _logger;
    private readonly AppConfig _config;

    public SearchResultMatcher(ILogger<SearchResultMatcher> logger, AppConfig config)
    {
        _logger = logger;
        _config = config;
    }

    /// <summary>
    /// Finds the best matching track from a list of candidates.
    /// Returns null if no acceptable match is found.
    /// </summary>
    public Track? FindBestMatch(
        string expectedArtist,
        string expectedTitle,
        int expectedDurationSeconds,
        IEnumerable<Track> candidates)
    {
        if (!candidates.Any())
            return null;

        if (!_config.FuzzyMatchEnabled)
        {
            _logger.LogDebug("Fuzzy matching disabled, returning first result");
            return candidates.FirstOrDefault();
        }

        var lengthTolerance = _config.SearchLengthToleranceSeconds;
        var matches = new List<(Track Track, double Score)>();

        foreach (var candidate in candidates)
        {
            var score = CalculateMatchScore(
                expectedArtist,
                expectedTitle,
                expectedDurationSeconds,
                candidate,
                lengthTolerance);

            if (score >= 0.7) // Minimum acceptable match threshold
            {
                matches.Add((candidate, score));
            }
        }

        if (!matches.Any())
        {
            _logger.LogWarning("No acceptable fuzzy matches found for {Artist} - {Title}", expectedArtist, expectedTitle);
            return null;
        }

        var bestMatch = matches.OrderByDescending(m => m.Score).FirstOrDefault();
        
        _logger.LogDebug("Best fuzzy match score: {Score:P} for {Artist} - {Title} (candidate: {Candidate})",
            bestMatch.Score,
            expectedArtist,
            expectedTitle,
            $"{bestMatch.Track.Artist} - {bestMatch.Track.Title}");

        return bestMatch.Track;
    }

    /// <summary>
    /// Calculates a match score (0-1) between expected and actual track.
    /// Factors: artist name similarity, title similarity, duration match.
    /// </summary>
    private double CalculateMatchScore(
        string expectedArtist,
        string expectedTitle,
        int expectedDurationSeconds,
        Track candidate,
        int lengthToleranceSeconds)
    {
        // Check duration first (hard constraint)
        if (!IsDurationAcceptable(expectedDurationSeconds, candidate.Length ?? 0, lengthToleranceSeconds))
            return 0.0;

        // Calculate string similarity (0-1)
        var artistSimilarity = CalculateSimilarity(expectedArtist, candidate.Artist ?? "");
        var titleSimilarity = CalculateSimilarity(expectedTitle, candidate.Title ?? "");

        // Weight: title is more important than artist (80% vs 20%)
        var combinedSimilarity = (titleSimilarity * 0.8) + (artistSimilarity * 0.2);

        // Apply bonus if duration is very close
        var durationBonus = GetDurationBonus(expectedDurationSeconds, candidate.Length ?? 0);

        var finalScore = Math.Min(1.0, combinedSimilarity + durationBonus);
        
        _logger.LogTrace(
            "Match score for {Candidate}: artist={ArtistScore:P}, title={TitleScore:P}, combined={Combined:P}, duration_bonus={DurationBonus:P}, final={Final:P}",
            $"{candidate.Artist} - {candidate.Title}",
            artistSimilarity,
            titleSimilarity,
            combinedSimilarity,
            durationBonus,
            finalScore);

        return finalScore;
    }

    /// <summary>
    /// Checks if duration is within acceptable tolerance.
    /// </summary>
    private bool IsDurationAcceptable(int expectedSeconds, int actualSeconds, int toleranceSeconds)
    {
        var difference = Math.Abs(expectedSeconds - actualSeconds);
        var acceptable = difference <= toleranceSeconds;
        
        if (!acceptable)
        {
            _logger.LogDebug(
                "Duration mismatch: expected {Expected}s, actual {Actual}s, tolerance {Tolerance}s",
                expectedSeconds,
                actualSeconds,
                toleranceSeconds);
        }

        return acceptable;
    }

    /// <summary>
    /// Returns a bonus score (0-0.1) based on how close duration is.
    /// Closer duration = higher bonus.
    /// </summary>
    private double GetDurationBonus(int expectedSeconds, int actualSeconds)
    {
        var difference = Math.Abs(expectedSeconds - actualSeconds);
        
        // No bonus if difference > 5 seconds
        if (difference > 5)
            return 0.0;

        // Smooth bonus: 0.1 at 0 difference, 0 at 5+ difference
        return Math.Max(0.0, 0.1 * (1.0 - (difference / 5.0)));
    }

    /// <summary>
    /// Calculates string similarity using Levenshtein Distance.
    /// Returns a score from 0 (completely different) to 1 (identical).
    /// </summary>
    private double CalculateSimilarity(string expected, string actual)
    {
        if (string.IsNullOrEmpty(expected) && string.IsNullOrEmpty(actual))
            return 1.0;

        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual))
            return 0.0;

        // Normalize for case-insensitive comparison
        var exp = expected.ToLowerInvariant().Trim();
        var act = actual.ToLowerInvariant().Trim();

        // Exact match
        if (exp == act)
            return 1.0;

        // Calculate Levenshtein Distance
        var distance = LevenshteinDistance(exp, act);
        var maxLength = Math.Max(exp.Length, act.Length);

        // Convert distance to similarity score
        var similarity = 1.0 - (distance / (double)maxLength);
        return Math.Max(0.0, similarity);
    }

    /// <summary>
    /// Calculates Levenshtein Distance between two strings.
    /// Distance = minimum number of single-character edits (insert, delete, substitute).
    /// </summary>
    private int LevenshteinDistance(string s1, string s2)
    {
        if (s1.Length == 0)
            return s2.Length;

        if (s2.Length == 0)
            return s1.Length;

        var dp = new int[s1.Length + 1, s2.Length + 1];

        // Initialize first row and column
        for (int i = 0; i <= s1.Length; i++)
            dp[i, 0] = i;

        for (int j = 0; j <= s2.Length; j++)
            dp[0, j] = j;

        // Fill the matrix
        for (int i = 1; i <= s1.Length; i++)
        {
            for (int j = 1; j <= s2.Length; j++)
            {
                var cost = s1[i - 1] == s2[j - 1] ? 0 : 1;

                dp[i, j] = Math.Min(
                    Math.Min(
                        dp[i - 1, j] + 1,        // deletion
                        dp[i, j - 1] + 1),      // insertion
                    dp[i - 1, j - 1] + cost);   // substitution
            }
        }

        return dp[s1.Length, s2.Length];
    }
}
