using System;
using System.Collections.Generic;
using SLSKDONET.Models;
using SLSKDONET.Configuration;
using SLSKDONET.Services;

namespace SLSKDONET.Services.Ranking;

/// <summary>
/// Defines the quality tiers for search results.
/// </summary>
public enum TrackTier
{
    Diamond = 1,    // Perfect Match (The "One")
    Gold = 2,       // Excellent Quality & Match
    Silver = 3,     // Good / Acceptable
    Bronze = 4,     // Fallback / Low Quality
    Trash = 5       // Should have been gated, but kept at bottom
}

/// <summary>
/// Deterministic comparator that ranks tracks based on a tiered strategy.
/// Replaces the legacy weight-based scoring system.
/// </summary>
public class TieredTrackComparer : IComparer<Track>
{
    private readonly SearchPolicy _policy;
    private readonly Track _searchTrack;

    public TieredTrackComparer(SearchPolicy policy, Track searchTrack)
    {
        _policy = policy ?? SearchPolicy.QualityFirst();
        _searchTrack = searchTrack;
    }

    public int Compare(Track? x, Track? y)
    {
        if (x == null && y == null) return 0;
        if (x == null) return 1;
        if (y == null) return -1;

        // 1. Determine Tiers
        var tierX = CalculateTier(x);
        var tierY = CalculateTier(y);

        // Lower tier number is better (Diamond < Gold)
        int tierComparison = tierX.CompareTo(tierY);
        if (tierComparison != 0) return tierComparison;

        // 2. Intra-Tier Tie-Breakers
        return CompareWithinTier(x, y);
    }

    /// <summary>
    /// Calculates the visual rank score (0-1) for UI compatibility.
    /// Diamond = 1.0, Gold = 0.85, Silver = 0.6, Bronze = 0.4.
    /// </summary>
    public double CalculateRankScore(Track track)
    {
        var tier = CalculateTier(track);
        return tier switch
        {
            TrackTier.Diamond => 1.0,
            TrackTier.Gold => 0.85,
            TrackTier.Silver => 0.60,
            TrackTier.Bronze => 0.40,
            _ => 0.10
        };
    }

    public string GenerateBreakdown(Track track)
    {
        var tier = CalculateTier(track);
        return tier switch
        {
            TrackTier.Diamond => "💎 DIAMOND TIER\n• Perfect Match\n• High Quality\n• Available",
            TrackTier.Gold => "🥇 GOLD TIER\n• Great Quality\n• Good Availability",
            TrackTier.Silver => "🥈 SILVER TIER\n• Acceptable Match",
            TrackTier.Bronze => "🥉 BRONZE TIER\n• Low Quality/Availability",
            _ => "📉 LOW TIER"
        };
    }

    private TrackTier CalculateTier(Track track)
    {
        // 0. FORENSIC GATEKEEPING (New in Phase 14)
        var config = ResultSorter.GetCurrentConfig();
        if (config?.EnableVbrFraudDetection ?? true)
        {
            if (MetadataForensicService.IsFake(track))
            {
                return TrackTier.Trash; // Immediate demotion for mathematical fakes
            }
        }

        // 1. Availability Check (Dead End?)
        if (track.HasFreeUploadSlot == false && track.QueueLength > 500)
            return TrackTier.Bronze; // Effectively unavailable

        // 2. Quality Checks
        bool isLossless = track.Format?.ToLower() == "flac" || track.Format?.ToLower() == "wav";
        bool isHighRes = track.Bitrate >= 320 || isLossless;
        bool isMidRes = track.Bitrate >= 192;

        // 3. Metadata Checks
        bool hasBpm = track.BPM.HasValue || (track.Filename?.Contains("bpm", StringComparison.OrdinalIgnoreCase) ?? false);
        bool hasKey = !string.IsNullOrEmpty(track.MusicalKey);

        // --- POLICY EVALUATION ---

        // 4. Integrity/Duration Check (Demote suspicious files)
        if (_policy.EnforceDurationMatch && _searchTrack.Length.HasValue && track.Length.HasValue)
        {
             if (Math.Abs(_searchTrack.Length.Value - track.Length.Value) > _policy.DurationToleranceSeconds)
                 return TrackTier.Bronze; 
        }

        // Check for BPM Match (only if search track has BPM defined)
        bool bpmMatches = !_searchTrack.BPM.HasValue || (track.BPM.HasValue && Math.Abs(_searchTrack.BPM.Value - track.BPM.Value) < 3);

        if (_policy.Priority == SearchPriority.DjReady)
        {
            // DJ Mode: Needs BPM/Key and decent quality
            // MUST MATCH BPM if specified
            if (hasBpm && bpmMatches && isHighRes && track.HasFreeUploadSlot) return TrackTier.Diamond;
            if ((hasBpm || hasKey) && bpmMatches && isMidRes) return TrackTier.Gold;
            
            // Mismatch but good quality -> Silver
            if (isMidRes) return TrackTier.Silver;
            return TrackTier.Bronze;
        }
        else // Quality First (Audiophile)
        {
            // Quality Mode: Needs Bitrate
            bool perfectFormat = isLossless || track.Bitrate == 320;
            
            if (perfectFormat && track.HasFreeUploadSlot) return TrackTier.Diamond;
            if (isHighRes) return TrackTier.Gold;
            if (isMidRes) return TrackTier.Silver;
            return TrackTier.Bronze;
        }
    }

    private int CompareWithinTier(Track x, Track y)
    {
        // 1. Queue/Availability (Immediate gratification wins tie-breaks)
        if (x.HasFreeUploadSlot != y.HasFreeUploadSlot)
            return x.HasFreeUploadSlot ? -1 : 1; // Free slot wins

        // 2. Bitrate (Higher is better)
        if (Math.Abs(x.Bitrate - y.Bitrate) > _policy.SignificantBitrateGap)
            return y.Bitrate.CompareTo(x.Bitrate); // Higher wins

        // 3. Queue Length (Shorter is better)
        if (Math.Abs(x.QueueLength - y.QueueLength) > _policy.SignificantQueueGap)
            return x.QueueLength.CompareTo(y.QueueLength); // Shorter wins

        // 4. Filename Aesthetics (Shorter often cleaner, unless too short)
        // Prefer "Artist - Title.mp3" over "Artist_-_Title_(www.site.com).mp3"
        // Heuristic: Shorter length usually implies less junk
        int lenX = x.Filename?.Length ?? 1000;
        int lenY = y.Filename?.Length ?? 1000;
        return lenX.CompareTo(lenY);
    }
}
