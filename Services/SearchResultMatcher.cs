using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
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

    public record MatchResult(double Score, string? ScoreBreakdown = null, string? RejectionReason = null, string? ShortReason = null);

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
            if (result.Score >= 70) // Phase 1.1: Threshold is 70/100
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
                ScoreBreakdown = r.Result.ScoreBreakdown,
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
        int score = 0;
        var breakdown = new List<string>();

        // 1. Duration (40 pts)
        var expectedDuration = model.CanonicalDuration.HasValue ? model.CanonicalDuration.Value / 1000 : 0;
        if (expectedDuration > 0)
        {
            var diff = Math.Abs(expectedDuration - (candidate.Length ?? 0));
            int durationPts = diff switch
            {
                <= 2 => 40,
                <= 5 => 20,
                <= 10 => 5,
                _ => 0
            };
            score += durationPts;
            if (durationPts > 0) breakdown.Add($"Duration: +{durationPts} ({candidate.Length}s vs {expectedDuration}s)");
            else breakdown.Add($"Duration: 0 (Mismatch: {candidate.Length}s vs {expectedDuration}s)");
        }

        // Tokenization for Path Analysis
        var artistTokens = Tokenize(model.Artist);
        var titleTokens = Tokenize(model.Title);
        var allPathText = new List<string> { Path.GetFileName(candidate.Filename ?? "") };
        if (candidate.PathSegments != null) allPathText.AddRange(candidate.PathSegments);

        // 2. Artist in Path (30 pts)
        double artistMatchRatio = CalculateTokenMatchRatio(artistTokens, allPathText);
        int artistPts = (int)(30 * artistMatchRatio);
        score += artistPts;
        if (artistPts > 0) breakdown.Add($"Artist: +{artistPts} (Tokens: {string.Join(",", artistTokens.Intersect(Tokenize(string.Join(" ", allPathText))))})");

        // 3. Title in Path (20 pts)
        double titleMatchRatio = CalculateTokenMatchRatio(titleTokens, allPathText);
        int titlePts = (int)(20 * titleMatchRatio);
        score += titlePts;
        if (titlePts > 0) breakdown.Add($"Title: +{titlePts}");

        // 4. Bitrate (10 pts)
        int bitratePts = 0;
        if (candidate.Bitrate >= 320 || candidate.Format?.ToUpper() == "FLAC") bitratePts = 10;
        else if (candidate.Bitrate >= 192) bitratePts = 5;
        score += bitratePts;
        if (bitratePts > 0) breakdown.Add($"Bitrate: +{bitratePts} ({candidate.Bitrate}kbps)");

        // BPM Bonus (Extra)
        if (model.BPM.HasValue && model.BPM > 0)
        {
            var candidateBpm = ParseBpm(candidate.Filename);
            if (candidateBpm.HasValue && Math.Abs(candidateBpm.Value - model.BPM.Value) < 3)
            {
                score += 5;
                breakdown.Add("BPM Bonus: +5");
            }
        }

        var finalScore = Math.Min(100, score);
        var breakdownStr = string.Join(" | ", breakdown);
        
        string? rejection = null;
        if (finalScore < 70) rejection = $"Low confidence score: {finalScore}/100. Breakdown: {breakdownStr}";

        return new MatchResult(finalScore, breakdownStr, rejection, finalScore < 70 ? "Low Score" : null);
    }

    private List<string> Tokenize(string? input)
    {
        if (string.IsNullOrEmpty(input)) return new List<string>();
        // Normalize handles most furniture. We just split by space and dash.
        return Regex.Split(NormalizeFuzzy(input), @"[\s\-]+")
                    .Where(s => s.Length > 1) // Ignore single chars/tokens
                    .Select(s => s.ToLowerInvariant())
                    .Distinct()
                    .ToList();
    }

    private double CalculateTokenMatchRatio(List<string> searchTokens, List<string> haystack)
    {
        if (!searchTokens.Any()) return 1.0;
        
        var haystackMerged = string.Join(" ", haystack).ToLowerInvariant();
        int matched = 0;
        foreach (var token in searchTokens)
        {
            if (haystackMerged.Contains(token)) matched++;
        }
        return (double)matched / searchTokens.Count;
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

        // 1. Standard Check: Filename contains Artist
        if (ContainsWithBoundary(normalizedFilename, normalizedArtist, ignoreCase: true))
            return true;
            
        // 1.5 Prefix/Suffix Leniency (Phase 1.2)
        // Handle "The Artist" vs "Artist"
        string strippedArtist = Regex.Replace(normalizedArtist, @"\b(the|dj|mc)\b", "", RegexOptions.IgnoreCase).Trim();
        if (!string.IsNullOrEmpty(strippedArtist) && ContainsWithBoundary(normalizedFilename, strippedArtist, ignoreCase: true))
            return true;

        // 2. Multi-Artist Handling
        var splitArtists = normalizedArtist.Split(new[] { ',', '&', '/' }, StringSplitOptions.RemoveEmptyEntries)
                                           .Select(a => a.Trim())
                                           .Where(a => a.Length > 2)
                                           .ToList();
                                           
        if (splitArtists.Count > 1)
        {
            foreach (var subArtist in splitArtists)
            {
                if (ContainsWithBoundary(normalizedFilename, subArtist, ignoreCase: true))
                    return true;
                
                // Also check sub-artist with prefix leniency
                string strippedSub = Regex.Replace(subArtist, @"\b(the|dj|mc)\b", "", RegexOptions.IgnoreCase).Trim();
                if (!string.IsNullOrEmpty(strippedSub) && ContainsWithBoundary(normalizedFilename, strippedSub, ignoreCase: true))
                    return true;
            }
        }
        
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
