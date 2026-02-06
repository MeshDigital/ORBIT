using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Musical;

/// <summary>
/// Phase 10: Cue Generation Engine.
/// Uses Tri-Band RMS data (from WaveformAnalysisService) to detect structural DJ cue points.
/// </summary>
public class CueGenerationEngine
{
    private readonly ILogger<CueGenerationEngine> _logger;
    private readonly DropDetectionEngine? _dropDetectionEngine;

    public CueGenerationEngine(ILogger<CueGenerationEngine> logger, DropDetectionEngine? dropDetectionEngine)
    {
        _logger = logger;
        _dropDetectionEngine = dropDetectionEngine;
    }

    /// <summary>
    /// Generates high-fidelity cue points based on Tri-Band frequency energy.
    /// </summary>
    public async Task<List<OrbitCue>> GenerateCuesAsync(WaveformAnalysisData data, double duration, string? genre = null)
    {
        var cues = new List<OrbitCue>();
        if (data.RmsData == null || data.RmsData.Length == 0) return cues;

        int pointsCount = data.RmsData.Length;
        double secondsPerPoint = duration / pointsCount;

        // 1. INTRO: Find first significant energy in Mid/High bands
        int introIdx = FindFirstEnergy(data.MidData, 10); // Threshold 10/255
        if (introIdx >= 0)
        {
            cues.Add(new OrbitCue 
            { 
                Timestamp = introIdx * secondsPerPoint, 
                Name = "Intro", 
                Role = CueRole.Intro, 
                Color = "#0000FF", // Blue
                Confidence = 0.9
            });
        }

        // 2. KICK-IN: Find where Low band (Bass) sustains
        int kickInIdx = FindKickIn(data.LowData, introIdx >= 0 ? introIdx : 0);
        if (kickInIdx >= 0)
        {
            cues.Add(new OrbitCue 
            { 
                Timestamp = kickInIdx * secondsPerPoint, 
                Name = "Kick-In", 
                Role = CueRole.KickIn, 
                Color = "#00FFFF", // Cyan
                Confidence = 0.8
            });
        }

        // 3. THE DROP: Use sudden energy surge
        int dropIdx = await FindDropIdxAsync(data, secondsPerPoint);
        if (dropIdx >= 0)
        {
             cues.Add(new OrbitCue 
            { 
                Timestamp = dropIdx * secondsPerPoint, 
                Name = "The Drop", 
                Role = CueRole.Drop, 
                Color = "#FF0000", // Red
                Confidence = 0.7
            });
        }

        // 4. OUTRO: Find start of final energy fade
        int outroIdx = FindOutro(data.MidData, pointsCount);
        if (outroIdx >= 0)
        {
            cues.Add(new OrbitCue 
            { 
                Timestamp = outroIdx * secondsPerPoint, 
                Name = "Outro", 
                Role = CueRole.Outro, 
                Color = "#00FF00", // Green
                Confidence = 0.8
            });
        }

        return cues.OrderBy(c => c.Timestamp).ToList();
    }

