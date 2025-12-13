using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SLSKDONET.Services;

public class DatabaseService
{
    private readonly ILogger<DatabaseService> _logger;

    public DatabaseService(ILogger<DatabaseService> logger)
    {
        _logger = logger;
    }

    public async Task InitAsync()
    {
        using var context = new AppDbContext();
        await context.Database.EnsureCreatedAsync();

        // Manual Schema Migration for existing databases
        try 
        {
            // 1. Check for SortOrder in PlaylistTracks
            // We can't easily read results with ExecuteSqlRaw, but we can try to add and catch error, 
            // OR use a safe add column syntax if SQLite supports it (it doesn't support IF NOT EXISTS for columns directly in all versions).
            // A safer way: Try select.
            try
            {
                await context.Database.ExecuteSqlRawAsync("SELECT SortOrder FROM PlaylistTracks LIMIT 1");
            }
            catch
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'SortOrder' to PlaylistTracks");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE PlaylistTracks ADD COLUMN SortOrder INTEGER DEFAULT 0");
            }

            // 2. Check for IsDeleted in PlaylistJobs
            try
            {
                 await context.Database.ExecuteSqlRawAsync("SELECT IsDeleted FROM PlaylistJobs LIMIT 1");
            }
            catch
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'IsDeleted' to PlaylistJobs");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE PlaylistJobs ADD COLUMN IsDeleted INTEGER DEFAULT 0");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to patch database schema");
        }
            // 3. Check for ActivityLogs table
            // Since it's a new table, we can check if it exists in sqlite_master
            try 
            {
               // This works but might be complex to parse. Better: try select
               await context.Database.ExecuteSqlRawAsync("SELECT Id FROM ActivityLogs LIMIT 1");
            }
            catch
            {
                 _logger.LogWarning("Schema Patch: Creating missing table 'ActivityLogs'");
                 // Create table script matching the Entity
                 var createTableSql = @"
                    CREATE TABLE IF NOT EXISTS ActivityLogs (
                        Id TEXT NOT NULL CONSTRAINT PK_ActivityLogs PRIMARY KEY,
                        PlaylistId TEXT NOT NULL,
                        Action TEXT NOT NULL,
                        Details TEXT NOT NULL,
                        Timestamp TEXT NOT NULL,
                        CONSTRAINT FK_ActivityLogs_PlaylistJobs_PlaylistId FOREIGN KEY (PlaylistId) REFERENCES PlaylistJobs (Id) ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS IX_ActivityLogs_PlaylistId ON ActivityLogs (PlaylistId);
                 ";
                 await context.Database.ExecuteSqlRawAsync(createTableSql);
            }




        _logger.LogInformation("Database initialized and schema verified.");
    }

    // ===== Track Methods =====

    public async Task<List<TrackEntity>> LoadTracksAsync()
    {
        using var context = new AppDbContext();
        return await context.Tracks.ToListAsync();
    }

    public async Task SaveTrackAsync(TrackEntity track)
    {
        using var context = new AppDbContext();

        // Apply the atomic upsert pattern. EF Core's Update() method will generate
        // an INSERT for a new entity or an UPDATE for an existing one based on its primary key.
        context.Tracks.Update(track);

        await context.SaveChangesAsync();
    }

    public async Task RemoveTrackAsync(string globalId)
    {
        using var context = new AppDbContext();
        var track = await context.Tracks.FindAsync(globalId);
        if (track != null)
        {
            context.Tracks.Remove(track);
            await context.SaveChangesAsync();
        }
    }

    // Helper to bulk save if needed
    public async Task SaveAllAsync(IEnumerable<TrackEntity> tracks)
    {
        using var context = new AppDbContext();
        foreach(var t in tracks)
        {
            if (!await context.Tracks.AnyAsync(x => x.GlobalId == t.GlobalId))
            {
                await context.Tracks.AddAsync(t);
            }
        }
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Updates the status of a track across all playlists that contain it,
    /// then recalculates the progress counts for those playlists.
    /// </summary>
    public async Task<List<Guid>> UpdatePlaylistTrackStatusAndRecalculateJobsAsync(string trackUniqueHash, TrackStatus newStatus, string? resolvedPath)
    {
        using var context = new AppDbContext();

        // 1. Find all PlaylistTrack entries for this global track hash
        var playlistTracks = await context.PlaylistTracks
            .Where(pt => pt.TrackUniqueHash == trackUniqueHash)
            .ToListAsync();

        if (!playlistTracks.Any()) return new List<Guid>();

        var distinctJobIds = playlistTracks.Select(pt => pt.PlaylistId).Distinct().ToList();

        // 2. Update their status in memory
        foreach (var pt in playlistTracks)
        {
            pt.Status = newStatus;
            if (!string.IsNullOrEmpty(resolvedPath))
            {
                pt.ResolvedFilePath = resolvedPath;
            }
        }
        
        // 3. Fetch all affected jobs and all their related tracks in two efficient queries
        var jobsToUpdate = await context.PlaylistJobs
            .Where(j => distinctJobIds.Contains(j.Id))
            .ToListAsync();

        var allRelatedTracks = await context.PlaylistTracks
            .Where(t => distinctJobIds.Contains(t.PlaylistId))
            .AsNoTracking() // Use NoTracking for the read-only calculation part
            .ToListAsync();

        // 4. Recalculate counts for each job in memory
        foreach (var job in jobsToUpdate)
        {
            // Combine the already-updated tracks with the other tracks for this job
            var currentJobTracks = allRelatedTracks
                .Where(t => t.PlaylistId == job.Id && t.TrackUniqueHash != trackUniqueHash)
                .ToList();
            currentJobTracks.AddRange(playlistTracks.Where(pt => pt.PlaylistId == job.Id));

            job.SuccessfulCount = currentJobTracks.Count(t => t.Status == TrackStatus.Downloaded);
            job.FailedCount = currentJobTracks.Count(t => t.Status == TrackStatus.Failed || t.Status == TrackStatus.Skipped);
        }

        // 5. Save all changes (track status and job counts) in a single transaction
        await context.SaveChangesAsync();
        _logger.LogInformation("Updated status for track {Hash}, affecting {TrackCount} playlist entries and recalculating {JobCount} jobs.", trackUniqueHash, playlistTracks.Count, jobsToUpdate.Count);

        return distinctJobIds;
    }

    // ===== LibraryEntry Methods =====

    public async Task<LibraryEntryEntity?> FindLibraryEntryAsync(string uniqueHash)
    {
        using var context = new AppDbContext();
        return await context.LibraryEntries.FindAsync(uniqueHash);
    }

    public async Task<List<LibraryEntryEntity>> LoadAllLibraryEntriesAsync()
    {
        using var context = new AppDbContext();
        return await context.LibraryEntries.AsNoTracking().ToListAsync();
    }

    public async Task SaveLibraryEntryAsync(LibraryEntryEntity entry)
    {
        using var context = new AppDbContext();

        // EF Core's Update() on a detached entity with a PK acts as an "upsert".
        // It will generate an INSERT if the key doesn't exist, or an UPDATE if it does.
        // This is more atomic than a separate read-then-write operation.
        // Note: For this to work, LibraryEntryEntity.UniqueHash must be configured as the primary key.
        entry.LastUsedAt = DateTime.UtcNow; // Ensure the timestamp is always updated.
        context.LibraryEntries.Update(entry);
        
        await context.SaveChangesAsync();
    }


    // ===== PlaylistJob Methods =====

    public async Task<List<PlaylistJobEntity>> LoadAllPlaylistJobsAsync()
    {
        using var context = new AppDbContext();
        return await context.PlaylistJobs
            .AsNoTracking()
            .Where(j => !j.IsDeleted)
            .Include(j => j.Tracks)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync();
    }

    public async Task<PlaylistJobEntity?> LoadPlaylistJobAsync(Guid jobId)
    {
        using var context = new AppDbContext();
        return await context.PlaylistJobs.AsNoTracking()
            .Include(j => j.Tracks)
            .FirstOrDefaultAsync(j => j.Id == jobId);
    }

    public async Task SavePlaylistJobAsync(PlaylistJobEntity job)
    {
        using var context = new AppDbContext();

        // Use the same atomic upsert pattern for PlaylistJobs.
        // EF Core will handle INSERT vs. UPDATE based on the job.Id primary key.
        // We set CreatedAt here if it's a new entity. The DB context tracks the entity state.
        if (context.Entry(job).State == EntityState.Detached)
             job.CreatedAt = DateTime.UtcNow;
        context.PlaylistJobs.Update(job);
        await context.SaveChangesAsync();
        _logger.LogInformation("Saved PlaylistJob: {Title} ({Id})", job.SourceTitle, job.Id);
    }

    public async Task DeletePlaylistJobAsync(Guid jobId)
    {
        using var context = new AppDbContext();
        var job = await context.PlaylistJobs.FindAsync(jobId);
        if (job != null)
        {
            context.PlaylistJobs.Remove(job);
            await context.SaveChangesAsync();
            _logger.LogInformation("Deleted PlaylistJob: {Id}", jobId);
        }
    }

    public async Task SoftDeletePlaylistJobAsync(Guid jobId)
    {
        using var context = new AppDbContext();
        var job = await context.PlaylistJobs.FindAsync(jobId);
        if (job != null)
        {
            job.IsDeleted = true;
            job.DeletedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
            _logger.LogInformation("Soft-deleted PlaylistJob: {Id}", jobId);
        }
    }


    // ===== PlaylistTrack Methods =====

    public async Task<List<PlaylistTrackEntity>> LoadPlaylistTracksAsync(Guid jobId)
    {
        using var context = new AppDbContext();
        return await context.PlaylistTracks
            .Where(t => t.PlaylistId == jobId)
            .OrderBy(t => t.TrackNumber)
            .ToListAsync();
    }

    public async Task SavePlaylistTrackAsync(PlaylistTrackEntity track)
    {
        using var context = new AppDbContext();

        // Apply the atomic upsert pattern. EF Core handles INSERT vs. UPDATE.
        // Set AddedAt only if the entity is new (detached).
        if (context.Entry(track).State == EntityState.Detached)
        {
            track.AddedAt = DateTime.UtcNow;
        }
        context.PlaylistTracks.Update(track);

        await UpdatePlaylistJobCountersAsync(context, track.PlaylistId);
        await context.SaveChangesAsync();
    }

    private static async Task UpdatePlaylistJobCountersAsync(AppDbContext context, Guid playlistId)
    {
        var job = await context.PlaylistJobs.FirstOrDefaultAsync(j => j.Id == playlistId);
        if (job == null)
        {
            return;
        } 
        var statuses = await context.PlaylistTracks.AsNoTracking()
            .Where(t => t.PlaylistId == playlistId)
            .Select(t => t.Status)
            .ToListAsync();

        job.TotalTracks = statuses.Count;
        job.SuccessfulCount = statuses.Count(s => s == TrackStatus.Downloaded);
        job.FailedCount = statuses.Count(s => s == TrackStatus.Failed || s == TrackStatus.Skipped);

        var remaining = statuses.Count(s => s == TrackStatus.Missing);
        if (job.TotalTracks > 0 && remaining == 0)
        {
            job.CompletedAt ??= DateTime.UtcNow;
        }
        else
        {
            job.CompletedAt = null;
        }
    }

    public async Task SavePlaylistTracksAsync(IEnumerable<PlaylistTrackEntity> tracks)
    {
        using var context = new AppDbContext();
        
        // Apply the atomic upsert pattern for a collection of entities.
        // Set AddedAt for any new entities.
        foreach (var track in tracks.Where(t => context.Entry(t).State == EntityState.Detached))
        {
            track.AddedAt = DateTime.UtcNow;
        }
        context.PlaylistTracks.UpdateRange(tracks);

        await context.SaveChangesAsync();
        _logger.LogInformation("Saved {Count} playlist tracks", tracks.Count());
    }

    public async Task DeletePlaylistTracksAsync(Guid jobId)
    {
        using var context = new AppDbContext();
        var tracks = await context.PlaylistTracks
            .Where(t => t.PlaylistId == jobId)
            .ToListAsync();
        
        foreach (var track in tracks)
        {
            context.PlaylistTracks.Remove(track);
        }
        
        await context.SaveChangesAsync();
        await context.SaveChangesAsync();
        _logger.LogInformation("Deleted {Count} playlist tracks for job {JobId}", tracks.Count, jobId);
    }

    public async Task DeleteSinglePlaylistTrackAsync(Guid playlistTrackId)
    {
        using var context = new AppDbContext();
        var track = await context.PlaylistTracks.FindAsync(playlistTrackId);
        if (track != null)
        {
            context.PlaylistTracks.Remove(track);
            await UpdatePlaylistJobCountersAsync(context, track.PlaylistId);
            await context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Atomically saves a PlaylistJob and all its associated tracks in a single transaction.
    /// This ensures data integrity: either the entire job+tracks are saved, or none are.
    /// Called by DownloadManager.QueueProject() for imports.
    /// </summary>
    public async Task SavePlaylistJobWithTracksAsync(PlaylistJob job)
    {
        using var context = new AppDbContext();
        using var transaction = await context.Database.BeginTransactionAsync();
        
        try
        {
            // Convert model to entity
            var jobEntity = new PlaylistJobEntity
            {
                Id = job.Id,
                SourceTitle = job.SourceTitle,
                SourceType = job.SourceType,
                DestinationFolder = job.DestinationFolder,
                CreatedAt = job.CreatedAt,
                TotalTracks = job.TotalTracks,
                SuccessfulCount = job.SuccessfulCount,
                FailedCount = job.FailedCount,
                IsDeleted = false
            };
            
            // Check if job exists to decide between Add (Insert) and Update
            var exists = await context.PlaylistJobs.AnyAsync(j => j.Id == job.Id);

            if (exists)
            {
                context.PlaylistJobs.Update(jobEntity);
            }
            else
            {
                context.PlaylistJobs.Add(jobEntity);
            }
            
            // For tracks, we also need to handle Add vs Update. 
            if (!exists)
            {
                var trackEntities = job.PlaylistTracks.Select(track => new PlaylistTrackEntity
                {
                    Id = track.Id,
                    PlaylistId = job.Id,
                    Artist = track.Artist,
                    Title = track.Title,
                    Album = track.Album,
                    TrackUniqueHash = track.TrackUniqueHash,
                    Status = track.Status,
                    ResolvedFilePath = track.ResolvedFilePath,
                    TrackNumber = track.TrackNumber,
                    AddedAt = track.AddedAt,
                    SortOrder = track.SortOrder
                });
                context.PlaylistTracks.AddRange(trackEntities);
            }
            else
            {
                var trackIds = job.PlaylistTracks.Select(t => t.Id).ToList();
                var existingTrackIds = await context.PlaylistTracks
                    .Where(t => trackIds.Contains(t.Id))
                    .Select(t => t.Id)
                    .ToListAsync();
                var existingTrackIdSet = new HashSet<Guid>(existingTrackIds);

                foreach (var track in job.PlaylistTracks)
                {
                    var trackEntity = new PlaylistTrackEntity
                    {
                        Id = track.Id,
                        PlaylistId = job.Id,
                        Artist = track.Artist,
                        Title = track.Title,
                        Album = track.Album,
                        TrackUniqueHash = track.TrackUniqueHash,
                        Status = track.Status,
                        ResolvedFilePath = track.ResolvedFilePath,
                        TrackNumber = track.TrackNumber,
                        AddedAt = track.AddedAt,
                        SortOrder = track.SortOrder
                    };

                    if (existingTrackIdSet.Contains(track.Id))
                    {
                        context.PlaylistTracks.Update(trackEntity);
                    }
                    else
                    {
                        context.PlaylistTracks.Add(trackEntity);
                    }
                }
            }
            
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
            
            _logger.LogInformation(
                "Atomically saved PlaylistJob '{Title}' ({Id}) with {TrackCount} tracks. Thread: {ThreadId}",
                job.SourceTitle,
                job.Id,
                job.PlaylistTracks.Count,
                Thread.CurrentThread.ManagedThreadId);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Failed to save PlaylistJob and tracks - transaction rolled back");
            throw;
        }
    }

    public async Task LogPlaylistJobDiagnostic(Guid jobId)
    {
        using var context = new AppDbContext();
        var job = await context.PlaylistJobs
            .AsNoTracking()
            .Include(j => j.Tracks)
            .FirstOrDefaultAsync(j => j.Id == jobId);

        if (job == null)
        {
            _logger.LogWarning("DIAGNOSTIC: JobId {JobId} not found.", jobId);
            return;
        }

        _logger.LogInformation(
            "DIAGNOSTIC for JobId {JobId}: Title='{SourceTitle}', IsDeleted={IsDeleted}, CreatedAt={CreatedAt}, TotalTracks={TotalTracks}",
            job.Id,
            job.SourceTitle,
            job.IsDeleted,
            job.CreatedAt,
            job.TotalTracks
        );

        foreach (var track in job.Tracks)
        {
            _logger.LogInformation(
                "  DIAGNOSTIC for Track {TrackId} in Job {JobId}: Artist='{Artist}', Title='{Title}', TrackUniqueHash='{TrackUniqueHash}', Status='{Status}'",
                track.Id,
                job.Id,
                track.Artist,
                track.Title,
                track.TrackUniqueHash,
                track.Status
            );
        }
    }

    public async Task<List<PlaylistTrackEntity>> GetAllPlaylistTracksAsync()
    {
        using var context = new AppDbContext();
        
        // Filter out tracks from soft-deleted jobs
        var validJobIds = context.PlaylistJobs
            .Where(j => !j.IsDeleted)
            .Select(j => j.Id);
            
        return await context.PlaylistTracks
            .AsNoTracking()
            .Where(t => validJobIds.Contains(t.PlaylistId))
            .OrderByDescending(t => t.AddedAt)
            .ToListAsync();
    }

    public async Task LogActivityAsync(PlaylistActivityLogEntity log)
    {
        using var context = new AppDbContext();
        context.ActivityLogs.Add(log);
        await context.SaveChangesAsync();
    }
}
