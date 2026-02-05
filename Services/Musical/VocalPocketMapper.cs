using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Services.Musical;

/// <summary>
/// Vocal zone classification for heatmap rendering.
/// </summary>
public enum VocalZoneType
{
    /// <summary>Safe to mix - no vocals detected.</summary>
    Instrumental,
    /// <summary>Light ad-libs or sparse verse.</summary>
    Sparse,
    /// <summary>Repeating hook or melodic focus.</summary>
    Hook,
    /// <summary>Full lyrics - danger zone for mixing.</summary>
    Dense
}

/// <summary>
/// Represents a continuous segment of vocal activity.
/// </summary>
public class VocalPocketSegment
{
    public float StartSeconds { get; set; }
    public float EndSeconds { get; set; }
    public VocalZoneType ZoneType { get; set; }
    public float AverageIntensity { get; set; }
    
    public float DurationSeconds => EndSeconds - StartSeconds;
    
    public VocalPocketSegment(float start, float end, VocalZoneType type, float intensity = 0f)
    {
        StartSeconds = start;
        EndSeconds = end;
        ZoneType = type;
        AverageIntensity = intensity;
    }
}

/// <summary>
/// Container for vocal pocket data ready for UI rendering.
/// </summary>
public class VocalPocketRenderModel
{
    public float TrackDurationSeconds { get; }
    public IReadOnlyList<VocalPocketSegment> Segments { get; }
    
    public VocalPocketRenderModel(float durationSeconds, IReadOnlyList<VocalPocketSegment> segments)
    {
        TrackDurationSeconds = durationSeconds;
        Segments = segments;
    }
    
    public static VocalPocketRenderModel Empty => new(0, Array.Empty<VocalPocketSegment>());
}

/// <summary>
/// Maps raw vocal density curves to classified pocket segments.
/// Merges adjacent frames with the same classification to avoid UI flicker.
/// </summary>
public static class VocalPocketMapper
{
    /// <summary>
    /// Classification thresholds for vocal zones.
    /// Tuned for DJ decision-making in high-pressure environments.
    /// </summary>
    private const float InstThreshold = 0.05f;     // Below 5% = Safe Harbor
    private const float SparseThreshold = 0.15f;   // 5-15% = Light vocals
    private const float HookThreshold = 0.35f;     // 15-35% = Hook/chorus
    // Above 35% = Dense lyrics

    /// <summary>
    /// Converts raw density curve to classified segments.
    /// </summary>
    /// <param name="densityCurve">Frame-by-frame vocal density (0.0 to 1.0)</param>
    /// <param name="trackDurationSeconds">Total track duration for time mapping</param>
    /// <returns>Render model ready for VocalPocketRenderer</returns>
    public static VocalPocketRenderModel Map(IReadOnlyList<float> densityCurve, float trackDurationSeconds)
    {
        if (densityCurve == null || densityCurve.Count == 0 || trackDurationSeconds <= 0)
            return VocalPocketRenderModel.Empty;

        float secondsPerFrame = trackDurationSeconds / densityCurve.Count;
        var segments = new List<VocalPocketSegment>();
        
        // Start first segment
        var currentType = Classify(densityCurve[0]);
        float segmentStart = 0f;
        float intensitySum = densityCurve[0];
        int frameCount = 1;

        for (int i = 1; i < densityCurve.Count; i++)
        {
            var frameType = Classify(densityCurve[i]);
            
            if (frameType != currentType)
            {
                // Close current segment
                float segmentEnd = i * secondsPerFrame;
                float avgIntensity = intensitySum / frameCount;
                segments.Add(new VocalPocketSegment(segmentStart, segmentEnd, currentType, avgIntensity));
                
                // Start new segment
                segmentStart = segmentEnd;
                currentType = frameType;
                intensitySum = densityCurve[i];
                frameCount = 1;
            }
            else
            {
                intensitySum += densityCurve[i];
                frameCount++;
            }
        }

        // Close final segment
        segments.Add(new VocalPocketSegment(
            segmentStart, 
            trackDurationSeconds, 
            currentType, 
            intensitySum / frameCount));

        // Merge very short segments (under 0.5s) into neighbors to reduce visual noise
        var merged = MergeShortSegments(segments, minDurationSeconds: 0.5f);

        return new VocalPocketRenderModel(trackDurationSeconds, merged);
    }

    /// <summary>
    /// Classifies a single density value into a vocal zone.
    /// </summary>
    private static VocalZoneType Classify(float density) => density switch
    {
        < InstThreshold => VocalZoneType.Instrumental,
        < SparseThreshold => VocalZoneType.Sparse,
        < HookThreshold => VocalZoneType.Hook,
        _ => VocalZoneType.Dense
    };

    /// <summary>
    /// Merges segments shorter than minDuration into their neighbors.
    /// This prevents visual clutter from brief vocal moments.
    /// </summary>
    private static IReadOnlyList<VocalPocketSegment> MergeShortSegments(
        List<VocalPocketSegment> segments, 
        float minDurationSeconds)
    {
        if (segments.Count <= 1) return segments;

        var result = new List<VocalPocketSegment>();
        VocalPocketSegment? pending = null;

        foreach (var seg in segments)
        {
            if (pending == null)
            {
                pending = seg;
                continue;
            }

            if (seg.DurationSeconds < minDurationSeconds)
            {
                // Absorb short segment into pending
                pending = new VocalPocketSegment(
                    pending.StartSeconds,
                    seg.EndSeconds,
                    pending.ZoneType,
                    (pending.AverageIntensity + seg.AverageIntensity) / 2);
            }
            else
            {
                result.Add(pending);
                pending = seg;
            }
        }

        if (pending != null) result.Add(pending);
        return result;
    }
}