    private int FindFirstEnergy(byte[]? data, byte threshold)
    {
        if (data == null) return -1;
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] > threshold) return i;
        }
        return -1;
    }

    private int FindKickIn(byte[]? lowData, int startIdx)
    {
        if (lowData == null) return -1;
        // Look for a "step up" in bass energy that sustains
        int windowSize = 20; 
        for (int i = Math.Max(0, startIdx); i < lowData.Length - windowSize; i++)
        {
            double avg = lowData.Skip(i).Take(windowSize).Average(b => (int)b);
            if (avg > 40) return i; // Baseline bass energy
        }
        return -1;
    }

    private async Task<int> FindDropIdxAsync(WaveformAnalysisData data, double secondsPerPoint)
    {
        if (data.PeakData == null) return -1;
        int bestIdx = -1;
        double maxDelta = 0;
        
        int window = 50; 
        for (int i = 100; i < data.PeakData.Length - window; i++)
        {
            double delta = (int)data.PeakData[i] - (int)data.PeakData[i - 50];
            if (delta > maxDelta)
            {
                maxDelta = delta;
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    private int FindOutro(byte[]? midData, int totalPoints)
    {
        if (midData == null) return -1;
        int start = (int)(totalPoints * 0.7);
        for (int i = totalPoints - 10; i > start; i--)
        {
            if (midData[i] > 30) return i;
        }
        return -1;
    }

    /// <summary>
    /// Phase 17: Mixed In Key Pro Parity.
    /// Maps new structural points to the 8-slot MIK pattern.
    /// </summary>
    public async Task<List<OrbitCue>> GenerateMikStandardCues(string trackUniqueHash)
    {
        using var db = new SLSKDONET.Data.AppDbContext();
        var features = await db.AudioFeatures.FirstOrDefaultAsync(f => f.TrackUniqueHash == trackUniqueHash);
        var track = await db.LibraryEntries.FirstOrDefaultAsync(le => le.UniqueHash == trackUniqueHash);
        
        if (track == null || features == null) return new List<OrbitCue>();

        double duration = features.TrackDuration;
        var data = new WaveformAnalysisData
        {
            RmsData = track.RmsData ?? Array.Empty<byte>(),
            LowData = track.LowData ?? Array.Empty<byte>(),
            MidData = track.MidData ?? Array.Empty<byte>(),
            HighData = track.HighData ?? Array.Empty<byte>(),
            PeakData = track.WaveformData ?? Array.Empty<byte>(),
            DurationSeconds = duration
        };

        var cues = await GenerateMikStandardCuesAsync(data, duration, features.Bpm);
        
        // Calculate Segmented Energy
        var segmentedEnergy = CalculateSegmentedEnergy(data, cues);
        features.SegmentedEnergyJson = System.Text.Json.JsonSerializer.Serialize(segmentedEnergy);
        
        return cues;
    }

    public async Task<List<OrbitCue>> GenerateMikStandardCuesAsync(WaveformAnalysisData data, double duration, float bpm)
    {
        var cues = new List<OrbitCue>();
        if (data == null || data.RmsData == null || data.RmsData.Length == 0) return cues;

        int pointsCount = data.RmsData.Length;
        double secondsPerPoint = duration / pointsCount;
        double secondsPerBeat = 60.0 / (bpm > 0 ? bpm : 120);

        // 1. INTRO
        int introIdx = FindFirstEnergy(data.MidData, 10);
        cues.Add(new OrbitCue { Timestamp = introIdx * secondsPerPoint, Name = "Intro", Role = CueRole.Intro, Color = "#0000FF", SlotIndex = 1 });

        // 2. KICK-IN
        int kickInIdx = FindKickIn(data.LowData, introIdx);
        cues.Add(new OrbitCue { Timestamp = kickInIdx * secondsPerPoint, Name = "Kick-In", Role = CueRole.KickIn, Color = "#00FFFF", SlotIndex = 2 });

        // 3. BUILD
        int dropIdx = await FindDropIdxAsync(data, secondsPerPoint);
        double buildTime = Math.Max(0, (dropIdx * secondsPerPoint) - (secondsPerBeat * 32));
        cues.Add(new OrbitCue { Timestamp = buildTime, Name = "Build", Role = CueRole.Build, Color = "#FFFF00", SlotIndex = 3 });

        // 4. DROP 1
        cues.Add(new OrbitCue { Timestamp = dropIdx * secondsPerPoint, Name = "Drop 1", Role = CueRole.Drop, Color = "#FF0000", SlotIndex = 4 });

        // 5. BREAKDOWN 2
        int breakdown2Idx = FindBreakdown(data.RmsData, dropIdx + 100);
        cues.Add(new OrbitCue { Timestamp = breakdown2Idx * secondsPerPoint, Name = "Breakdown 2", Role = CueRole.Breakdown2, Color = "#AA00FF", SlotIndex = 5 });

        // 6. CLIMAX
        int climaxIdx = FindClimax(data.HighData, dropIdx);
        cues.Add(new OrbitCue { Timestamp = climaxIdx * secondsPerPoint, Name = "Climax", Role = CueRole.Climax, Color = "#FF00FF", SlotIndex = 6 });

        // 7. BRIDGE
        int outroIdx = FindOutro(data.MidData, pointsCount);
        double bridgeTime = Math.Max(0, (outroIdx * secondsPerPoint) - (secondsPerBeat * 64));
        cues.Add(new OrbitCue { Timestamp = bridgeTime, Name = "Bridge", Role = CueRole.Bridge, Color = "#FFA500", SlotIndex = 7 });

        // 8. OUTRO
        cues.Add(new OrbitCue { Timestamp = outroIdx * secondsPerPoint, Name = "Outro", Role = CueRole.Outro, Color = "#00FF00", SlotIndex = 8 });

        return cues.OrderBy(c => c.Timestamp).ToList();
    }

    private List<int> CalculateSegmentedEnergy(WaveformAnalysisData data, List<OrbitCue> cues)
    {
        var energies = new List<int>();
        if (data.RmsData == null || data.RmsData.Length == 0) return Enumerable.Repeat(5, 8).ToList();

        var sortedCues = cues.OrderBy(c => c.Timestamp).ToList();
        double duration = data.DurationSeconds > 0 ? data.DurationSeconds : data.RmsData.Length / 100.0;
        double samplesPerSecond = data.RmsData.Length / duration;

        for (int i = 0; i < sortedCues.Count; i++)
        {
            double start = sortedCues[i].Timestamp;
            double end = (i < sortedCues.Count - 1) ? sortedCues[i + 1].Timestamp : duration;

            int startIdx = (int)(start * samplesPerSecond);
            int endIdx = (int)(end * samplesPerSecond);

            if (startIdx < 0) startIdx = 0;
            if (endIdx > data.RmsData.Length) endIdx = data.RmsData.Length;

            if (endIdx > startIdx)
            {
                var segment = data.RmsData.Skip(startIdx).Take(endIdx - startIdx);
                double avg = segment.Average(b => (int)b);
                // Map 0-255 to 1-10
                int score = (int)Math.Clamp(Math.Round(avg / 25.5), 1, 10);
                energies.Add(score);
            }
            else
            {
                energies.Add(5);
            }
        }

        // Ensure exactly 8 slots if we have fewer cues
        while (energies.Count < 8) energies.Add(5);
        return energies.Take(8).ToList();
    }

    private int FindBreakdown(byte[] rmsData, int startIdx)
    {
        if (startIdx >= rmsData.Length) return Math.Max(0, rmsData.Length - 100);
        for (int i = startIdx; i < rmsData.Length - 50; i++)
        {
            if (rmsData[i] < 40) return i;
        }
        return startIdx;
    }

    private int FindClimax(byte[]? highData, int startIdx)
    {
        if (highData == null) return -1;
        if (startIdx >= highData.Length) return Math.Max(0, highData.Length - 50);
        int bestIdx = startIdx;
        byte maxVal = 0;
        for (int i = startIdx; i < highData.Length; i++)
        {
            if (highData[i] > maxVal) { maxVal = highData[i]; bestIdx = i; }
        }
        return bestIdx;
    }

    public CueSet GenerateCues(double dropTime, float bpm)
    {
        double secondsPerBeat = 60.0 / (bpm > 0 ? bpm : 120);
        return new CueSet
        {
            Intro = Math.Max(0, dropTime - (secondsPerBeat * 64)),
            PhraseStart = Math.Max(0, dropTime - (secondsPerBeat * 32)),
            Build = Math.Max(0, dropTime - (secondsPerBeat * 16)),
            Drop = dropTime
        };
    }
}

public struct CueSet
{
    public double Intro { get; set; }
    public double PhraseStart { get; set; }
    public double Build { get; set; }
    public double Drop { get; set; }
}
