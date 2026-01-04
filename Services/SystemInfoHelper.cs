using System;

namespace SLSKDONET.Services;

/// <summary>
/// Helper class for detecting system resources and calculating optimal parallelism.
/// </summary>
public static class SystemInfoHelper
{
    /// <summary>
    /// Get the optimal number of parallel analysis threads based on available system resources.
    /// </summary>
    /// <param name="configuredValue">User-configured value (0 = auto-detect)</param>
    /// <returns>Recommended parallel thread count (minimum 1)</returns>
    public static int GetOptimalParallelism(int configuredValue = 0)
    {
        // If user configured a specific value, honor it
        if (configuredValue > 0)
            return Math.Min(configuredValue, 32); // Cap at 32 for safety
            
        var cores = Environment.ProcessorCount;
        var ramGB = GetTotalRamGB();
        
        // Conservative calculation:
        // - Leave at least 1 core free for system/UI
        // - Allocate 300MB RAM per analysis thread
        // - Reserve 2GB for system
        int byCores = Math.Max(1, cores - 1);
        int byRam = Math.Max(1, (int)((ramGB - 2.0) / 0.3)); // 300MB per track
        
        // Take the minimum to avoid overloading either resource
        var optimal = Math.Min(byCores, byRam);
        
        // Special cases for common configurations
        if (cores >= 16 && ramGB >= 32)
            optimal = Math.Min(optimal, 12); // Cap high-end at 12 to leave headroom
        else if (cores <= 4 || ramGB < 8)
            optimal = Math.Min(optimal, 2); // Conservative for entry-level PCs
            
        // Phase 3: Power Sensitivity
        // Halve threads if on battery to save energy
        if (GetCurrentPowerMode() == PowerEfficiencyMode.Efficiency)
        {
            optimal = Math.Max(1, optimal / 2);
        }

        return optimal;
    }
    
    /// <summary>
    /// Get total available system RAM in gigabytes.
    /// </summary>
    public static double GetTotalRamGB()
    {
        try
        {
            var memoryInfo = GC.GetGCMemoryInfo();
            return memoryInfo.TotalAvailableMemoryBytes / (1024.0 * 1024.0 * 1024.0);
        }
        catch
        {
            // Fallback if GC memory info unavailable
            return 8.0; // Assume 8GB as safe default
        }
    }
    
    /// <summary>
    /// Get a human-readable description of the system configuration.
    /// </summary>
    public static string GetSystemDescription()
    {
        var cores = Environment.ProcessorCount;
        var ramGB = GetTotalRamGB();
        return $"{cores} cores, {ramGB:F1}GB RAM";
    }

    /// <summary>
    /// Safely configures the process priority (e.g., to "BelowNormal" for background tasks).
    /// </summary>
    public static void ConfigureProcessPriority(System.Diagnostics.Process process, System.Diagnostics.ProcessPriorityClass priority)
    {
        try
        {
            process.PriorityClass = priority;
        }
        catch (Exception ex)
        {
            // Ignore errors (e.g. access denied on some systems). 
            // Better to run at normal priority than crash.
            System.Diagnostics.Debug.WriteLine($"Failed to set process priority: {ex.Message}");
        }
    }

    // ==========================================
    // Phase 3: Power Sensitivity (Win32 P/Invoke)
    // ==========================================
    
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus lpSystemPowerStatus);

    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    public enum PowerEfficiencyMode
    {
        Performance, // Plugged In
        Efficiency   // On Battery
    }

    /// <summary>
    /// Detects if the system is running on battery power to throttle background tasks.
    /// </summary>
    public static PowerEfficiencyMode GetCurrentPowerMode()
    {
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            if (GetSystemPowerStatus(out var status))
            {
                // ACLineStatus: 0 = Offline (Battery), 1 = Online (Plugged In), 255 = Unknown
                if (status.ACLineStatus == 0) 
                    return PowerEfficiencyMode.Efficiency;
            }
        }
        
        // Default to Performance if unknown or plugged in
        return PowerEfficiencyMode.Performance;
    }
}
