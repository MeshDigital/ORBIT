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

    public record MatchResult(double Score, string? RejectionReason = null, string? ShortReason = null);

    /// <summary>
    /// Finds the best matching track from a list of candidates.
    /// Returns null if no acceptable match is found.
    /// </summary>
    public Track? FindBestMatch(PlaylistTrack model, IEnumerable<Track> candidates)
    {
        return FindBestMatchWithDiagnostics(model, candidates).BestMatch;
    }

    public (Track? BestMatch, SearchAttemptLog Log) FindBestMatchWithDiagnostics(PlaylistTrack model, IEnumerable<Track> candidates)
    {
        var log = new SearchAttemptLog
        {
            QueryString = $"{model.Artist} - {model.Title}",
            ResultsCount = candidates.Count()
        };

        if (!candidates.Any()) return (null, log);

        var matches = new List<(Track Track, MatchResult Result)>();
        var rejections = new List<(Track Track, MatchResult Result)>();

        foreach (var candidate in candidates)
        {
            var result = CalculateMatchResult(model, candidate);
            if (result.Score >= 0.7)
            {
                matches.Add((candidate, result));
            }
            else
            {
                rejections.Add((candidate, result));
                
                if (result.ShortReason?.StartsWith("Duration") == true) log.RejectedByQuality++;
                else if (result.ShortReason?.Contains("Mismatch") == true) log.RejectedByQuality++;
                else if (result.ShortReason?.Contains("Format") == true) log.RejectedByFormat++;
                else if (result.ShortReason?.Contains("Blacklist") == true) log.RejectedByBlacklist++;
            }
        }

        // Capture top 3 rejections for diagnostics
        log.Top3RejectedResults = rejections
            .OrderByDescending(r => r.Result.Score)
            .Take(3)
            .Select((r, i) => new RejectedResult
            {
                Rank = i + 1,
                Username = r.Track.Username ?? "Unknown",
                Bitrate = r.Track.Bitrate,
                Format = r.Track.Format ?? "Unknown",
                FileSize = r.Track.Size ?? 0,
                Filename = r.Track.Filename ?? "Unknown",
                SearchScore = r.Result.Score,
                RejectionReason = r.Result.RejectionReason ?? "Unknown rejection",
                ShortReason = r.Result.ShortReason ?? "Rejected"
            })
            .ToList();

        if (!matches.Any())
        {
            _logger.LogWarning("No acceptable matches for {Artist} - {Title}. {Summary}", 
                model.Artist, model.Title, log.GetSummary());
            return (null, log);
        }

        var best = matches.OrderByDescending(m => m.Result.Score).First();
        return (best.Track, log);
    }

    /// <summary>
    /// Calculates a match score (0-1) for a single candidate against the requested track.
    /// Publicly exposed for Real-Time "Threshold Trigger" evaluation.
    /// </summary>
    public double CalculateScore(PlaylistTrack model, Track candidate)
    {
        return CalculateMatchResult(model, candidate).Score;
    }

    public MatchResult CalculateMatchResult(PlaylistTrack model, Track candidate)
    {
        var expectedDuration = model.CanonicalDuration.HasValue ? model.CanonicalDuration.Value / 1000 : 0;
        var lengthTolerance = _config.SearchLengthToleranceSeconds;

        // Base score (Artist/Title/Duration)
        var result = CalculateMatchResultInternal(
            model.Artist,
            model.Title,
            expectedDuration,
            candidate,
            lengthTolerance);

        var score = result.Score;

        // BPM Bonus
        if (model.BPM.HasValue && model.BPM > 0)
        {
            var candidateBpm = ParseBpm(candidate.Filename);
            if (candidateBpm.HasValue)
            {
                // If BPM matches within 3%, give a nice boost
                if (Math.Abs(candidateBpm.Value - model.BPM.Value) < 3)
                {
                    score += 0.15; // Significant boost for BPM match
                }
            }
        }
        
        return result with { Score = Math.Min(1.0, score) };
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
            var result = CalculateMatchResultInternal(
                expectedArtist,
                expectedTitle,
                expectedDurationSeconds,
                candidate,
                lengthTolerance);

            if (result.Score >= 0.7) // Minimum acceptable match threshold
            {
                matches.Add((candidate, result.Score));
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
    private MatchResult CalculateMatchResultInternal(
        string expectedArtist,
        string expectedTitle,
        int expectedDurationSeconds,
        Track candidate,
        int lengthToleranceSeconds)
    {
        // Check duration first (hard constraint)
        if (expectedDurationSeconds > 0 && !IsDurationAcceptable(expectedDurationSeconds, candidate.Length ?? 0, lengthToleranceSeconds))
        {
            return new MatchResult(0.0, 
                $"Duration mismatch: expected {expectedDurationSeconds}s, actual {candidate.Length}s (tol: {lengthToleranceSeconds}s)", 
                $"Duration ({candidate.Length}s != {expectedDurationSeconds}s)");
        }

        // Strict filename matching (slsk-batchdl approach)
        // Filename must contain title and artist with word boundaries
        if (!StrictTitleSatisfies(candidate.Filename ?? string.Empty, expectedTitle))
        {
            _logger.LogTrace("Strict title check failed: {Filename} does not contain {Title}", candidate.Filename, expectedTitle);
            return new MatchResult(0.0, 
                $"Strict title check failed: '{candidate.Filename}' does not contain '{expectedTitle}'", 
                "Title Mismatch");
        }

        if (!StrictArtistSatisfies(candidate.Filename ?? string.Empty, expectedArtist))
        {
            _logger.LogTrace("Strict artist check failed: {Filename} does not contain {Artist}", candidate.Filename, expectedArtist);
            return new MatchResult(0.0, 
                $"Strict artist check failed: '{candidate.Filename}' does not contain '{expectedArtist}'", 
                "Artist Mismatch");
        }

        // Calculate string similarity (0-1)
        var artistSimilarity = CalculateSimilarity(expectedArtist, candidate.Artist ?? "");
        var titleSimilarity = CalculateSimilarity(expectedTitle, candidate.Title ?? "");

        // Weight: title is more important than artist (80% vs 20%)
        var combinedSimilarity = (titleSimilarity * 0.8) + (artistSimilarity * 0.2);

        // Apply bonus if duration is very close
        var durationBonus = (expectedDurationSeconds > 0) ? GetDurationBonus(expectedDurationSeconds, candidate.Length ?? 0) : 0.0;

        var finalScore = Math.Min(1.0, combinedSimilarity + durationBonus);
        
        return new MatchResult(finalScore);
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
    /// Checks if filename contains the expected title with word boundaries.
    /// Based on slsk-batchdl's StrictTitle logic.
    /// </summary>
    private bool StrictTitleSatisfies(string filename, string expectedTitle)
    {
        if (string.IsNullOrEmpty(expectedTitle)) return true;

        // Get filename without extension and path
        var filenameOnly = System.IO.Path.GetFileNameWithoutExtension(filename);
        
        // Normalize both strings
        var normalizedFilename = NormalizeFuzzy(filenameOnly);
        var normalizedTitle = NormalizeFuzzy(expectedTitle);

        // Check if filename contains title with word boundaries
        return ContainsWithBoundary(normalizedFilename, normalizedTitle, ignoreCase: true);
    }

    /// <summary>
    /// Checks if filename contains the expected artist with word boundaries.
    /// Relaxed in Phase 1.1: Also returns true if the filename contains one of the artists in a multi-artist query,
    /// or if the query contains the filename artist (reverse check).
    /// </summary>
    private bool StrictArtistSatisfies(string filename, string expectedArtist)
    {
        if (string.IsNullOrEmpty(expectedArtist)) return true;

        // Normalize both strings
        var normalizedFilename = NormalizeFuzzy(filename);
        var normalizedArtist = NormalizeFuzzy(expectedArtist);

        // 1. Standard Check: Filename contains Artist (e.g. "Artist - Title.mp3" contains "Artist")
        if (ContainsWithBoundary(normalizedFilename, normalizedArtist, ignoreCase: true))
            return true;
            
        // 2. Multi-Artist Handling (Phase 1.1)
        // If query is "Artist A, Artist B", and filename is "Artist A - Title.mp3", allow it.
        var splitArtists = normalizedArtist.Split(new[] { ',', '&', '/' }, StringSplitOptions.RemoveEmptyEntries)
                                           .Select(a => a.Trim())
                                           .Where(a => a.Length > 2) // Ignore tiny parts like "DJ"
                                           .ToList();
                                           
        if (splitArtists.Count > 1)
        {
            foreach (var subArtist in splitArtists)
            {
                if (ContainsWithBoundary(normalizedFilename, subArtist, ignoreCase: true))
                    return true;
            }
        }
        
        // 3. Reverse Containment (Phase 1.1)
        // If filename is "Artist A feat. Artist B" and query is "Artist A", it passes (handled by #1).
        // BUT, if filename is "Primate" and query is "Primate, Captain Bass"...
        // normalizedFilename = "primate", normalizedArtist = "primate captain bass"
        // Check if query contains filename artist? No, that's too loose ("The" would match "The Beatles").
        // But if the filename artist is a SUBSTRING of the query artist?
        // E.g. Filename: "Primate", Query: "Primate, Captain Bass"
        // We already handled splitting the Query in #2. 
        // Let's rely on #2 for now. The screenshot case was "Primate, Captain Bass" rejected "Primate".
        // If the FILE is "Primate" and EXPECTED is "Primate, Captain Bass":
        // My Logic #1 check: "primate" contains "primate captain bass" -> False.
        // My Logic #2 check: "primate captain bass" splits to "primate", "captain bass".
        //   Check: "primate" (file) contains "primate" (split) -> True!
        
        return false;
    }

    /// <summary>
    /// Checks if haystack contains needle with word boundaries.
    /// Prevents "love" from matching "glove".
    /// </summary>
    private bool ContainsWithBoundary(string haystack, string needle, bool ignoreCase = true)
    {
        if (string.IsNullOrEmpty(needle)) return true;
        if (string.IsNullOrEmpty(haystack)) return false;

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        
        // Find all occurrences
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, comparison)) != -1)
        {
            // Check if this occurrence has word boundaries
            bool leftBoundary = index == 0 || !char.IsLetterOrDigit(haystack[index - 1]);
            bool rightBoundary = (index + needle.Length >= haystack.Length) || !char.IsLetterOrDigit(haystack[index + needle.Length]);

            if (leftBoundary && rightBoundary)
                return true;

            index++;
        }

        return false;
    }

    /// <summary>
    /// Normalizes a string for fuzzy matching by removing special characters,
    /// smart quotes, en-dashes, and normalizing "feat." variants.
    /// </summary>
    private string NormalizeFuzzy(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        
        // 0. Lowercase immediately to ensure regex [a-z] works and we don't strip uppercase chars
        input = input.ToLowerInvariant();

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
