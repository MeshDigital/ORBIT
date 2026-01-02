using System;
using System.Collections.Generic;
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
    public int Bitrate { get; set; } // Added for UpgradeScout
    
    // Metadata for re-hydration
    public DateTime AddedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? CoverArtUrl { get; set; } // Added for Album Art

    // Spotify Metadata (Phase 0: Metadata Gravity Well)
    public string? SpotifyTrackId { get; set; }
    public string? ISRC { get; set; }
    public string? SpotifyAlbumId { get; set; }
    public string? SpotifyArtistId { get; set; }
    public string? AlbumArtUrl { get; set; }
    public string? ArtistImageUrl { get; set; }
    public string? Genres { get; set; }
    public int? Popularity { get; set; }
    public int? CanonicalDuration { get; set; }
    public DateTime? ReleaseDate { get; set; }

    // Phase 0.1: Musical Intelligence & Antigravity
    public string? MusicalKey { get; set; } // e.g. "8A"
    public double? BPM { get; set; } // e.g. 128.0
    public double? Energy { get; set; } // 0.0 - 1.0 (Spotify)
    public double? Valence { get; set; } // 0.0 - 1.0 (Spotify)
    public double? Danceability { get; set; } // 0.0 - 1.0 (Spotify)
    public string? CuePointsJson { get; set; } // Rekordbox/DJ cue points blob
    public string? AudioFingerprint { get; set; } // Chromaprint/SoundFingerprinting hash
    public int? BitrateScore { get; set; } // Quality score for replacement
    public double? AnalysisOffset { get; set; } // Silence offset for time alignment
    
    // Phase 3A: Dual-Truth Metadata
    public double? SpotifyBPM { get; set; }
    public string? SpotifyKey { get; set; }
    public double? ManualBPM { get; set; }
    public string? ManualKey { get; set; }

    // Phase 8: Sonic Integrity & Spectral Analysis
    public IntegrityLevel Integrity { get; set; } = IntegrityLevel.None; // Phase 3B: Dual-Truth Verification
    public string? SpectralHash { get; set; } // Headless frequency histogram hash
    public double? QualityConfidence { get; set; } // 0.0 - 1.0 confidence score
    public int? FrequencyCutoff { get; set; } // Detected frequency limit in Hz
    public bool? IsTrustworthy { get; set; } // False if flagged as upscaled/fake
    public string? QualityDetails { get; set; } // Analysis details
    
    // New Flag
    public bool IsEnriched { get; set; } = false;
    
    // Phase 5: Self-Healing Library - Upgrade Tracking
    public DateTime? LastUpgradeScanAt { get; set; }
    public DateTime? LastUpgradeAt { get; set; }
    public DateTime? NextRetryTime { get; set; } // Phase 5: Ghost File Deferral
    public string? UpgradeSource { get; set; } // "Auto" or "Manual"
    public string? PreviousBitrate { get; set; } // e.g., "128kbps MP3" before upgrade

    // Phase 3C: Advanced Queue Orchestration
    public int Priority { get; set; } = 1;
    public Guid? SourcePlaylistId { get; set; }
    public string? SourcePlaylistName { get; set; }

    // Phase 13: Per-Track Filter Overrides
    public string? PreferredFormats { get; set; }
    public int? MinBitrateOverride { get; set; }
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
    public int MissingCount { get; set; }

    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }

    // Download Recovery & Hydration
    public bool IsUserPaused { get; set; } = false;         // User manually paused (don't auto-resume)
    public DateTime? DateStarted { get; set; }              // When first track started downloading
    public DateTime DateUpdated { get; set; } = DateTime.UtcNow;  // Last orchestrator touch

    /// <summary>
    /// URL for the playlist/album cover art.
    /// </summary>
    public string? AlbumArtUrl { get; set; }

    public string? SourceUrl { get; set; }
    
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
    public TrackStatus Status { get; set; } = TrackStatus.Missing;
    public string ResolvedFilePath { get; set; } = string.Empty;
    public int TrackNumber { get; set; }
    public int Bitrate { get; set; } = 0;
    public string? Format { get; set; }

    // User engagement
    public int Rating { get; set; } = 0;
    public bool IsLiked { get; set; } = false;
    public int PlayCount { get; set; } = 0;
    public DateTime? LastPlayedAt { get; set; }

    public DateTime AddedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int SortOrder { get; set; }
    
    // Spotify Metadata
    public string? SpotifyTrackId { get; set; }
    public string? ISRC { get; set; }
    public string? SpotifyAlbumId { get; set; }
    public string? SpotifyArtistId { get; set; }
    public string? AlbumArtUrl { get; set; }
    public byte[]? WaveformData { get; set; }
    public byte[]? RmsData { get; set; }
    public string? ArtistImageUrl { get; set; }
    public string? Genres { get; set; }
    public int? Popularity { get; set; }
    public int? CanonicalDuration { get; set; }
    public DateTime? ReleaseDate { get; set; }

    // Musical Intelligence
    public string? MusicalKey { get; set; }
    public double? BPM { get; set; }
    public string? CuePointsJson { get; set; }
    public string? AudioFingerprint { get; set; }
    public int? BitrateScore { get; set; }
    public double? AnalysisOffset { get; set; }
    public double? Energy { get; set; }
    public double? Danceability { get; set; }
    public double? Valence { get; set; }
    
    // Dual-Truth Metadata
    public double? SpotifyBPM { get; set; }
    public string? SpotifyKey { get; set; }
    public double? ManualBPM { get; set; }
    public string? ManualKey { get; set; }

    // Sonic Integrity
    public string? SpectralHash { get; set; }
    public double? QualityConfidence { get; set; }
    public int? FrequencyCutoff { get; set; }
    public bool? IsTrustworthy { get; set; }
    public IntegrityLevel Integrity { get; set; } = IntegrityLevel.None; // NEW
    public string? QualityDetails { get; set; }
    
    // Queue Orchestration
    public int Priority { get; set; } = 1;
    public Guid? SourcePlaylistId { get; set; }
    public string? SourcePlaylistName { get; set; }
    
    public bool IsEnriched { get; set; } = false;
    
    // Phase 15
    public string? DetectedSubGenre { get; set; }

    // Filter Overrides
    public string? PreferredFormats { get; set; }
    public int? MinBitrateOverride { get; set; }
    
    public PlaylistJobEntity? Job { get; set; }
}

