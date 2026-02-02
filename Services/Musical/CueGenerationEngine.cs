using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Musical;

/// <summary>
/// Phase 10: Cue Generation Engine.
/// Uses Tri-Band RMS data (from WaveformAnalysisService) to detect structural DJ cue points.
/// </summary>
public class CueGenerationEngine
{
    private readonly ILogger<CueGenerationEngine> _logger;
    private readonly DropDetectionEngine _dropDetectionEngine;

    public CueGenerationEngine(ILogger<CueGenerationEngine> logger, DropDetectionEngine dropDetectionEngine)
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

        // 3. THE DROP: Use sudden energy surge + existing engine logic
        // This usually happens when all bands jump together or Low band explodes
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

    private int FindFirstEnergy(byte[] data, byte threshold)
    {
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] > threshold) return i;
        }
        return -1;
    }

    private int FindKickIn(byte[] lowData, int startIdx)
    {
        // Look for a "step up" in bass energy that sustains
        int windowSize = 20; // ~0.2s if 100fps
        for (int i = startIdx; i < lowData.Length - windowSize; i++)
        {
            double avg = lowData.Skip(i).Take(windowSize).Average(b => (int)b);
            if (avg > 40) return i; // Baseline bass energy
        }
        return -1;
    }

    private async Task<int> FindDropIdxAsync(WaveformAnalysisData data, double secondsPerPoint)
    {
        // Simple surge detection for now: sharpest increase in total energy
        // In full implementation, we'd integrate with DropDetectionEngine too
        int bestIdx = -1;
        double maxDelta = 0;
        
        int window = 50; // 0.5s
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

    private int FindOutro(byte[] midData, int totalPoints)
    {
        // Look at last 30% of track and find the last significant energy drop
        int start = (int)(totalPoints * 0.7);
        for (int i = totalPoints - 10; i > start; i--)
        {
            if (midData[i] > 30) return i;
        }
        return -1;
    }

    /// <summary>
    /// Phase 10: Industrial Prep Workflow (Synchronous Fallback)
    /// </summary>
    /// <summary>
    /// Phase 10: Industrial Prep Workflow (Async)
    /// </summary>
    public async Task<List<OrbitCue>> GenerateCuesAsync(SLSKDONET.Data.Entities.TrackTechnicalEntity technicalData, string? genre = null)
    {
         if (technicalData == null) return new List<OrbitCue>();
         
         // Reconstruct analysis data
         var data = new WaveformAnalysisData
         {
             RmsData = technicalData.RmsData ?? Array.Empty<byte>(),
             LowData = technicalData.LowData ?? Array.Empty<byte>(),
             MidData = technicalData.MidData ?? Array.Empty<byte>(),
             HighData = technicalData.HighData ?? Array.Empty<byte>(),
             PeakData = technicalData.WaveformData ?? Array.Empty<byte>()
         };
         
         // Estimate duration from RMS length (assuming 100 points/sec)
         double duration = (technicalData.RmsData?.Length ?? 0) / 100.0;
         if (duration < 30) duration = 180.0; // Fallback
         
         return await GenerateCuesAsync(data, duration, genre);
    }

    /// <summary>
    /// Phase 10: Industrial Prep Workflow (Synchronous Wrapper)
    /// </summary>
    public List<OrbitCue> GenerateCues(SLSKDONET.Data.Entities.TrackTechnicalEntity technicalData, string? genre = null)
    {
        return GenerateCuesAsync(technicalData, genre).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Legacy/Direct Drop Support
    /// </summary>
    public (double PhraseStart, double Build, double Drop, double Intro) GenerateCues(double dropTime, float bpm)
    {
        // Simple 8-bar phrase math
        double secondsPerBeat = 60.0 / (bpm > 0 ? bpm : 120);
        double bars16 = secondsPerBeat * 4 * 16;
        double bars8 = secondsPerBeat * 4 * 8;
        
        return (
            PhraseStart: Math.Max(0, dropTime - bars16),
            Build: Math.Max(0, dropTime - bars8),
            Drop: dropTime,
            Intro: 0
        );
    }

    /// <summary>
    /// Generates SmartCue objects for the Set Designer timeline.
    /// Converts OrbitCue to the timeline-compatible SmartCue format.
    /// </summary>
    public async Task<List<ViewModels.Timeline.SmartCue>> GenerateSmartCuesForTimelineAsync(
        WaveformAnalysisData data, 
        double duration, 
        int sampleRate = 44100,
        string? genre = null)
    {
        var orbitCues = await GenerateCuesAsync(data, duration, genre);
        return orbitCues.Select(c => new ViewModels.Timeline.SmartCue
        {
            TimestampSeconds = c.Timestamp,
            SamplePosition = (long)(c.Timestamp * sampleRate),
            Label = c.Name,
            Type = MapCueRole(c.Role),
            Color = c.Color,
            Confidence = (float)c.Confidence
        }).ToList();
    }

    /// <summary>
    /// Generates SmartCue objects from technical entity data.
    /// </summary>
    public async Task<List<ViewModels.Timeline.SmartCue>> GenerateSmartCuesForTimelineAsync(
        SLSKDONET.Data.Entities.TrackTechnicalEntity technicalData,
        int sampleRate = 44100,
        string? genre = null)
    {
        var orbitCues = await GenerateCuesAsync(technicalData, genre);
        return orbitCues.Select(c => new ViewModels.Timeline.SmartCue
        {
            TimestampSeconds = c.Timestamp,
            SamplePosition = (long)(c.Timestamp * sampleRate),
            Label = c.Name,
            Type = MapCueRole(c.Role),
            Color = c.Color,
            Confidence = (float)c.Confidence
        }).ToList();
    }

    private ViewModels.Timeline.CueType MapCueRole(CueRole role)
    {
        return role switch
        {
            CueRole.Intro => ViewModels.Timeline.CueType.Intro,
            CueRole.KickIn => ViewModels.Timeline.CueType.Build,
            CueRole.Drop => ViewModels.Timeline.CueType.Drop,
            CueRole.Outro => ViewModels.Timeline.CueType.Outro,
            CueRole.Custom => ViewModels.Timeline.CueType.Custom,
            _ => ViewModels.Timeline.CueType.Custom
        };
    }
}
