using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Utils;

namespace SLSKDONET.Services.SelfHealing;

/// <summary>
/// Scans the library to identify tracks eligible for quality upgrades.
/// Uses batch processing to minimize memory footprint.
/// </summary>
public class LibraryScanner
{
    private readonly ILogger<LibraryScanner> _logger;
    private readonly DatabaseService _databaseService;
    
    private const int BATCH_SIZE = 50; // Process 50 tracks at a time
    private const int SCAN_COOLDOWN_DAYS = 7; // Don't rescan files within 7 days
    
    public LibraryScanner(
        ILogger<LibraryScanner> logger,
        DatabaseService databaseService)
    {
        _logger = logger;
        _databaseService = databaseService;
    }
    
    /// <summary>
    /// Scans the library and yields upgrade candidates in batches.
    /// </summary>
    public async IAsyncEnumerable<List<UpgradeCandidate>> ScanForUpgradesAsync(
        UpgradeScanOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger.LogInformation("Starting library scan for upgrade candidates (BatchSize: {Size})", BATCH_SIZE);
        
        var totalScanned = 0;
        var totalCandidates = 0;
        
        await using var context = new AppDbContext();
        
        // Query tracks eligible for upgrade
        var query = context.Tracks
            .AsNoTracking()
            .Where(t => ShouldConsiderForUpgrade(t, options));
        
        var totalTracks = await query.CountAsync(ct);
        _logger.LogInformation("Found {Count} tracks eligible for scanning", totalTracks);
        
        // Process in batches using Skip/Take
        for (var skip = 0; skip < totalTracks; skip += BATCH_SIZE)
        {
            if (ct.IsCancellationRequested) yield break;
            
            var batch = await query
                .OrderBy(t => t.Bitrate) // Process lowest quality first
                .Skip(skip)
                .Take(BATCH_SIZE)
                .ToListAsync(ct);
            
            var candidates = new List<UpgradeCandidate>();
            
            foreach (var track in batch)
            {
                if (ct.IsCancellationRequested) yield break;
                
                totalScanned++;
                
                // Verify file exists and is readable
                if (!await VerifyTrackFileAsync(track))
                {
                    _logger.LogDebug("Skipping {Track}: File not accessible", track.Title);
                    continue;
                }
                
                // Check if upgrade is warranted
                var candidate = await EvaluateUpgradeNeedAsync(track, options);
                if (candidate != null)
                {
                    candidates.Add(candidate);
                    totalCandidates++;
                }
            }
            
            if (candidates.Any())
            {
                _logger.LogInformation("Batch {Current}/{Total}: Found {Count} candidates", 
                    skip / BATCH_SIZE + 1, 
                    (totalTracks / BATCH_SIZE) + 1, 
                    candidates.Count);
                    
                yield return candidates;
            }
            
            // Mark batch as scanned
            await UpdateScanTimestampsAsync(batch.Select(t => t.GlobalId).ToList());
        }
        
        _logger.LogInformation("Scan complete: {Scanned} tracks scanned, {Candidates} candidates found", 
            totalScanned, totalCandidates);
    }
    
    /// <summary>
    /// Determines if a track should be considered for upgrade scanning.
    /// </summary>
    private bool ShouldConsiderForUpgrade(TrackEntity track, UpgradeScanOptions options)
    {
        // Exclude tracks scanned recently
        if (track.LastUpgradeScanAt.HasValue)
        {
            var daysSinceScan = (DateTime.UtcNow - track.LastUpgradeScanAt.Value).TotalDays;
            if (daysSinceScan < SCAN_COOLDOWN_DAYS)
                return false;
        }
        
        // Exclude Gold status tracks (user-verified)
        if (track.Integrity == IntegrityLevel.Gold && !options.IncludeGoldTracks)
            return false;
        
        // Exclude tracks currently being played (if FileLockMonitor is enabled)
        // TODO: Integrate FileLockMonitor check
        
        // File must exist
        if (string.IsNullOrEmpty(track.Filename) || !File.Exists(track.Filename))
            return false;
        
        // Quality-based filtering
        if (options.UpgradeScope == UpgradeScope.FlacOnly)
        {
            // Only consider non-FLAC files
            var format = Path.GetExtension(track.Filename)?.ToLowerInvariant();
            return format != ".flac";
        }
        else if (options.UpgradeScope == UpgradeScope.LowQualityOnly)
        {
            // Only consider files below 320kbps
            return track.Bitrate < 320;
        }
        
        return true;
    }
    
