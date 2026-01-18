using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

[Table("audio_analysis")]
public class AudioAnalysisEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Foreign key link to the LibraryEntry via TrackUniqueHash.
    /// </summary>
    [Required]
    public string TrackUniqueHash { get; set; } = string.Empty;

    public int Bitrate { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public string Codec { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    
    // Loudness & Dynamics
    public double LoudnessLufs { get; set; }
    public double TruePeakDb { get; set; }
    public double DynamicRange { get; set; }



    public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;

    // Phase 3.5: Integrity Scout (Sonic Truth)
    public bool IsUpscaled { get; set; }
    public string SpectralHash { get; set; } = string.Empty; // For deduplication/verification
    public int FrequencyCutoff { get; set; } // e.g. 16000 for 128kbps, 20000+ for 320kbps
    public double QualityConfidence { get; set; } // 0.0 - 1.0
}
