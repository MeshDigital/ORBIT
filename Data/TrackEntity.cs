using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SLSKDONET.Models;

namespace SLSKDONET.Data;

/// <summary>
/// Database entity for a track in the persisted queue.
/// </summary>
public class TrackEntity
{
    [Key]
    public string GlobalId { get; set; } = string.Empty; // TrackUniqueHash

    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string State { get; set; } = "Pending";
    public string Filename { get; set; } = string.Empty;
    public string SoulseekUsername { get; set; } = string.Empty;
    public long Size { get; set; }
    
    // Metadata for re-hydration
    public DateTime AddedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? CoverArtUrl { get; set; } // Added for Album Art

    // Spotify Metadata (Phase 0: Metadata Gravity Well)
    public string? SpotifyTrackId { get; set; }
    public string? SpotifyAlbumId { get; set; }
    public string? SpotifyArtistId { get; set; }
    public string? AlbumArtUrl { get; set; }
    public string? ArtistImageUrl { get; set; }
    public string? Genres { get; set; }
    public int? Popularity { get; set; }
    public int? CanonicalDuration { get; set; }
    public DateTime? ReleaseDate { get; set; }
}

/// <summary>
/// Database entity for a playlist/import job header.
/// </summary>
public class PlaylistJobEntity
{
    [Key]
    public Guid Id { get; set; }

    public string SourceTitle { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty; // "Spotify", "CSV", etc.
    public string DestinationFolder { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    
    // Counts for quick access
    public int TotalTracks { get; set; }
    public int SuccessfulCount { get; set; }
    public int FailedCount { get; set; }

    // Phase 1C: Add Soft Delete Flag
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }
    
    [InverseProperty(nameof(PlaylistTrackEntity.Job))]
    public ICollection<PlaylistTrackEntity> Tracks { get; set; } = new List<PlaylistTrackEntity>();
}

/// <summary>
/// Database entity for a track within a playlist.
/// </summary>
public class PlaylistTrackEntity
{
    [Key]
    public Guid Id { get; set; }

    [ForeignKey(nameof(Job))]
    public Guid PlaylistId { get; set; }
    
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string TrackUniqueHash { get; set; } = string.Empty;
    public TrackStatus Status { get; set; } = TrackStatus.Missing; // Changed to enum
    public string ResolvedFilePath { get; set; } = string.Empty;
    public int TrackNumber { get; set; }

    // User engagement
    public int Rating { get; set; } = 0; // 1-5 stars, 0 = not rated
    public bool IsLiked { get; set; } = false;
    public int PlayCount { get; set; } = 0;
    public DateTime? LastPlayedAt { get; set; }

    public DateTime AddedAt { get; set; }
    public int SortOrder { get; set; }
    
    // Spotify Metadata (Phase 0: Metadata Gravity Well)
    /// <summary>
    /// Spotify track ID - canonical identifier for this track.
    /// Used for duplicate detection, quality guard, and recommendations.
    /// </summary>
    public string? SpotifyTrackId { get; set; }
    
    /// <summary>
    /// Spotify album ID - for artwork and album grouping.
    /// </summary>
    public string? SpotifyAlbumId { get; set; }
    
    /// <summary>
    /// Spotify artist ID - for artist pages and recommendations.
    /// </summary>
    public string? SpotifyArtistId { get; set; }
    
    /// <summary>
    /// Album artwork URL from Spotify (300x300 or larger).
    /// </summary>
    public string? AlbumArtUrl { get; set; }
    
    /// <summary>
    /// Artist image URL from Spotify.
    /// </summary>
    public string? ArtistImageUrl { get; set; }
    
    /// <summary>
    /// Genres as JSON array (e.g. ["rock", "indie"]).
    /// </summary>
    public string? Genres { get; set; }
    
    /// <summary>
    /// Spotify popularity score (0-100).
    /// </summary>
    public int? Popularity { get; set; }
    
    /// <summary>
    /// Canonical track duration from Spotify (milliseconds).
    /// Used for quality guard and fake detection.
    /// </summary>
    public int? CanonicalDuration { get; set; }
    
    /// <summary>
    /// Release date from Spotify.
    /// </summary>
    public DateTime? ReleaseDate { get; set; }

    // Phase 0.1: Musical Intelligence & Antigravity
    public string? MusicalKey { get; set; } // e.g. "8A"
    public double? BPM { get; set; } // e.g. 128.0
    public string? CuePointsJson { get; set; } // Rekordbox/DJ cue points blob
    public string? AudioFingerprint { get; set; } // Chromaprint/SoundFingerprinting hash
    public int? BitrateScore { get; set; } // Quality score for replacement
    public double? AnalysisOffset { get; set; } // Silence offset for time alignment
    
    public PlaylistJobEntity? Job { get; set; }
}

/// <summary>
/// Database entity for a unique, downloaded file in the global library.
/// This replaces the old JSON-based LibraryEntry.
/// </summary>
public class LibraryEntryEntity
{
    [Key]
    public string UniqueHash { get; set; } = string.Empty;

    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string? OriginalFilePath { get; set; } // Track original path for resolution tracking

    // Audio metadata
    public int Bitrate { get; set; }
    public int? DurationSeconds { get; set; }
    public string Format { get; set; } = string.Empty;

    // Timestamps
    public DateTime AddedAt { get; set; }
    public DateTime LastUsedAt { get; set; }
    public DateTime? FilePathUpdatedAt { get; set; } // Track when path was last resolved/updated

    // Spotify Metadata (Phase 0: Metadata Gravity Well)
    public string? SpotifyTrackId { get; set; }
    public string? SpotifyAlbumId { get; set; }
    public string? SpotifyArtistId { get; set; }
    public string? AlbumArtUrl { get; set; }
    public string? ArtistImageUrl { get; set; }
    public string? Genres { get; set; } // JSON array
    public int? Popularity { get; set; }
    public int? CanonicalDuration { get; set; } // milliseconds
    public DateTime? ReleaseDate { get; set; }

    // Phase 0.1: Musical Intelligence & Antigravity
    public string? MusicalKey { get; set; }
    public double? BPM { get; set; }
    public string? AudioFingerprint { get; set; }
}
