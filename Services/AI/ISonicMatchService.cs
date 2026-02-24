using System.Collections.Generic;
using System.Threading.Tasks;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Models.Musical;

namespace SLSKDONET.Services.AI;

/// <summary>
/// AI-powered track similarity engine.
/// Refactored in Phase 5 to provide transparent match breakdowns.
/// </summary>
public interface ISonicMatchService
{
    /// <summary>
    /// Finds tracks with a similar "vibe" based on multi-dimensional analysis.
    /// Returns a list of matches including transparency tags and breakdown scores.
    /// </summary>
    /// <param name="sourceTrackHash">UniqueHash of the source track</param>
    /// <param name="limit">Maximum number of matches to return</param>
    /// <returns>List of similar tracks ordered by confidence.</returns>
    Task<List<SonicMatch>> FindSonicMatchesAsync(string sourceTrackHash, int limit = 20);
    
    /// <summary>
    /// Calculates similarity between two audio feature sets.
    /// </summary>
    double CalculateSonicDistance(AudioFeaturesEntity a, AudioFeaturesEntity b);

    /// <summary>
    /// Finds "Bridge" tracks that sit harmonically and sonically between two tracks.
    /// </summary>
    Task<List<SonicMatch>> FindBridgeAsync(LibraryEntryEntity trackA, LibraryEntryEntity trackB, int limit = 5);
}

/// <summary>
/// Represents a track match with its rich similarity breakdown.
/// </summary>
public class SonicMatch
{
    public string TrackUniqueHash { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    
    /// <summary>Legacy distance field (lower = closer).</summary>
    public double Distance { get; set; }
    
    /// <summary>Legacy similarity percentage (0-100).</summary>
    public double SimilarityPercent => (Breakdown?.TotalConfidence ?? 0) * 100.0;

    /// <summary>Legacy MatchReason for backward compatibility.</summary>
    public string MatchReason => Breakdown?.MatchTags?.FirstOrDefault() ?? (Confidence > 0.95 ? "Seamless Match" : "Compatible");

    /// <summary>Legacy MatchSource for backward compatibility.</summary>
    public string MatchSource => Breakdown != null ? "AI (Phase 5)" : "AI";

    /// <summary>
    /// Primary source of transparency for Phase 5.
    /// Contains per-dimension scores and human-readable tags.
    /// </summary>
    public SimilarityBreakdown? Breakdown { get; set; }

    /// <summary>Legacy confidence (0.0 - 1.0).</summary>
    public double Confidence => Breakdown?.TotalConfidence ?? 0;

    public string ConfidenceLabel => Breakdown != null && Breakdown.MatchTags.Count > 0 
        ? Breakdown.MatchTags[0] 
        : (Confidence > 0.95 ? "Seamless" : "Transition");

    public string BadgeColor => Confidence > 0.95 ? "#00FF99" : (Confidence > 0.85 ? "#FFAA00" : "#666666");

    // Legacy fields for backward compatibility with older UI parts
    public float Arousal { get; set; }
    public float Valence { get; set; }
    public float Danceability { get; set; }
    public string? MoodTag { get; set; }
    public double Bpm { get; set; }
}
