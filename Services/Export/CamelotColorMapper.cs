using System;
using System.Collections.Generic;

namespace SLSKDONET.Services.Export;

/// <summary>
/// Maps Camelot keys to high-contrast hex colors for Emergency Card PDF.
/// Uses the Camelot wheel color system for instant harmonic recognition.
/// </summary>
public static class CamelotColorMapper
{
    /// <summary>
    /// Camelot color palette - optimized for dark-mode booth legibility.
    /// Colors are bright against black background.
    /// </summary>
    private static readonly Dictionary<string, string> CamelotColors = new()
    {
        // A Keys (Minor) - Cooler tones
        ["1A"] = "#5B9BD5",  // Cool Blue
        ["2A"] = "#70AD47",  // Forest Green
        ["3A"] = "#FFC000",  // Gold
        ["4A"] = "#ED7D31",  // Orange
        ["5A"] = "#C00000",  // Deep Red
        ["6A"] = "#7030A0",  // Purple
        ["7A"] = "#00B0F0",  // Cyan
        ["8A"] = "#00B050",  // Bright Green
        ["9A"] = "#FFFF00",  // Yellow
        ["10A"] = "#FF6600", // Tangerine
        ["11A"] = "#FF0066", // Hot Pink
        ["12A"] = "#9933FF", // Violet

        // B Keys (Major) - Warmer tones
        ["1B"] = "#4472C4",  // Royal Blue
        ["2B"] = "#548235",  // Olive Green
        ["3B"] = "#BF9000",  // Dark Gold
        ["4B"] = "#C65911",  // Burnt Orange
        ["5B"] = "#990000",  // Maroon
        ["6B"] = "#5B2C6F",  // Deep Purple
        ["7B"] = "#0080FF",  // Azure
        ["8B"] = "#008040",  // Emerald
        ["9B"] = "#CCCC00",  // Chartreuse
        ["10B"] = "#CC5200", // Rust
        ["11B"] = "#CC0052", // Raspberry
        ["12B"] = "#7A29CC", // Indigo
    };

    /// <summary>
    /// Gets the hex color for a Camelot key.
    /// </summary>
    public static string GetHexColor(string? camelotKey)
    {
        if (string.IsNullOrWhiteSpace(camelotKey))
            return "#808080"; // Gray for unknown

        var key = camelotKey.Trim().ToUpperInvariant();
        return CamelotColors.TryGetValue(key, out var color) ? color : "#808080";
    }

    /// <summary>
    /// Determines if text should be black or white based on background color brightness.
    /// </summary>
    public static bool ShouldUseBlackText(string hexColor)
    {
        // Parse hex color
        if (hexColor.StartsWith("#") && hexColor.Length == 7)
        {
            var r = Convert.ToInt32(hexColor.Substring(1, 2), 16);
            var g = Convert.ToInt32(hexColor.Substring(3, 2), 16);
            var b = Convert.ToInt32(hexColor.Substring(5, 2), 16);
            
            // Calculate relative luminance
            var luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255;
            return luminance > 0.5; // Use black text on bright backgrounds
        }
        return false;
    }
}
