using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;

namespace SLSKDONET.Services;

/// <summary>
/// Professional DJ Service: Finds harmonically compatible tracks using Camelot Wheel theory.
/// Features:
/// - Perfect Key Matches (same key)
/// - Compatible Keys (±1 semitone, relative major/minor)
/// - BPM Compatibility (±6% for beatmatching)
/// - Energy/Mood Matching (Spotify metadata)
/// </summary>
public class HarmonicMatchService
{
    private readonly ILogger<HarmonicMatchService> _logger;
    private readonly DatabaseService _databaseService;

    // Camelot Wheel: Maps each key to its compatible neighbors
    private static readonly Dictionary<string, HashSet<string>> CamelotWheel = new()
    {
        // Major Keys (B notation)
        { "8B", new HashSet<string> { "8B", "8A", "7B", "9B" } },  // C Major
        { "9B", new HashSet<string> { "9B", "9A", "8B", "10B" } }, // Db Major
        { "10B", new HashSet<string> { "10B", "10A", "9B", "11B" } }, // D Major
        { "11B", new HashSet<string> { "11B", "11A", "10B", "12B" } }, // Eb Major
        { "12B", new HashSet<string> { "12B", "12A", "11B", "1B" } }, // E Major
        { "1B", new HashSet<string> { "1B", "1A", "12B", "2B" } }, // F Major
        { "2B", new HashSet<string> { "2B", "2A", "1B", "3B" } }, // Gb Major
        { "3B", new HashSet<string> { "3B", "3A", "2B", "4B" } }, // G Major
        { "4B", new HashSet<string> { "4B", "4A", "3B", "5B" } }, // Ab Major
        { "5B", new HashSet<string> { "5B", "5A", "4B", "6B" } }, // A Major
        { "6B", new HashSet<string> { "6B", "6A", "5B", "7B" } }, // Bb Major
        { "7B", new HashSet<string> { "7B", "7A", "6B", "8B" } }, // B Major
        
        // Minor Keys (A notation)
        { "5A", new HashSet<string> { "5A", "5B", "4A", "6A" } },  // A Minor
        { "6A", new HashSet<string> { "6A", "6B", "5A", "7A" } },  // Bb Minor
        { "7A", new HashSet<string> { "7A", "7B", "6A", "8A" } },  // B Minor
        { "8A", new HashSet<string> { "8A", "8B", "7A", "9A" } },  // C Minor
        { "9A", new HashSet<string> { "9A", "9B", "8A", "10A" } }, // Db Minor
        { "10A", new HashSet<string> { "10A", "10B", "9A", "11A" } }, // D Minor
        { "11A", new HashSet<string> { "11A", "11B", "10A", "12A" } }, // Eb Minor
        { "12A", new HashSet<string> { "12A", "12B", "11A", "1A" } }, // E Minor
        { "1A", new HashSet<string> { "1A", "1B", "12A", "2A" } }, // F Minor
        { "2A", new HashSet<string> { "2A", "2B", "1A", "3A" } }, // Gb Minor
        { "3A", new HashSet<string> { "3A", "3B", "2A", "4A" } }, // G Minor
        { "4A", new HashSet<string> { "4A", "4B", "3A", "5A" } }, // Ab Minor
    };

    public HarmonicMatchService(ILogger<HarmonicMatchService> logger, DatabaseService databaseService)
    {
        _logger = logger;
        _databaseService = databaseService;
    }

    /// <summary>
    /// Finds tracks that mix well with the seed track based on harmonic key and BPM.
    /// </summary>
    /// <param name="seedTrackId">The track to find matches for</param>
    /// <param name="limit">Maximum number of results</param>
    /// <param name="includeBpmRange">Allow ±6% BPM variance for beatmatching</param>
    /// <param name="includeEnergyMatch">Factor in Spotify energy/valence for vibe matching</param>
    /// <returns>List of compatible tracks with compatibility scores</returns>
    public async Task<List<HarmonicMatchResult>> GetHarmonicMatchesAsync(
        Guid seedTrackId, 
        int limit = 20,
        bool includeBpmRange = true,
        bool includeEnergyMatch = true)
    {
        try
        {
            // Get seed track
            var seedTrack = await _databaseService.GetLibraryEntryAsync(seedTrackId);
            if (seedTrack == null)
            {
                _logger.LogWarning("Seed track not found: {TrackId}", seedTrackId);
                return new List<HarmonicMatchResult>();
            }

            // Ensure seed has required data
            if (string.IsNullOrEmpty(seedTrack.Key))
            {
                _logger.LogWarning("Seed track has no key data: {TrackId}", seedTrackId);
                return new List<HarmonicMatchResult>();
            }

            // Get compatible keys from Camelot Wheel
            var compatibleKeys = GetCompatibleKeys(seedTrack.Key);
            
            // Calculate BPM range (±6% industry standard for beatmatching)
            double? minBpm = null;
            double? maxBpm = null;
            if (includeBpmRange && seedTrack.Bpm.HasValue && seedTrack.Bpm.Value > 0)
            {
                minBpm = seedTrack.Bpm.Value * 0.94; // -6%
                maxBpm = seedTrack.Bpm.Value * 1.06; // +6%
            }

            // Query library for compatible tracks
            var allTracks = await _databaseService.GetAllLibraryEntriesAsync();
            
            var candidates = allTracks
                .Where(t => t.Id != seedTrackId) // Exclude seed track
                .Where(t => !string.IsNullOrEmpty(t.Key))
                .Where(t => compatibleKeys.Contains(t.Key)) // Key compatibility
                .ToList();

            // Score and filter results
            var results = new List<HarmonicMatchResult>();
            
            foreach (var candidate in candidates)
            {
                var score = CalculateCompatibilityScore(
                    seedTrack, 
                    candidate, 
                    includeBpmRange, 
                    includeEnergyMatch,
                    minBpm,
                    maxBpm);

                if (score > 0 && !string.IsNullOrEmpty(candidate.Key))
                {
                    results.Add(new HarmonicMatchResult
                    {
                        Track = candidate,
                        CompatibilityScore = score,
                        KeyRelationship = GetKeyRelationship(seedTrack.Key!, candidate.Key),
                        BpmDifference = CalculateBpmDifference(seedTrack.Bpm, candidate.Bpm),
                        EnergyDifference = CalculateEnergyDifference(seedTrack, candidate)
                    });
                }
            }

            // Sort by compatibility and return top N
            return results
                .OrderByDescending(r => r.CompatibilityScore)
                .Take(limit)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find harmonic matches for track {TrackId}", seedTrackId);
            return new List<HarmonicMatchResult>();
        }
    }

