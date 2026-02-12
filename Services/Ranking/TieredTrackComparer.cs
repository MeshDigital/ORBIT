using System;
using System.Collections.Generic;
using SLSKDONET.Models;
using SLSKDONET.Configuration;
using SLSKDONET.Services;

namespace SLSKDONET.Services.Ranking;

public enum TrackTier
{
    Diamond = 1,
    Gold = 2,
    Silver = 3,
    Bronze = 4,
    Trash = 5
}

public class TieredTrackComparer : IComparer<Track>
{
    private readonly SearchPolicy _policy;
    private readonly Track _searchTrack;
    private readonly bool _enableForensics; // [CHANGE 1] Config field

    // [CHANGE 2] Update Constructor
    public TieredTrackComparer(SearchPolicy policy, Track searchTrack, bool enableForensics = true)
    {
        _policy = policy ?? SearchPolicy.QualityFirst();
        _searchTrack = searchTrack;
        _enableForensics = enableForensics;
    }

    public int Compare(Track? x, Track? y)
    {
        if (x == null && y == null) return 0;
        if (x == null) return 1;
        if (y == null) return -1;

        var tierX = CalculateTier(x);
        var tierY = CalculateTier(y);

        int tierComparison = tierX.CompareTo(tierY);
        if (tierComparison != 0) return tierComparison;

        return CompareWithinTier(x, y);
    }

    public double CalculateRankScore(Track track)
    {
        var tier = CalculateTier(track);
        // WHY: Non-linear scoring reflects real-world value differences:
        // - Diamond (1.0) = Perfect match, worth waiting/paying for
        // - Gold (0.85) = 15% discount for "very good" vs "perfect"
        // - Silver (0.60) = 40% discount for "acceptable"
        // - Bronze (0.40) = 60% discount for "barely usable"
        // - Trash (0.10) = Near-zero but not completely filtered (allows override)
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
            TrackTier.Trash => "🗑️ TRASH TIER\n• Forensic Mismatch (Fake?)", // Updated Label
            _ => "📉 LOW TIER"
        };
    }

    private TrackTier CalculateTier(Track track)
    {
        // [CHANGE 3] THE FORENSIC CORE INTEGRATION
        // WHY: Forensic check MUST be first in the evaluation chain:
        // 1. Prevents wasting time scoring a file that's mathematically fake
        // 2. User trust: showing "Diamond" fake 320kbps hurts credibility more than missing a real file
        // 3. Bandwidth conservation: fake files waste 10+ MB of download quota
        // 4. False positive acceptable: user can disable forensics if needed (enableForensics flag)
        if (_enableForensics && MetadataForensicService.IsFake(track))
        {
            return TrackTier.Trash; // Not negotiable - math says it's fake
        }

        // 1. Availability Check
        // WHY: No free slot + huge queue = likely timeout/failure
        // 500 threshold based on empirical data: queues >500 take 30+ min or time out
        if (track.HasFreeUploadSlot == false && track.QueueLength > 500)
            return TrackTier.Bronze;

        // 2. Quality Checks
        // WHY: Three-tier quality model based on audio engineering:
        // - Lossless (FLAC/WAV): Bit-perfect, 1411kbps uncompressed equivalent
        // - 320kbps: "Transparent" - ABX tests show <5% can distinguish from lossless
        // - 192kbps: "Acceptable" - most people can't tell on casual listening
        bool isLossless = track.Format?.ToLower() == "flac" || track.Format?.ToLower() == "wav";
        bool isHighRes = track.Bitrate >= 320 || isLossless;
        bool isMidRes = track.Bitrate >= 192;

        // 3. Metadata Checks
        bool hasBpm = track.BPM.HasValue || (track.Filename?.Contains("bpm", StringComparison.OrdinalIgnoreCase) ?? false);
        bool hasKey = !string.IsNullOrEmpty(track.MusicalKey);

        // --- POLICY EVALUATION ---
        if (_policy.EnforceDurationMatch && _searchTrack.Length.HasValue && track.Length.HasValue)
        {
             if (Math.Abs(_searchTrack.Length.Value - track.Length.Value) > _policy.DurationToleranceSeconds)
                 return TrackTier.Bronze; 
        }

        bool bpmMatches = !_searchTrack.BPM.HasValue || (track.BPM.HasValue && Math.Abs(_searchTrack.BPM.Value - track.BPM.Value) < 3);

        if (_policy.Priority == SearchPriority.DjReady)
        {
            // Diamond strictly requires a free slot for "Immediate" feedback
            if (hasBpm && bpmMatches && isHighRes && track.HasFreeUploadSlot == true) return TrackTier.Diamond;
            
            // Gold can be high-res without slot or mid-res with slot
            if ((hasBpm || hasKey) && bpmMatches && isHighRes) return TrackTier.Gold;
            if ((hasBpm || hasKey) && bpmMatches && isMidRes && track.HasFreeUploadSlot == true) return TrackTier.Gold;
            
            if (isMidRes) return TrackTier.Silver;
            return TrackTier.Bronze;
        }
        else 
        {
            // Quality First Policy
            bool perfectFormat = isLossless || track.Bitrate == 320;
            if (perfectFormat && track.HasFreeUploadSlot == true) return TrackTier.Diamond;
            
            if (isHighRes) return TrackTier.Gold;
            if (isMidRes) return TrackTier.Silver;
            return TrackTier.Bronze;
        }
    }

    private int CompareWithinTier(Track x, Track y)
    {
        if (x.HasFreeUploadSlot != y.HasFreeUploadSlot)
            return x.HasFreeUploadSlot ? -1 : 1; 

        if (Math.Abs(x.Bitrate - y.Bitrate) > _policy.SignificantBitrateGap)
            return y.Bitrate.CompareTo(x.Bitrate); 

        if (Math.Abs(x.QueueLength - y.QueueLength) > _policy.SignificantQueueGap)
            return x.QueueLength.CompareTo(y.QueueLength); 

        int lenX = x.Filename?.Length ?? 1000;
        int lenY = y.Filename?.Length ?? 1000;
        return lenX.CompareTo(lenY);
    }
}
