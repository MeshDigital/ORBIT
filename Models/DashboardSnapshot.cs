using System;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.ViewModels; 

namespace SLSKDONET.Models;

public enum SystemHealth
{
    Excellent,
    Good,
    Warning,
    Critical
}

/// <summary>
/// Immutable snapshot of the system state for the Mission Control Dashboard.
/// </summary>
public class DashboardSnapshot
{
    public DateTime CapturedAt { get; init; } = DateTime.UtcNow;
    public SystemHealth SystemHealth { get; init; } = SystemHealth.Excellent;
    
    // Resilience Metrics
    public int ActiveDownloads { get; init; }
    public int DeadLetterCount { get; init; }
    public int RecoveredFileCount { get; init; }
    public int ZombieProcessCount { get; init; }
    public List<string> ResilienceLog { get; init; } = new();

    // Forensic Telemetry (Phase 12.7)
    public bool IsForensicLockdownActive { get; init; }
    public double CurrentCpuLoad { get; init; }

    // Active Operations
    public List<MissionOperation> ActiveOperations { get; init; } = new();

    /// <summary>
    /// Generates a hash code to detect meaningful UI changes.
    /// </summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SystemHealth);
        hash.Add(ActiveDownloads);
        hash.Add(DeadLetterCount);
        hash.Add(RecoveredFileCount);
        hash.Add(ZombieProcessCount);
        
        // Add log hash (most recent entry)
        if (ResilienceLog.Count > 0)
            hash.Add(ResilienceLog[0]);
            
        // Add operations hash (count + first item)
        hash.Add(ActiveOperations.Count);
        if (ActiveOperations.Count > 0)
            hash.Add(ActiveOperations[0]);

        return hash.ToHashCode();
    }
}
