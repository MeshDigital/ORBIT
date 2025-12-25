using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Services.SelfHealing;

/// <summary>
/// Monitors file locks to prevent upgrading tracks that are currently in use.
/// Dual-layer safety: Internal player check + OS-level exclusive lock validation.
/// </summary>
public class FileLockMonitor
{
    private readonly ILogger<FileLockMonitor> _logger;
    private readonly PlayerViewModel? _playerViewModel;
    
    public FileLockMonitor(ILogger<FileLockMonitor> logger, PlayerViewModel? playerViewModel = null)
    {
        _logger = logger;
        _playerViewModel = playerViewModel;
    }
    
    /// <summary>
    /// Checks if a file is safe to replace.
    /// Layer 1: Checks if ORBIT's player is currently playing this track.
    /// Layer 2: Attempts exclusive OS-level lock to detect external apps (Rekordbox, Serato).
    /// </summary>
    public async Task<FileLockStatus> IsFileSafeToReplaceAsync(string filePath, string? trackId = null)
    {
        _logger.LogDebug("Checking file lock status: {Path}", filePath);
        
        // Layer 1: Internal ORBIT player check
        if (_playerViewModel != null && IsPlayingInOrbit(filePath, trackId))
        {
            _logger.LogInformation("File is currently playing in ORBIT: {Path}", filePath);
            return new FileLockStatus
            {
                IsSafe = false,
                Reason = FileLockReason.PlayingInOrbit,
                Message = "Track is currently playing in ORBIT"
            };
        }
        
        // Layer 2: OS-level exclusive lock check
        var osLockStatus = await IsLockedByExternalProcessAsync(filePath);
        if (!osLockStatus.IsSafe)
        {
            return osLockStatus;
        }
        
        _logger.LogDebug("✅ File is safe to replace: {Path}", filePath);
        return new FileLockStatus { IsSafe = true };
    }
    
    /// <summary>
    /// Checks if the file is currently playing in ORBIT's audio player.
    /// </summary>
    private bool IsPlayingInOrbit(string filePath, string? trackId)
    {
        if (_playerViewModel?.CurrentTrack == null)
            return false;
        
        // Check by TrackId if provided (most reliable)
        if (!string.IsNullOrEmpty(trackId) && _playerViewModel.CurrentTrack.GlobalId == trackId)
        {
            _logger.LogDebug("Track ID match: {TrackId} is currently playing", trackId);
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Attempts to get an exclusive OS-level lock on the file.
    /// If locked by external app (Rekordbox, Serato), returns unsafe status.
    /// Uses the "Exclusive Try-Open" pattern.
    /// </summary>
    private async Task<FileLockStatus> IsLockedByExternalProcessAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new FileLockStatus
            {
                IsSafe = false,
                Reason = FileLockReason.FileNotFound,
                Message = "File not found"
            };
        }
        
        try
        {
            // Attempt to open with exclusive access
            // FileShare.None = No other process can access the file
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1, // Minimal buffer, we're just checking the lock
                useAsync: true
            );
            
            // If we got here, file is not locked
            return new FileLockStatus { IsSafe = true };
        }
        catch (IOException ex)
        {
            // File is locked by another process
            _logger.LogWarning("File is locked by external process: {Path} - {Error}", 
                filePath, ex.Message);
            
            return new FileLockStatus
            {
                IsSafe = false,
                Reason = FileLockReason.LockedByExternalApp,
                Message = $"File is locked by external application (likely Rekordbox or Serato)"
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Access denied for file: {Path}", filePath);
            
            return new FileLockStatus
            {
                IsSafe = false,
                Reason = FileLockReason.AccessDenied,
                Message = "Access denied - check file permissions"
            };
        }
    }
    
    /// <summary>
    /// Checks if a track ID is currently playing (for quick ID-based checks).
    /// </summary>
    public bool IsTrackPlaying(string trackId)
    {
        if (_playerViewModel?.CurrentTrack == null)
            return false;
        
        return _playerViewModel.CurrentTrack.GlobalId == trackId;
    }
}

/// <summary>
/// Result of a file lock check.
/// </summary>
public class FileLockStatus
{
    public bool IsSafe { get; set; }
    public FileLockReason Reason { get; set; } = FileLockReason.None;
    public string Message { get; set; } = string.Empty;
    
    public override string ToString() => IsSafe 
        ? "Safe to replace" 
        : $"Locked: {Reason} - {Message}";
}

/// <summary>
/// Reason why a file cannot be replaced.
/// </summary>
public enum FileLockReason
{
    None = 0,
    PlayingInOrbit = 1,
    LockedByExternalApp = 2,
    FileNotFound = 3,
    AccessDenied = 4
}
