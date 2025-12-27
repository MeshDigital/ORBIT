using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SLSKDONET.Data.Entities;

[Table("audio_features")]
public class AudioFeaturesEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Foreign key link to the LibraryEntry via TrackUniqueHash.
    /// </summary>
    [Required]
    public string TrackUniqueHash { get; set; } = string.Empty;

    // Musical Intelligence (Essentia / Aubio)
    public float Bpm { get; set; }
    public string Key { get; set; } = string.Empty; // e.g., "7A" or "Cm"
    public string Scale { get; set; } = string.Empty; // "major", "minor"
    
    public double Energy { get; set; } // 0.0 - 1.0
    public double Danceability { get; set; } // 0.0 - 1.0
    
    public string Fingerprint { get; set; } = string.Empty; // AcoustID / Chromaprint

    public DateTime AnalyzedAt { get; set; } = DateTime.UtcNow;
}
