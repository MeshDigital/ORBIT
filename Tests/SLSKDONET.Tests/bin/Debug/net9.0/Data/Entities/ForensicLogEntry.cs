using System;
using System.ComponentModel.DataAnnotations;

namespace SLSKDONET.Data.Entities;

/// <summary>
/// Forensic log entry for track-scoped debugging.
/// Each track gets a CorrelationId that follows it through the entire pipeline:
/// Discovery → Matching → Download → Integrity → Analysis → Persistence
/// </summary>
public class ForensicLogEntry
{
    [Key]
    public int Id { get; set; }
    
    /// <summary>
    /// Unique correlation ID (GUID) that tracks a file through the entire pipeline
    /// </summary>
    [Required]
    [MaxLength(36)]
    public string CorrelationId { get; set; } = string.Empty;
    
    /// <summary>
    /// Track identifier (TrackUniqueHash or GlobalId)
    /// </summary>
    [MaxLength(64)]
    public string? TrackIdentifier { get; set; }
    
    /// <summary>
    /// Pipeline stage where this log was generated
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Stage { get; set; } = string.Empty;
    
    /// <summary>
    /// Log level (Debug, Info, Warning, Error)
    /// </summary>
    [Required]
    [MaxLength(20)]
    public string Level { get; set; } = "Info";
    
    /// <summary>
    /// The actual log message
    /// </summary>
    [Required]
    public string Message { get; set; } = string.Empty;
    
    /// <summary>
    /// Optional structured data (JSON) for detailed diagnostics
    /// </summary>
    public string? Data { get; set; }
    
    /// <summary>
    /// Timestamp when this log entry was created
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Duration in milliseconds (for measuring stage performance)
    /// </summary>
    public long? DurationMs { get; set; }
}

/// <summary>
/// Pipeline stages for categorization
/// </summary>
public static class ForensicStage
{
    public const string Discovery = "Discovery";
    public const string Matching = "Matching";
    public const string Download = "Download";
    public const string IntegrityCheck = "IntegrityCheck";
    public const string MusicalAnalysis = "MusicalAnalysis";
    public const string Persistence = "Persistence";
    public const string CueGeneration = "CueGeneration"; // Phase 4 specific
    public const string AnalysisQueue = "AnalysisQueue"; // New stage for queue orchestration
}

/// <summary>
/// Log levels for filtering
/// </summary>
public static class ForensicLevel
{
    public const string Debug = "Debug";
    public const string Info = "Info";
    public const string Warning = "Warning";
    public const string Error = "Error";
}
