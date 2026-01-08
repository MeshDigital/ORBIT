using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Configuration;

namespace SLSKDONET.Services;

public interface ISafetyFilterService
{
    bool IsSafe(Track candidate, string query, int? targetDurationSeconds = null);
    bool IsUpscaled(PlaylistTrack track);
    SafetyCheckResult EvaluateCandidate(Track candidate, string query, int? targetDuration = null);
    void EvaluateSafety(Track track, string query);
}

/// <summary>
/// "The Gatekeeper"
/// Enforces integrity standards on tracks before they are added to the library or downloaded.
/// Validates bitrate vs frequency cutoff (fake FLAC detection), duration matching, and blacklists.
/// </summary>
public class SafetyFilterService : ISafetyFilterService
{
    private readonly ILogger<SafetyFilterService> _logger;
    private readonly AppConfig _config;
    private readonly string[] _bannedExtensions = new[] { ".exe", ".zip", ".rar", ".lnk", ".bat", ".cmd", ".vbs", ".dmg", ".iso" };

    public SafetyFilterService(ILogger<SafetyFilterService> logger, AppConfig config)
    {
        _logger = logger;
        _config = config;
    }

    /// <summary>
    /// Phase 14A: The Bouncer.
    /// Evaluates track safety and flags suspicious files (Fake FLACs, Bad Users) without hiding them.
    /// Sets track.IsFlagged and track.FlagReason.
    /// </summary>
    /// <summary>
    /// Phase 14A: The Bouncer.
    /// Evaluates track safety and flags suspicious files (Fake FLACs, Bad Users) without hiding them.
    /// Sets track.IsFlagged and track.FlagReason.
    /// </summary>
    public void EvaluateSafety(Track track, string query)
    {
        var result = EvaluateCandidate(track, query, null);
        
        if (!result.IsSafe)
        {
            track.IsFlagged = true;
            track.FlagReason = result.Reason;
        }
        else
        {
            track.IsFlagged = false;
            track.FlagReason = null;
        }
    }

    /// <summary>
    /// Evaluates a candidate track and returns a detailed safety result.
    /// Used by both the UI (via EvaluateSafety) and the Automated Seeker (for Audit Logging).
    /// </summary>
    public SafetyCheckResult EvaluateCandidate(Track candidate, string query, int? targetDuration = null)
    {
        // 1. Check Extension Blacklist
        var ext = candidate.GetExtension().ToLower();
        if (_bannedExtensions.Contains(ext))
        {
            return new SafetyCheckResult(false, "Banned Extension", $"Extension '{ext}' is not allowed.");
        }

        // 2. Fake FLAC Detector (Heuristic: Size vs Duration)
        if (ext == "flac" && candidate.Length > 0 && candidate.Size.HasValue)
        {
            double duration = candidate.Length.Value;
            double sizeBytes = candidate.Size.Value;
            
            // Expected FLAC size (approx 900kbps avg, can vary)
            double kbps = (sizeBytes * 8) / (duration * 1024);
            
            if (kbps < 400) // Lower than reasonable FLAC compression
            {
                return new SafetyCheckResult(false, "Suspected Fake FLAC", $"Suspicious Size: {kbps:F0}kbps (Typical FLAC > 700kbps)");
            }
        }

        // 3. Bitrate Check (Low Quality)
        if (candidate.Bitrate > 0 && candidate.Bitrate < 128)
        {
             return new SafetyCheckResult(false, "Low Quality", $"Bitrate {candidate.Bitrate}kbps is below 128kbps threshold.");
        }
        
        // 4. Manual Blacklist (Keywords/Users)
        if (IsBlacklisted(candidate))
        {
             return new SafetyCheckResult(false, "Blacklisted", "Matches banned keyword or user.");
        }

        // 5. Duration Check (if target provided)
        if (targetDuration.HasValue && candidate.Length.HasValue)
        {
             if (!ValidateDuration(candidate.Length.Value, targetDuration.Value))
             {
                 return new SafetyCheckResult(false, "Duration Mismatch", $"Length {candidate.Length}s != Target {targetDuration}s");
             }
        }

        return new SafetyCheckResult(true, "Passed", null);
    }

    // Deprecated IsSafe for compatibility if needed, but we should switch completely.
    public bool IsSafe(Track track, string query, int? targetDurationSeconds = null) 
    {
        var result = EvaluateCandidate(track, query, targetDurationSeconds);
        if (!result.IsSafe)
        {
            track.IsFlagged = true;
            track.FlagReason = result.Reason;
        }
        return result.IsSafe;
    }

    /// <summary>
    /// Checks if a search result contains banned keywords or matches blacklisted criteria.
    /// </summary>
    private bool IsBlacklisted(Track item)
    {
        // 1. Extension check
        var ext = Path.GetExtension(item.Filename)?.ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) return true; // No extension? Suspicious.

        var allowedExtensions = new[] { ".mp3", ".flac", ".wav", ".aiff", ".m4a", ".aac", ".ogg" };
        if (!allowedExtensions.Contains(ext)) return true; // Filter exes, zips, etc.

        // 2. Keyword check
        // Guard against null filename
        if (string.IsNullOrEmpty(item.Filename)) return true;
        
        var filename = item.Filename.ToLowerInvariant();
        var bannedKeywords = new[] 
        { 
            "password", "virus", "install", ".exe", ".lnk", 
            "ringtone", "snippet", "preview" 
        };

        if (bannedKeywords.Any(k => filename.Contains(k))) return true;

        if (item.Username != null && _config.BlacklistedUsers.Contains(item.Username))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a search result candidate represents a potentially upscaled or fake file.
    /// Note: Search results usually don't have frequency data yet.
    /// This method is primarily for analyzing downloaded tracks (`PlaylistTrack`).
    /// </summary>
    public bool IsUpscaled(PlaylistTrack track)
    {
        // Require Frequency Cutoff data to make a determination
        if (!track.FrequencyCutoff.HasValue) return false;

        var cutoff = track.FrequencyCutoff.Value;
        var bitrate = track.Bitrate;

        // 1. Fake 320kbps / FLAC check
        // Real 320kbps MP3s usually cutoff around 20kHz or 20.5kHz.
        // 192kbps cuts off around 18-19kHz.
        // 128kbps cuts off around 16-17kHz.
        
        // If it claims to be high quality (> 256kbps) but has low shelf...
        if (bitrate >= 256 && cutoff < 17000)
        {
            _logger.LogWarning("Potential upscale detected: {Track} claims {Bitrate}kbps but cutoff is {Cutoff}Hz", track.Title, bitrate, cutoff);
            return true;
        }

        // If it claims to be FLAC (lossless) but looks like MP3 shelf
        if ((track.Format?.Equals("flac", StringComparison.OrdinalIgnoreCase) ?? false) && cutoff < 21000)
        {
             _logger.LogWarning("Potential Fake FLAC detected: {Track} is FLAC but cutoff is {Cutoff}Hz", track.Title, cutoff);
             return true;
        }

        return false;
    }

    /// <summary>
    /// Validates if the candidate track matches the target duration within tolerance.
    /// Helps reject Extended Mixes when looking for Radio Edits, or vice versa.
    /// </summary>
    public bool ValidateDuration(int candidateSeconds, int targetSeconds)
    {
        if (targetSeconds <= 0) return true; // No target to match against

        // Strict Mode: +/- 3 seconds
        const int ToleranceSeconds = 5;

        return Math.Abs(candidateSeconds - targetSeconds) <= ToleranceSeconds;
    }
}

public record SafetyCheckResult(bool IsSafe, string Reason, string? TechnicalDetails);
