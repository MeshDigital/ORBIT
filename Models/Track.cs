using System.IO;

namespace SLSKDONET.Models;

/// <summary>
/// Represents a music track found on Soulseek.
/// </summary>
public class Track
{
    public string? Filename { get; set; }
    public string? Directory { get; set; } // Added for Album Grouping
    public string? Artist { get; set; }
    public string? Title { get; set; }
    public string? Album { get; set; }
    public long? Size { get; set; }
    public string? Username { get; set; }
    public string? Format { get; set; }
    public int? Length { get; set; } // in seconds
    public int Bitrate { get; set; } // in kbps
    public Dictionary<string, object>? Metadata { get; set; }
    
    // Intelligence Metrics
    public bool HasFreeUploadSlot { get; set; }
    public int QueueLength { get; set; }
    public int UploadSpeed { get; set; } // Bytes per second

    /// <summary>
    /// Local filesystem path where the track was stored (if known).
    /// </summary>
    public string? LocalPath { get; set; }

    /// <summary>
    /// Absolute file path of the downloaded file, or expected final path if not yet downloaded.
    /// Used for library tracking and Rekordbox export.
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Name of the source playlist (e.g., Spotify playlist name or CSV filename).
    /// Temporary field used during parsing before tracks are added to PlaylistJob.
    /// </summary>
    public string? SourceTitle { get; set; }

    public bool IsSelected { get; set; } = false;
    public Soulseek.File? SoulseekFile { get; set; }
    
    /// <summary>
    /// Original index from the search results (before sorting/filtering).
    /// Allows user to reset view to original search order.
    /// </summary>
    public int OriginalIndex { get; set; } = -1;
    
    /// <summary>
    /// Current ranking score for this result.
    /// Higher = better match. Used for sorting display.
    /// </summary>
    public double CurrentRank { get; set; } = 0.0;

    /// <summary>
    /// Indicates whether this track already exists in the user's library.
    /// Used by ImportPreview to show duplicate status.
    /// </summary>
    public bool IsInLibrary { get; set; } = false;

    /// <summary>
    /// Unique hash for deduplication: artist-title combination (lowercase, no spaces).
    /// </summary>
    public string UniqueHash => $"{Artist?.ToLower().Replace(" ", "")}-{Title?.ToLower().Replace(" ", "")}".TrimStart('-').TrimEnd('-');

    /// <summary>
    /// Gets the file extension from the filename.
    /// </summary>
    public string GetExtension()
    {
        if (string.IsNullOrEmpty(Filename))
            return "";
        return Path.GetExtension(Filename).TrimStart('.');
    }

    /// <summary>
    /// Gets a user-friendly size representation.
    /// </summary>
    public string GetFormattedSize()
    {
        if (Size == null) return "Unknown";
        
        const long kb = 1024;
        const long mb = kb * 1024;
        const long gb = mb * 1024;

        return Size.Value switch
        {
            >= gb => $"{Size.Value / (double)gb:F2} GB",
            >= mb => $"{Size.Value / (double)mb:F2} MB",
            >= kb => $"{Size.Value / (double)kb:F2} KB",
            _ => $"{Size.Value} B"
        };
    }

    public override string ToString()
    {
        return $"{Artist} - {Title} ({Filename})";
    }
}
