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
    /// <summary>
    /// Finds the best matching track using rich metadata (BPM, Duration, etc.) from PlaylistTrack.
    /// </summary>
    public Track? FindBestMatch(PlaylistTrack model, IEnumerable<Track> candidates)
    {
        if (!candidates.Any()) return null;

        var expectedDuration = model.CanonicalDuration.HasValue ? model.CanonicalDuration.Value / 1000 : 0;
        
        // Use existing logic if we don't have rich metadata
        if (expectedDuration == 0)
        {
             return FindBestMatch(model.Artist, model.Title, 0, candidates);
        }

        var lengthTolerance = _config.SearchLengthToleranceSeconds;
        var matches = new List<(Track Track, double Score)>();

        foreach (var candidate in candidates)
        {
            // Base score (Artist/Title/Duration)
            var score = CalculateMatchScore(
                model.Artist,
                model.Title,
                expectedDuration,
                candidate,
                lengthTolerance);

            // BPM Bonus (Phase 2)
            if (model.BPM.HasValue && model.BPM > 0)
            {
                var candidateBpm = ParseBpm(candidate.Filename);
                if (candidateBpm.HasValue)
                {
                    // If BPM matches within 3%, give a nice boost
                    if (Math.Abs(candidateBpm.Value - model.BPM.Value) < 3)
                    {
                        score += 0.15; // Significant boost for BPM match
                        _logger.LogTrace("BPM Match! {CandidateBpm} vs {ModelBpm}", candidateBpm, model.BPM);
                    }
                }
            }

            if (score >= 0.7)
            {
                matches.Add((candidate, score));
            }
        }

        if (!matches.Any())
        {
            _logger.LogWarning("No acceptable matches for {Artist} - {Title}", model.Artist, model.Title);
            return null;
        }

        var best = matches.OrderByDescending(m => m.Score).First();
        return best.Track;
    }

    /// <summary>
    /// Simple parser to extract BPM from filename (e.g. "128bpm", "(128 BPM)").
    /// </summary>
    private int? ParseBpm(string? filename)
    {
        if (string.IsNullOrEmpty(filename)) return null;
        try 
        {
            // Simple regex for "128bpm" or "128 bpm"
            var match = System.Text.RegularExpressions.Regex.Match(filename, @"\b(\d{2,3})\s*bpm\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int bpm))
            {
                return bpm;
            }
        } 
        catch { }
        return null;
    }

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
        if (expectedDurationSeconds > 0 && !IsDurationAcceptable(expectedDurationSeconds, candidate.Length ?? 0, lengthToleranceSeconds))
            return 0.0;

        // Calculate string similarity (0-1)
        var artistSimilarity = CalculateSimilarity(expectedArtist, candidate.Artist ?? "");
        var titleSimilarity = CalculateSimilarity(expectedTitle, candidate.Title ?? "");

        // Weight: title is more important than artist (80% vs 20%)
        var combinedSimilarity = (titleSimilarity * 0.8) + (artistSimilarity * 0.2);

        // Apply bonus if duration is very close
        var durationBonus = (expectedDurationSeconds > 0) ? GetDurationBonus(expectedDurationSeconds, candidate.Length ?? 0) : 0.0;

        var finalScore = Math.Min(1.0, combinedSimilarity + durationBonus);
        
        // Trace log...
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

        if (_config.EnableFuzzyNormalization)
        {
            exp = NormalizeFuzzy(exp);
            act = NormalizeFuzzy(act);
        }

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

    /// <summary>
    /// Normalizes a string for fuzzy matching by removing special characters,
    /// smart quotes, en-dashes, and normalizing "feat." variants.
    /// </summary>
    private string NormalizeFuzzy(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        // 1. Normalize "feat." variants
        var featNormal = System.Text.RegularExpressions.Regex.Replace(input, @"\b(feat\.?|ft\.?|featuring)\b", "feat", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // 2. Normalize dashes (smart quotes, long dashes)
        var dashNormal = featNormal
            .Replace('—', '-') // Em-dash
            .Replace('–', '-') // En-dash
            .Replace('′', '\'') // Smart single quote
            .Replace('‘', '\'') // Smart single quote
            .Replace('’', '\'') // Smart single quote
            .Replace('″', '\"') // Smart double quote
            .Replace('“', '\"') // Smart double quote
            .Replace('”', '\"'); // Smart double quote

        // 3. Remove other non-alphanumeric frictional characters (except space, dash, quote)
        var frictionalNormal = System.Text.RegularExpressions.Regex.Replace(dashNormal, @"[^a-z0-9\s\-\']", "");

        // 4. Collapse whitespace
        return System.Text.RegularExpressions.Regex.Replace(frictionalNormal, @"\s+", " ").Trim();
    }
}
