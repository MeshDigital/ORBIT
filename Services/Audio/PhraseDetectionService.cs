using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services.Musical;

namespace SLSKDONET.Services.Audio;

/// <summary>
/// Phase 1: Structural Intelligence.
/// Detects Intro, Verse, Build, Drop, Breakdown, and Outro based on 
/// Spectral Energy Curves and AI-detected features.
/// </summary>
public class PhraseDetectionService
{
    private readonly ILogger<PhraseDetectionService> _logger;
    private readonly AppDbContext _context;
    private const int CurrentStructuralVersion = 2; // Incremented for strategic refinements

    public PhraseDetectionService(ILogger<PhraseDetectionService> logger, AppDbContext context)
    {
        _logger = logger;
        _context = context;
    }

    public async Task<bool> DetectPhrasesAsync(string trackUniqueHash)
    {
        try
        {
            var features = await _context.AudioFeatures.FirstOrDefaultAsync(f => f.TrackUniqueHash == trackUniqueHash);
            
            var trackId = await _context.PlaylistTracks
                .Where(p => p.TrackUniqueHash == trackUniqueHash)
                .Select(p => p.Id)
                .FirstOrDefaultAsync();

            var technical = await _context.TechnicalDetails
                .FirstOrDefaultAsync(t => t.PlaylistTrackId == trackId);

            if (features == null || technical == null || technical.RmsData == null)
            {
                _logger.LogWarning("Missing required data for Phrase Detection: {Hash}", trackUniqueHash);
                return false;
            }

            _logger.LogInformation("🧠 Phrase Detection: Analyzing {Hash}...", trackUniqueHash);

            // 1. Generate Energy Curves
            var energyCurve = CalculateEnergyCurve(technical);
            var vocalCurve = CalculateVocalDensityCurve(technical);
            
            // 2. Normalize and Smooth Curves
            NormalizeCurve(energyCurve);
            NormalizeCurve(vocalCurve);

            // 3. Detect Anomalies
            var anomalies = DetectAnomalies(technical, energyCurve);

            // 4. Identify Segments
            var segments = IdentifySegments(energyCurve, vocalCurve, features.Bpm, out var reasoning);

            // 5. Compute Structural Hash
            var structuralData = new { s = segments, e = energyCurve, v = vocalCurve };
            string structuralHash = BitConverter.ToString(System.Security.Cryptography.SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(structuralData))).Replace("-", "").ToLower();

            // 6. Save to Features
            features.PhraseSegmentsJson = JsonSerializer.Serialize(segments);
            features.EnergyCurveJson = JsonSerializer.Serialize(energyCurve);
            features.VocalDensityCurveJson = JsonSerializer.Serialize(vocalCurve);
            features.AnalysisReasoningJson = JsonSerializer.Serialize(reasoning);
            features.AnomaliesJson = JsonSerializer.Serialize(anomalies);
            features.StructuralVersion = CurrentStructuralVersion;
            features.StructuralHash = structuralHash;

            await _context.SaveChangesAsync();
            _logger.LogInformation("✅ Phase 1 Refined Analysis Completed for {Hash}: {Count} segments, {Anom} anomalies", 
                trackUniqueHash, segments.Count, anomalies.Count);
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed phrase detection for {Hash}", trackUniqueHash);
            return false;
        }
    }

    private List<float> CalculateEnergyCurve(TrackTechnicalEntity technical)
    {
        // Use Low + Mid data for general energy.
        // Downsample for UI performance (e.g., 1 point per 0.5s)
        var low = technical.LowData!;
        var mid = technical.MidData!;
        
        var points = new List<float>();
        int stride = 50; // 50 points = 0.5s if 100fps
        
        for (int i = 0; i < low.Length - stride; i += stride)
        {
            float avg = 0;
            for (int j = 0; j < stride; j++)
            {
                avg += (low[i + j] + mid[i + j]) / 2f;
            }
            points.Add((avg / stride) / 255f);
        }
        
        return points;
    }

    private List<float> CalculateVocalDensityCurve(TrackTechnicalEntity technical)
    {
        // Focus on Mid/High bands where vocals sit
        var mid = technical.MidData!;
        var high = technical.HighData!;
        
        var points = new List<float>();
        int stride = 50;
        
        for (int i = 0; i < mid.Length - stride; i += stride)
        {
            float avg = 0;
            for (int j = 0; j < stride; j++)
            {
                 // Heavy weight on Mid, lighter on High
                avg += (mid[i + j] * 0.7f + high[i + j] * 0.3f);
            }
            points.Add((avg / stride) / 255f);
        }
        
        return points;
    }

    private void NormalizeCurve(List<float> curve)
    {
        if (curve.Count == 0) return;
        float max = curve.Max();
        float min = curve.Min();
        float range = max - min;
        
        if (range > 0.01f)
        {
            for (int i = 0; i < curve.Count; i++)
                curve[i] = (curve[i] - min) / range;
        }
    }

    private List<string> DetectAnomalies(TrackTechnicalEntity technical, List<float> energyCurve)
    {
        var anomalies = new List<string>();
        
        // 1. Sustained Silence (Energy near zero for > 15s)
        int silenceCount = 0;
        int maxSilence = 0;
        foreach(var v in energyCurve) {
            if (v < 0.02f) silenceCount++;
            else {
                maxSilence = Math.Max(maxSilence, silenceCount);
                silenceCount = 0;
            }
        }
        if (maxSilence > 30) // 30 points * 0.5s = 15s
            anomalies.Add("Lengthy silence detected (>15s)");

        // 2. Extreme Clipping Heuristic (Assume signal near max for sustained periods)
        if (technical.RmsData != null)
        {
            int clippedSamples = technical.RmsData.Count(p => p > 250);
            if (clippedSamples > technical.RmsData.Length * 0.15)
                anomalies.Add("Potential heavy clipping / compression detected");
        }

        return anomalies;
    }

    private List<PhraseSegment> IdentifySegments(List<float> energyCurve, List<float> vocalCurve, float bpm, out Dictionary<string, string> reasoning)
    {
        var segments = new List<PhraseSegment>();
        reasoning = new Dictionary<string, string>();
        
        if (energyCurve.Count == 0) return segments;

        float secondsPerPoint = 0.5f; 
        
        // 1. Find Intro
        int introIdx = energyCurve.FindIndex(v => v > 0.05f);
        if (introIdx >= 0)
        {
            var s = new PhraseSegment { Label = "Intro", Start = introIdx * secondsPerPoint, Color = "#4A90E2", Confidence = 0.9f };
            segments.Add(s);
            reasoning["Intro"] = $"First significant energy detected at {s.Start:F1}s";
        }

        // 2. Find The Drop
        int dropIdx = -1;
        float maxJump = 0;
        for (int i = 4; i < energyCurve.Count - 20; i++)
        {
            float prev = energyCurve.Skip(i - 4).Take(4).Average();
            float next = energyCurve.Skip(i).Take(4).Average();
            float jump = next - prev;
            
            if (jump > maxJump && next > 0.6f)
            {
                maxJump = jump;
                dropIdx = i;
            }
        }

        if (dropIdx > 0)
        {
            int buildStart = Math.Max(0, dropIdx - (int)(15f / secondsPerPoint)); 
            
            segments.Add(new PhraseSegment { Label = "Build", Start = buildStart * secondsPerPoint, Color = "#F5A623", Confidence = 0.7f });
            segments.Add(new PhraseSegment { Label = "Drop", Start = dropIdx * secondsPerPoint, Color = "#D0021B", Confidence = 0.85f });
            
            reasoning["Drop"] = $"Major energy jump of {maxJump:P0} detected at {dropIdx * secondsPerPoint:F1}s";
            reasoning["Build"] = "Energy ramping identified before the drop landmark.";
        }

        // 3. Find Breakdown
        if (dropIdx > 0)
        {
            int breakdownIdx = energyCurve.FindIndex(dropIdx + 40, v => v < 0.4f);
            if (breakdownIdx > 0)
            {
                segments.Add(new PhraseSegment { Label = "Breakdown", Start = breakdownIdx * secondsPerPoint, Color = "#BD10E0", Confidence = 0.65f });
                reasoning["Breakdown"] = "Primary energy dip following the main drop segment.";
            }
        }

        // 4. Find Outro
        int outroIdx = energyCurve.FindLastIndex(v => v > 0.15f);
        if (outroIdx > 100)
        {
             segments.Add(new PhraseSegment { Label = "Outro", Start = (outroIdx - 20) * secondsPerPoint, Color = "#7ED321", Confidence = 0.8f });
             reasoning["Outro"] = "End of track detected via sustained energy reduction.";
        }

        // 5. Finalize Metadata (Duration, Bars, Beats)
        var sorted = segments.OrderBy(s => s.Start).ToList();
        float totalDuration = energyCurve.Count * secondsPerPoint;

        for (int i = 0; i < sorted.Count; i++)
        {
            float end = (i < sorted.Count - 1) ? sorted[i + 1].Start : totalDuration;
            sorted[i].Duration = end - sorted[i].Start;
            
            if (bpm > 30)
            {
                float beats = (sorted[i].Duration * bpm) / 60f;
                sorted[i].Beats = (int)Math.Round(beats);
                sorted[i].Bars = (int)Math.Round(beats / 4f);
            }
        }

        return sorted;
    }
}