    /// <summary>
    /// Gets all keys that are harmonically compatible with the given key.
    /// </summary>
    private HashSet<string> GetCompatibleKeys(string seedKey)
    {
        if (CamelotWheel.TryGetValue(seedKey, out var compatibleKeys))
        {
            return compatibleKeys;
        }

        _logger.LogWarning("Unknown Camelot key: {Key}", seedKey);
        return new HashSet<string> { seedKey }; // Fallback: only exact match
    }

    /// <summary>
    /// Calculates a compatibility score (0-100) based on multiple factors.
    /// </summary>
    private double CalculateCompatibilityScore(
        LibraryEntryEntity seed,
        LibraryEntryEntity candidate,
        bool includeBpm,
        bool includeEnergy,
        double? minBpm,
        double? maxBpm)
    {
        double score = 0;

        // KEY MATCH (0-50 points)
        if (string.IsNullOrEmpty(seed.Key) || string.IsNullOrEmpty(candidate.Key))
            return 0;

        var relationship = GetKeyRelationship(seed.Key, candidate.Key);
        score += relationship switch
        {
            KeyRelationship.Perfect => 50,      // Same key
            KeyRelationship.Compatible => 40,   // Adjacent on wheel
            KeyRelationship.Relative => 35,     // Relative major/minor
            _ => 0
        };

        // BPM COMPATIBILITY (0-30 points)
        if (includeBpm && seed.Bpm.HasValue && candidate.Bpm.HasValue)
        {
            var bpmDiff = Math.Abs(seed.Bpm.Value - candidate.Bpm.Value);
            
            if (candidate.Bpm >= minBpm && candidate.Bpm <= maxBpm)
            {
                // Within ±6% range
                var variance = bpmDiff / seed.Bpm.Value;
                score += 30 * (1 - variance / 0.06); // Linear decay
            }
        }

        // ENERGY/MOOD MATCH (0-20 points)
        if (includeEnergy)
        {
            var energyScore = CalculateEnergyScore(seed, candidate);
            score += energyScore;
        }

        return Math.Round(score, 2);
    }

    private KeyRelationship GetKeyRelationship(string seedKey, string candidateKey)
    {
        if (seedKey == candidateKey)
            return KeyRelationship.Perfect;

        if (!CamelotWheel.TryGetValue(seedKey, out var compatible))
            return KeyRelationship.Incompatible;

        if (compatible.Contains(candidateKey))
        {
            // Check if it's relative major/minor (same number, different letter)
            if (seedKey[..^1] == candidateKey[..^1]) // e.g., 8A vs 8B
                return KeyRelationship.Relative;
            
            return KeyRelationship.Compatible;
        }

        return KeyRelationship.Incompatible;
    }

    private double? CalculateBpmDifference(double? seedBpm, double? candidateBpm)
    {
        if (!seedBpm.HasValue || !candidateBpm.HasValue)
            return null;

        return Math.Round(Math.Abs(seedBpm.Value - candidateBpm.Value), 2);
    }

    private double? CalculateEnergyDifference(LibraryEntryEntity seed, LibraryEntryEntity candidate)
    {
        // Placeholder: Would use Spotify Energy/Valence if available
        // For now, return null
        return null;
    }

    private double CalculateEnergyScore(LibraryEntryEntity seed, LibraryEntryEntity candidate)
    {
        // Placeholder: Would calculate based on Spotify metadata
        // Energy (0-1) and Valence (0-1) similarity
        // For now, return neutral score
        return 10; // Neutral baseline
    }
}

/// <summary>
/// Represents a harmonically compatible track with scoring details.
/// </summary>
public class HarmonicMatchResult
{
    public LibraryEntryEntity Track { get; set; } = null!;
    public double CompatibilityScore { get; set; }
    public KeyRelationship KeyRelationship { get; set; }
    public double? BpmDifference { get; set; }
    public double? EnergyDifference { get; set; }
}

/// <summary>
/// Describes the harmonic relationship between two keys.
/// </summary>
public enum KeyRelationship
{
    Perfect,        // Same key
    Compatible,     // Adjacent on Camelot Wheel (±1 semitone)
    Relative,       // Relative major/minor (8A ↔ 8B)
    Incompatible    // No harmonic relationship
}