/// <summary>
/// Database entity for a unique, downloaded file in the global library.
/// </summary>
public class LibraryEntryEntity
{
    [Key]
    public string UniqueHash { get; set; } = string.Empty;
    
    /// <summary>
    /// Guid identifier for service compatibility (HarmonicMatchService expects this).
    /// UniqueHash remains the primary key.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string? OriginalFilePath { get; set; }

    // Audio metadata
    public int Bitrate { get; set; }
    public int? DurationSeconds { get; set; }
    public string Format { get; set; } = string.Empty;

    // Timestamps
    public DateTime AddedAt { get; set; }
    public DateTime LastUsedAt { get; set; }
    public DateTime? FilePathUpdatedAt { get; set; }

    // Spotify Metadata
    public string? SpotifyTrackId { get; set; }
    public string? ISRC { get; set; }
    public string? SpotifyAlbumId { get; set; }
    public string? SpotifyArtistId { get; set; }
    public string? AlbumArtUrl { get; set; }
    public byte[]? WaveformData { get; set; }
    public byte[]? RmsData { get; set; }
    public string? ArtistImageUrl { get; set; }
    public string? Genres { get; set; }
    public int? Popularity { get; set; }
    public int? CanonicalDuration { get; set; }
    public DateTime? ReleaseDate { get; set; }

    // Musical Intelligence
    public string? MusicalKey { get; set; }
    public string? Key => MusicalKey; // Alias for HarmonicMatchService
    public double? BPM { get; set; }
    public double? Bpm => BPM; // Alias for HarmonicMatchService (PascalCase)
    
    // Dual-Truth Metadata
    public double? SpotifyBPM { get; set; }
    public string? SpotifyKey { get; set; }
    public double? ManualBPM { get; set; }
    public string? ManualKey { get; set; }

    public double? Energy { get; set; }
    public double? Valence { get; set; }
    public double? Danceability { get; set; }
    public string? AudioFingerprint { get; set; }
    
    // Dual-Truth Verification
    public IntegrityLevel Integrity { get; set; } = IntegrityLevel.None;
    
    public bool IsEnriched { get; set; } = false;
}
