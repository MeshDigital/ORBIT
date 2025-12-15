using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SLSKDONET.Models;

namespace SLSKDONET.Utils;

/// <summary>
/// Utility for parsing tracklists from YouTube comments, SoundCloud descriptions, etc.
/// Removes timestamps, filters junk lines, and extracts artist/title pairs.
/// </summary>
public static class CommentTracklistParser
{
    // Matches timestamps like: 0:00, 00:00, 1:00:00, with optional dash/separator
    private static readonly Regex TimestampRegex = new(@"^\s*[\d:]{1,8}\s*[-–—]?\s*", RegexOptions.Compiled);
    
    // Matches artist/title separator (supports: -, –, —)
    private static readonly Regex SeparatorRegex = new(@"\s*[-–—]\s*", RegexOptions.Compiled);
    
    // Keywords that indicate junk lines
    private static readonly string[] JunkKeywords = 
    { 
        "tracklist", 
        "setlist", 
        "playlist", 
        "📈", 
        "🎵", 
        "🎶",
        "track list",
        "timestamps"
    };

    /// <summary>
    /// Parse raw tracklist text into SearchQuery objects.
    /// </summary>
    /// <param name="rawText">Raw text containing tracklist (e.g., from YouTube comment)</param>
    /// <returns>List of parsed tracks</returns>
    public static List<SearchQuery> Parse(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return new List<SearchQuery>();

        var tracks = new List<SearchQuery>();
        var lines = rawText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            // 1. Remove timestamps
            var cleaned = RemoveTimestamp(line).Trim();
            
            // 2. Filter junk lines
            if (IsJunkLine(cleaned) || string.IsNullOrWhiteSpace(cleaned))
                continue;
            
            // 3. Split artist/title
            var (artist, title) = SplitArtistTitle(cleaned);
            
            // 4. Create SearchQuery if both artist and title are valid
            if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
            {
                tracks.Add(new SearchQuery
                {
                    Artist = artist.Trim(),
                    Title = title.Trim(),
                    Album = null // No album info from comments
                });
            }
        }

        return tracks;
    }

    /// <summary>
    /// Remove leading timestamp from a line.
    /// Handles formats: 0:00, 00:00, 1:00:00, with optional dash
    /// </summary>
    private static string RemoveTimestamp(string line)
    {
        return TimestampRegex.Replace(line, string.Empty);
    }

    /// <summary>
    /// Check if a line is junk (header, metadata, etc.)
    /// </summary>
    private static bool IsJunkLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return true;

        var lowerLine = line.ToLowerInvariant();
        
        // Check for junk keywords
        if (JunkKeywords.Any(keyword => lowerLine.Contains(keyword.ToLowerInvariant())))
            return true;
        
        // Filter lines that are just "ID" or "ID - ID"
        if (lowerLine.Trim() == "id" || lowerLine.Contains(" id") || lowerLine.Contains("- id"))
            return true;
        
        // Filter lines that are too short (likely not a track)
        if (line.Length < 3)
            return true;

        return false;
    }

    /// <summary>
    /// Split a cleaned line into artist and title.
    /// Handles edge cases like multiple hyphens in the title.
    /// </summary>
    private static (string Artist, string Title) SplitArtistTitle(string line)
    {
        // Split on first separator only (to handle titles with hyphens)
        var parts = SeparatorRegex.Split(line, 2);
        
        if (parts.Length == 2)
        {
            return (parts[0].Trim(), parts[1].Trim());
        }
        else if (parts.Length == 1)
        {
            // No separator found - assume it's just a title
            return ("Unknown Artist", parts[0].Trim());
        }
        
        return (string.Empty, string.Empty);
    }
}