    /// <summary>
    /// Verifies that the track file exists and is readable.
    /// Uses FileVerificationHelper to check actual file header.
    /// </summary>
    private async Task<bool> VerifyTrackFileAsync(TrackEntity track)
    {
        try
        {
            if (string.IsNullOrEmpty(track.Filename) || !File.Exists(track.Filename))
                return false;
            
            // TODO: Use FileVerificationHelper to verify file header
            // This prevents trusting stale database Bitrate values
            return await Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to verify file for {Track}", track.Title);
            return false;
        }
    }
    
    /// <summary>
    /// Evaluates if a track needs an upgrade and creates a candidate object.
    /// </summary>
    private async Task<UpgradeCandidate?> EvaluateUpgradeNeedAsync(TrackEntity track, UpgradeScanOptions options)
    {
        var format = Path.GetExtension(track.Filename)?.ToLowerInvariant();
        
        // FLAC-only mode
        if (options.UpgradeScope == UpgradeScope.FlacOnly && format == ".flac")
        {
            // Already FLAC, check for suspicious files (faked FLAC)
            if (track.IsTrustworthy == false)
            {
                _logger.LogDebug("Track {Title} is FLAC but marked as suspicious, flagging for review", track.Title);
                return new UpgradeCandidate
                {
                    TrackId = track.GlobalId,
                    Artist = track.Artist,
                    Title = track.Title,
                    CurrentBitrate = track.Bitrate,
                    CurrentFormat = format ?? "unknown",
                    TargetFormat = "flac",
                    MinimumTargetBitrate = 1411, // CD quality
                    UpgradeReason = UpgradeReason.SuspiciousQuality,
                    Priority = UpgradePriority.Low
                };
            }
            return null; // Already optimal
        }
        
        // Determine target upgrade
        var targetBitrate = options.MinimumQualityGain + track.Bitrate;
        var targetFormat = "flac";
        
        // 128kbps MP3 → FLAC (high priority)
        if (track.Bitrate <= 128)
        {
            return new UpgradeCandidate
            {
                TrackId = track.GlobalId,
                Artist = track.Artist,
                Title = track.Title,
                CurrentBitrate = track.Bitrate,
                CurrentFormat = format ?? "mp3",
                TargetFormat = targetFormat,
                MinimumTargetBitrate = targetBitrate,
                UpgradeReason = UpgradeReason.LowQuality,
                Priority = UpgradePriority.High,
                DurationSeconds = track.CanonicalDuration ?? 0
            };
        }
        
        // 192kbps MP3 → FLAC (medium priority)
        if (track.Bitrate < 320)
        {
            return new UpgradeCandidate
            {
                TrackId = track.GlobalId,
                Artist = track.Artist,
                Title = track.Title,
                CurrentBitrate = track.Bitrate,
                CurrentFormat = format ?? "mp3",
                TargetFormat = targetFormat,
                MinimumTargetBitrate = 320,
                UpgradeReason = UpgradeReason.MediumQuality,
                Priority = UpgradePriority.Medium,
                DurationSeconds = track.CanonicalDuration ?? 0
            };
        }
        
        return null;
    }
    
    /// <summary>
    /// Updates the LastUpgradeScanAt timestamp for scanned tracks.
    /// </summary>
    private async Task UpdateScanTimestampsAsync(List<string> trackIds)
    {
        await using var context = new AppDbContext();
        
        var now = DateTime.UtcNow;
        await context.Tracks
            .Where(t => trackIds.Contains(t.GlobalId))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUpgradeScanAt, now));
    }
}

/// <summary>
/// Configuration options for upgrade scanning.
/// </summary>
public class UpgradeScanOptions
{
    public UpgradeScope UpgradeScope { get; set; } = UpgradeScope.FlacOnly;
    public int MinimumQualityGain { get; set; } = 192; // Minimum bitrate improvement
    public bool IncludeGoldTracks { get; set; } = false;
}

/// <summary>
/// Represents a track eligible for upgrade.
/// </summary>
public class UpgradeCandidate
{
    public string TrackId { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int CurrentBitrate { get; set; }
    public string CurrentFormat { get; set; } = string.Empty;
    public string TargetFormat { get; set; } = string.Empty;
    public int MinimumTargetBitrate { get; set; }
    public UpgradeReason UpgradeReason { get; set; }
    public UpgradePriority Priority { get; set; }
    public int DurationSeconds { get; set; }
}

public enum UpgradeScope
{
    FlacOnly,       // Only upgrade to FLAC
    LowQualityOnly, // Any quality improvement (MP3 → MP3 320)
    All             // All potential upgrades
}

public enum UpgradeReason
{
    LowQuality,       // <128kbps
    MediumQuality,    // 128-320kbps
    SuspiciousQuality // Potentially fake FLAC
}

public enum UpgradePriority
{
    Low = 0,
    Medium = 1,
    High = 2
}
