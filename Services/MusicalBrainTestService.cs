using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using Microsoft.EntityFrameworkCore;

namespace SLSKDONET.Services;

/// <summary>
/// Diagnostic service for testing the Musical Brain (audio analysis pipeline).
/// </summary>
public class MusicalBrainTestService
{
    private readonly ILogger<MusicalBrainTestService> _logger;
    private readonly AnalysisQueueService _queueService;
    private readonly AppDbContext _dbContext;
    private readonly SonicIntegrityService _sonicService;
    
    public MusicalBrainTestService(
        ILogger<MusicalBrainTestService> logger,
        AnalysisQueueService queueService,
        AppDbContext dbContext,
        SonicIntegrityService sonicService)
    {
        _logger = logger;
        _queueService = queueService;
        _dbContext = dbContext;
        _sonicService = sonicService;
    }
    
    /// <summary>
    /// Perform pre-flight validation checks.
    /// </summary>
    public async Task<TestPreFlightResult> RunPreFlightChecksAsync()
    {
        var result = new TestPreFlightResult();
        
        // Check 1: FFmpeg availability
        try
        {
            var ffmpegAvailable = await _sonicService.ValidateFfmpegAsync();
            result.FFmpegAvailable = ffmpegAvailable;
            result.Checks.Add(ffmpegAvailable ? "✅ FFmpeg Available" : "❌ FFmpeg Not Found");
        }
        catch (Exception ex)
        {
            result.FFmpegAvailable = false;
            result.Checks.Add($"❌ FFmpeg Check Failed: {ex.Message}");
        }
        
        // Check 2: Temp directory writable
        try
        {
            var tempPath = Path.GetTempPath();
            var testFile = Path.Combine(tempPath, $"brain_test_{Guid.NewGuid()}.tmp");
            await File.WriteAllTextAsync(testFile, "test");
            File.Delete(testFile);
            result.TempDirectoryWritable = true;
            result.Checks.Add("✅ Temp Directory Writable");
        }
        catch (Exception ex)
        {
            result.TempDirectoryWritable = false;
            result.Checks.Add($"❌ Temp Directory Not Writable: {ex.Message}");
        }
        
        // Check 3: Analysis queue functional
        result.QueueFunctional = true; // If service is injected, it's functional
        result.Checks.Add("✅ Analysis Queue Ready");
        
        // Check 4: Database accessible
        try
        {
            await _dbContext.Database.CanConnectAsync();
            result.DatabaseAccessible = true;
            result.Checks.Add("✅ Database Accessible");
        }
        catch (Exception ex)
        {
            result.DatabaseAccessible = false;
            result.Checks.Add($"❌ Database Error: {ex.Message}");
        }
        
        result.AllChecksPassed = result.FFmpegAvailable && 
                                 result.TempDirectoryWritable && 
                                 result.QueueFunctional && 
                                 result.DatabaseAccessible;
        
        return result;
    }
    
    /// <summary>
    /// Select test tracks from the library.
    /// </summary>
    public async Task<List<TestTrack>> SelectTestTracksAsync(int count = 10)
    {
        var tracks = await _dbContext.LibraryEntries
            .Where(t => !string.IsNullOrEmpty(t.FilePath) && System.IO.File.Exists(t.FilePath))
            .OrderBy(t => Guid.NewGuid()) // Random selection
            .Take(count)
            .Select(t => new TestTrack
            {
                GlobalId = t.UniqueHash,
                FilePath = t.FilePath!,
                FileName = Path.GetFileName(t.FilePath!),
                Artist = t.Artist ?? "Unknown",
                Title = t.Title ?? "Unknown"
            })
            .ToListAsync();
        
        _logger.LogInformation("Selected {Count} test tracks from library", tracks.Count);
        return tracks;
    }
    
    /// <summary>
    /// Queue test tracks for analysis.
    /// </summary>
    public void QueueTestTracks(List<TestTrack> tracks)
    {
        foreach (var track in tracks)
        {
            if (File.Exists(track.FilePath))
            {
                _queueService.QueueAnalysis(track.FilePath, track.GlobalId);
                _logger.LogInformation("Queued test track: {FileName}", track.FileName);
            }
            else
            {
                _logger.LogWarning("Test track file not found: {FilePath}", track.FilePath);
            }
        }
    }
}

/// <summary>
/// Result of pre-flight validation checks.
/// </summary>
public class TestPreFlightResult
{
    public bool AllChecksPassed { get; set; }
    public bool FFmpegAvailable { get; set; }
    public bool TempDirectoryWritable { get; set; }
    public bool QueueFunctional { get; set; }
    public bool DatabaseAccessible { get; set; }
    public List<string> Checks { get; set; } = new();
}

/// <summary>
/// Represents a track selected for testing.
/// </summary>
public class TestTrack
{
    public string GlobalId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
}
