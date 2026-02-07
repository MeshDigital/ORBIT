using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Testing;

/// <summary>
/// Session "Flight Recorder" that logs telemetry events during stress tests.
/// Creates a JSON log file for post-mortem analysis ("Session Autopsy").
/// </summary>
public class SessionAutopsyService
{
    private readonly ILogger<SessionAutopsyService> _logger;
    private readonly string _logDirectory;
    private readonly List<TelemetryEvent> _events = new();
    private DateTime _sessionStart;
    private bool _isRecording;

    public SessionAutopsyService(ILogger<SessionAutopsyService> logger)
    {
        _logger = logger;
        _logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ORBIT", "FlightRecorder");
        
        Directory.CreateDirectory(_logDirectory);
    }

    /// <summary>
    /// Starts a new recording session.
    /// </summary>
    public void StartSession(string sessionName)
    {
        _events.Clear();
        _sessionStart = DateTime.UtcNow;
        _isRecording = true;
        
        RecordEvent(TelemetryEventType.SessionStart, sessionName, new { Timestamp = _sessionStart });
        _logger.LogInformation("🔴 Flight Recorder started: {Session}", sessionName);
    }

    /// <summary>
    /// Record a telemetry event during the session.
    /// </summary>
    public void RecordEvent(TelemetryEventType type, string description, object? data = null)
    {
        if (!_isRecording) return;

        var evt = new TelemetryEvent
        {
            Timestamp = DateTime.UtcNow,
            OffsetMs = (DateTime.UtcNow - _sessionStart).TotalMilliseconds,
            Type = type,
            Description = description,
            Data = data
        };

        _events.Add(evt);
    }

    /// <summary>
    /// Record a frame metric (FPS, Jitter, CPU).
    /// </summary>
    public void RecordFrameMetrics(double fps, double jitterMs, double cpuPercent)
    {
        RecordEvent(TelemetryEventType.FrameMetrics, $"FPS: {fps:F1}", new
        {
            Fps = fps,
            JitterMs = jitterMs,
            CpuPercent = cpuPercent
        });
    }

    /// <summary>
    /// Record a creative move (crossfader, stem toggle, etc).
    /// </summary>
    public void RecordCreativeMove(string moveType, string details)
    {
        RecordEvent(TelemetryEventType.CreativeMove, $"{moveType}: {details}", new
        {
            MoveType = moveType,
            Details = details
        });
    }

    /// <summary>
    /// Record a system stress event (CPU spike, jitter increase).
    /// </summary>
    public void RecordStressEvent(string stressType, double value, string? warning = null)
    {
        RecordEvent(TelemetryEventType.StressEvent, $"{stressType}: {value:F2}", new
        {
            StressType = stressType,
            Value = value,
            Warning = warning
        });
    }

    /// <summary>
    /// Record a phase transition.
    /// </summary>
    public void RecordPhaseChange(int phase, string phaseName)
    {
        RecordEvent(TelemetryEventType.PhaseChange, $"Phase {phase}: {phaseName}", new
        {
            Phase = phase,
            PhaseName = phaseName
        });
    }

    /// <summary>
    /// Ends the recording session and writes to disk.
    /// </summary>
    public string EndSession(StressTestMetrics? finalMetrics = null)
    {
        if (!_isRecording) return string.Empty;

        _isRecording = false;

        RecordEvent(TelemetryEventType.SessionEnd, "Session complete", new
        {
            Duration = (DateTime.UtcNow - _sessionStart).TotalSeconds,
            FinalMetrics = finalMetrics
        });

        // Create session report
        var report = new SessionAutopsyReport
        {
            SessionStart = _sessionStart,
            SessionEnd = DateTime.UtcNow,
            Duration = DateTime.UtcNow - _sessionStart,
            TotalEvents = _events.Count,
            Events = _events,
            FinalMetrics = finalMetrics,
            Verdict = AnalyzeSession(finalMetrics)
        };

        // Save to JSON
        var filename = $"autopsy_{_sessionStart:yyyyMMdd_HHmmss}.json";
        var filepath = Path.Combine(_logDirectory, filename);

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        File.WriteAllText(filepath, JsonSerializer.Serialize(report, options));
        _logger.LogInformation("💾 Flight Recorder saved: {Path}", filepath);

        return filepath;
    }

    /// <summary>
    /// Analyzes the session to generate a verdict.
    /// </summary>
    private SessionVerdict AnalyzeSession(StressTestMetrics? metrics)
    {
        if (metrics == null) return new SessionVerdict { Grade = "N/A", Summary = "No metrics recorded." };

        var warnings = new List<string>();
        
        if (metrics.AverageFps < 55)
            warnings.Add($"Low FPS: {metrics.AverageFps:F1} (target: 55+)");
        
        if (metrics.JitterMs > 5)
            warnings.Add($"High Jitter: {metrics.JitterMs:F2}ms (target: <5ms)");
        
        if (metrics.MaxCpuPercent > 20)
            warnings.Add($"CPU Spike: {metrics.MaxCpuPercent:F1}% (target: <20%)");
        
        if (metrics.DroppedFrames > 10)
            warnings.Add($"Frame Drops: {metrics.DroppedFrames} (target: <10)");

        // Calculate grade
        string grade;
        if (warnings.Count == 0)
            grade = "✅ A+ (Production Ready)";
        else if (warnings.Count == 1)
            grade = "⚠️ B (Minor Issues)";
        else if (warnings.Count == 2)
            grade = "🟠 C (Needs Optimization)";
        else
            grade = "❌ F (Critical Issues)";

        return new SessionVerdict
        {
            Grade = grade,
            Summary = warnings.Count == 0 
                ? "All metrics within acceptable range. System is performance-ready."
                : $"Issues detected: {string.Join("; ", warnings)}",
            Warnings = warnings
        };
    }

    /// <summary>
    /// Gets the path to the most recent autopsy file.
    /// </summary>
    public string? GetLatestAutopsyPath()
    {
        var files = Directory.GetFiles(_logDirectory, "autopsy_*.json");
        return files.OrderByDescending(f => f).FirstOrDefault();
    }
}

#region Models

public enum TelemetryEventType
{
    SessionStart,
    SessionEnd,
    PhaseChange,
    FrameMetrics,
    CreativeMove,
    StressEvent,
    Warning,
    Error
}

public class TelemetryEvent
{
    public DateTime Timestamp { get; set; }
    public double OffsetMs { get; set; }
    public TelemetryEventType Type { get; set; }
    public string Description { get; set; } = string.Empty;
    public object? Data { get; set; }
}

public class SessionAutopsyReport
{
    public DateTime SessionStart { get; set; }
    public DateTime SessionEnd { get; set; }
    public TimeSpan Duration { get; set; }
    public int TotalEvents { get; set; }
    public List<TelemetryEvent> Events { get; set; } = new();
    public StressTestMetrics? FinalMetrics { get; set; }
    public SessionVerdict Verdict { get; set; } = new();
}

public class SessionVerdict
{
    public string Grade { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = new();
}

#endregion
