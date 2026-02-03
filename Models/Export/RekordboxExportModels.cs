using System;
using System.Collections.Generic;

namespace SLSKDONET.Models
{
    /// <summary>
    /// Normalized export model for a track, independent of target format.
    /// Maps ORBIT's internal data to export-friendly structure.
    /// </summary>
    public class ExportTrack
    {
        // Core Metadata
        public string TrackId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public string Genre { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public double Bpm { get; set; }
        public TimeSpan Duration { get; set; }
        public string Comments { get; set; } = string.Empty;
        
        // File Information
        public string FilePath { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public int BitRate { get; set; }
        public int SampleRate { get; set; }
        
        // ORBIT Intelligence (encoded in comments/custom fields)
        public string StructuralHash { get; set; } = string.Empty;
        public double EnergyLevel { get; set; }
        public double VocalDensity { get; set; }
        public string ForensicNotes { get; set; } = string.Empty;
        
        // Cue Points
        public List<ExportCue> Cues { get; set; } = new();
        
        // Beatgrid (optional)
        public ExportBeatgrid? Beatgrid { get; set; }
        
        // Surgical Edit Lineage (if this is an edited track)
        public string? OriginalTrackId { get; set; }
        public bool IsSurgicallyEdited { get; set; }
    }

    /// <summary>
    /// Normalized cue point model supporting both Hot Cues and Memory Cues.
    /// </summary>
    public class ExportCue
    {
        public CueType Type { get; set; }
        public int Number { get; set; } // Hot Cue number (0-7) or Memory Cue index
        public TimeSpan Position { get; set; }
        public string Name { get; set; } = string.Empty;
        public CueColor Color { get; set; }
        
        // ORBIT-specific metadata
        public string? StructuralLabel { get; set; } // e.g., "INTRO", "DROP", "BREAKDOWN"
        public string? TransitionNote { get; set; } // e.g., "Transition to Track X"
    }

    /// <summary>
    /// Cue point type (Rekordbox distinguishes between Hot and Memory cues)
    /// </summary>
    public enum CueType
    {
        HotCue,      // User-triggerable performance cue
        MemoryCue,   // Structural landmark for navigation
        LoopCue      // Loop point (future)
    }

    /// <summary>
    /// Rekordbox cue colors (mapped to ORBIT's structural intelligence)
    /// </summary>
    public enum CueColor
    {
        Red,      // Drops, high-energy moments
        Orange,   // Builds, transitions
        Yellow,   // Breakdowns, mid-energy
        Green,    // Intros, outros
        Blue,     // Vocals, special moments
        Purple,   // Custom/user-defined
        Pink,     // Experimental
        White     // Default
    }

    /// <summary>
    /// Beatgrid export data (optional, for tempo-synced playback)
    /// </summary>
    public class ExportBeatgrid
    {
        public double Bpm { get; set; }
        public TimeSpan FirstBeatPosition { get; set; }
        public bool IsConstantTempo { get; set; }
        
        // For variable BPM tracks (future)
        public List<BeatMarker>? BeatMarkers { get; set; }
    }

    public class BeatMarker
    {
        public TimeSpan Position { get; set; }
        public double Bpm { get; set; }
    }

    /// <summary>
    /// Normalized playlist/set model for export.
    /// </summary>
    public class ExportPlaylist
    {
        public string PlaylistId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime LastModifiedAt { get; set; }
        
        // ORBIT Set Intelligence
        public double FlowHealth { get; set; }
        public string SetNotes { get; set; } = string.Empty;
        
        // Tracks in order
        public List<ExportPlaylistTrack> Tracks { get; set; } = new();
        
        // Nested playlists (for folder structure)
        public List<ExportPlaylist> Children { get; set; } = new();
    }

    /// <summary>
    /// Track reference within a playlist, with position and transition metadata.
    /// </summary>
    public class ExportPlaylistTrack
    {
        public string TrackId { get; set; } = string.Empty;
        public int Position { get; set; }
        
        // ORBIT Transition Intelligence
        public string? TransitionType { get; set; } // "Long Blend", "Quick Cut", etc.
        public double? TransitionOffset { get; set; }
        public string? TransitionReasoning { get; set; }
        public string? DjNotes { get; set; }
    }
}

