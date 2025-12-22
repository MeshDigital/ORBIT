using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Services.Models;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SLSKDONET.Services;

public class DashboardService
{
    private readonly ILogger<DashboardService> _logger;
    private readonly DatabaseService _databaseService;
    private readonly AppConfig _config;

    public DashboardService(
        ILogger<DashboardService> logger,
        DatabaseService databaseService,
        AppConfig config)
    {
        _logger = logger;
        _databaseService = databaseService;
        _config = config;
    }

    public async Task<LibraryHealthEntity?> GetLibraryHealthAsync()
    {
        try
        {
            using var context = new AppDbContext();
            // We expect only one record with Id=1
            return await context.LibraryHealth.FindAsync(1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch library health from cache");
            return null;
        }
    }

    public async Task RecalculateLibraryHealthAsync()
    {
        try
        {
            _logger.LogInformation("Recalculating library health statistics...");
            
            using var context = new AppDbContext();
            
            var totalTracks = await context.PlaylistTracks.CountAsync();
            var hqTracks = await context.PlaylistTracks.CountAsync(t => t.Bitrate >= 256 || t.Format.ToLower() == "flac");
            var lowBitrateTracks = await context.PlaylistTracks.CountAsync(t => t.Status == TrackStatus.Downloaded && t.Bitrate > 0 && t.Bitrate < 256);
            
            // For storage info
            var storageInsight = GetStorageInsight();
            
            var health = await context.LibraryHealth.FindAsync(1) ?? new LibraryHealthEntity { Id = 1 };
            
            health.TotalTracks = totalTracks;
            health.HqTracks = hqTracks;
            health.UpgradableCount = lowBitrateTracks;
            health.TotalStorageBytes = storageInsight.TotalBytes;
            health.FreeStorageBytes = storageInsight.FreeBytes;
            health.LastScanDate = DateTime.Now;
            
            // Calculate top genres (Simplified aggregation)
            var genreCounts = context.PlaylistTracks
                .Where(t => !string.IsNullOrEmpty(t.Genres))
                .AsEnumerable() // Pull into memory for JSON parsing
                .SelectMany(t => (t.Genres ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .GroupBy(g => g)
                .Select(g => new { Genre = g.Key, Count = g.Count() })
                .OrderByDescending(g => g.Count)
                .Take(5)
                .ToList();
                
            health.TopGenresJson = System.Text.Json.JsonSerializer.Serialize(genreCounts);

            if (context.Entry(health).State == Microsoft.EntityFrameworkCore.EntityState.Detached)
            {
                context.LibraryHealth.Add(health);
            }
            
            await context.SaveChangesAsync();
            _logger.LogInformation("Library health cache updated successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recalculate library health");
        }
    }

    public (long TotalBytes, long FreeBytes) GetStorageInsight()
    {
        try
        {
            var path = _config.DownloadDirectory;
            if (string.IsNullOrEmpty(path)) return (0, 0);

            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return (0, 0);

            var drive = new DriveInfo(root);
            if (drive.IsReady)
            {
                return (drive.TotalSize, drive.AvailableFreeSpace);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve storage insights for {Path}", _config.DownloadDirectory);
        }

        return (0, 0);
    }

    public async Task<List<PlaylistJob>> GetRecentPlaylistsAsync(int count = 5)
    {
        try
        {
            // DatabaseService doesn't have a direct "GetRecent" yet, we'll query it here or add to DatabaseService
            // For now, using AppDbContext directly for simplicity in DashboardService
            using var context = new AppDbContext();
            var entities = await context.PlaylistJobs
                .OrderByDescending(j => j.CreatedAt)
                .Take(count)
                .ToListAsync();
                
            // Use LibraryService or mapper to convert to models if needed, 
            // but for Dashboard maybe entities are fine if they have what we need.
            // Let's assume we want models for the ViewModels.
            // I'll check LibraryService for mapping logic.
            return entities.Select(e => MapToModel(e)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch recent playlists");
            return new List<PlaylistJob>();
        }
    }

    private PlaylistJob MapToModel(PlaylistJobEntity entity)
    {
        return new PlaylistJob
        {
            Id = entity.Id,
            SourceTitle = entity.SourceTitle,
            SourceType = entity.SourceType,
            CreatedAt = entity.CreatedAt,
            TotalTracks = entity.TotalTracks,
            SuccessfulCount = entity.SuccessfulCount,
            FailedCount = entity.FailedCount,
            MissingCount = entity.MissingCount,
            PlaylistTracks = new List<PlaylistTrack>() // Empty list for dashboard display
        };
    }
}
