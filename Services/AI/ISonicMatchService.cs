using System.Collections.Generic;
using System.Threading.Tasks;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Services.AI;

/// <summary>
/// AI-powered track similarity engine using Essentia analysis data.
/// Uses Euclidean distance in 3D vibe space (Arousal, Valence, Danceability).
/// </summary>
public interface ISonicMatchService
{
    /// <summary>
    /// Finds tracks with a similar "vibe" based on Essentia AI analysis.
    /// </summary>
    /// <param name="sourceTrackHash">UniqueHash of the source track</param>
    /// <param name="limit">Maximum number of matches to return</param>
    /// <returns>List of similar tracks ordered by sonic distance (closest first)</returns>
    Task<List<SonicMatch>> FindSonicMatchesAsync(string sourceTrackHash, int limit = 20);
    
    /// <summary>
    /// Calculates the weighted Euclidean distance between two audio feature sets.
    /// </summary>
    /// <param name="a">First track's audio features</param>
    /// <param name="b">Second track's audio features</param>
    /// <returns>Distance (0 = identical, higher = more different)</returns>
    double CalculateSonicDistance(AudioFeaturesEntity a, AudioFeaturesEntity b);
}

/// <summary>
/// Represents a track match with its sonic distance from the source.
/// </summary>
public class SonicMatch
{
    public string TrackUniqueHash { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public double Distance { get; set; }
    
    /// <summary>
    /// Human-readable reason for the match (Twin Vibe, Energy Match, etc.)
    /// </summary>
    public string MatchReason { get; set; } = "Compatible";

    /// <summary>
    /// Source of the match (ISRC, AI, Hybrid, etc.)
    /// </summary>
    public string MatchSource { get; set; } = "AI";

    /// <summary>
    /// Confidence in the match result (0.0 - 1.0)
    /// </summary>
    public double Confidence { get; set; }
    
    /// <summary>
    /// Similarity as a percentage (100% = identical, 0% = completely different)
    /// </summary>
    public double SimilarityPercent => Math.Max(0, 100 - (Distance * 10));
    
    // Feature breakdown for UI display
    public float Arousal { get; set; }
    public float Valence { get; set; }
    public float Danceability { get; set; }
    public string? MoodTag { get; set; }
    public double Bpm { get; set; }
}
