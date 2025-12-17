using Microsoft.Data.Sqlite;
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
    
    // Semaphore to serialize database write operations and prevent SQLite locking issues
    private static readonly SemaphoreSlim _writeSemaphore = new SemaphoreSlim(1, 1);

    public DatabaseService(ILogger<DatabaseService> logger)
    {
        _logger = logger;
    }

    public async Task InitAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("[{Ms}ms] Database Init: Starting", sw.ElapsedMilliseconds);
        
        using var context = new AppDbContext();
        await context.Database.EnsureCreatedAsync();
        _logger.LogInformation("[{Ms}ms] Database Init: EnsureCreated completed", sw.ElapsedMilliseconds);

        // Manual Schema Migration for existing databases
        try 
        {
            // Optimize: Check all columns at once using PRAGMA table_info
            var connection = context.Database.GetDbConnection();
            await connection.OpenAsync();
            
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(PlaylistTracks)";
            var existingColumns = new HashSet<string>();
            
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    existingColumns.Add(reader.GetString(1)); // Column name is at index 1
                }
            }
            
            _logger.LogInformation("[{Ms}ms] Database Init: Column check completed", sw.ElapsedMilliseconds);
            
            // Add missing columns to PlaylistTracks
            var columnsToAdd = new List<(string Name, string Definition)>();
            
            if (!existingColumns.Contains("SortOrder"))
                columnsToAdd.Add(("SortOrder", "SortOrder INTEGER DEFAULT 0"));
            if (!existingColumns.Contains("Rating"))
                columnsToAdd.Add(("Rating", "Rating INTEGER DEFAULT 0"));
            if (!existingColumns.Contains("IsLiked"))
                columnsToAdd.Add(("IsLiked", "IsLiked INTEGER DEFAULT 0"));
            if (!existingColumns.Contains("PlayCount"))
                columnsToAdd.Add(("PlayCount", "PlayCount INTEGER DEFAULT 0"));
            if (!existingColumns.Contains("LastPlayedAt"))
                columnsToAdd.Add(("LastPlayedAt", "LastPlayedAt TEXT NULL"));
            
            // Phase 0: Spotify Metadata Columns
            if (!existingColumns.Contains("SpotifyTrackId"))
                columnsToAdd.Add(("SpotifyTrackId", "SpotifyTrackId TEXT NULL"));
            if (!existingColumns.Contains("SpotifyAlbumId"))
                columnsToAdd.Add(("SpotifyAlbumId", "SpotifyAlbumId TEXT NULL"));
            if (!existingColumns.Contains("SpotifyArtistId"))
                columnsToAdd.Add(("SpotifyArtistId", "SpotifyArtistId TEXT NULL"));
            if (!existingColumns.Contains("AlbumArtUrl"))
                columnsToAdd.Add(("AlbumArtUrl", "AlbumArtUrl TEXT NULL"));
            if (!existingColumns.Contains("ArtistImageUrl"))
                columnsToAdd.Add(("ArtistImageUrl", "ArtistImageUrl TEXT NULL"));
            if (!existingColumns.Contains("Genres"))
                columnsToAdd.Add(("Genres", "Genres TEXT NULL"));
            if (!existingColumns.Contains("Popularity"))
                columnsToAdd.Add(("Popularity", "Popularity INTEGER NULL"));
            if (!existingColumns.Contains("CanonicalDuration"))
                columnsToAdd.Add(("CanonicalDuration", "CanonicalDuration INTEGER NULL"));
            if (!existingColumns.Contains("ReleaseDate"))
                columnsToAdd.Add(("ReleaseDate", "ReleaseDate TEXT NULL"));

            // Phase 0.1: Musical Intelligence & Antigravity
            if (!existingColumns.Contains("MusicalKey"))
                columnsToAdd.Add(("MusicalKey", "MusicalKey TEXT NULL"));
            if (!existingColumns.Contains("BPM"))
                columnsToAdd.Add(("BPM", "BPM REAL NULL"));
            if (!existingColumns.Contains("CuePointsJson"))
                columnsToAdd.Add(("CuePointsJson", "CuePointsJson TEXT NULL"));
            if (!existingColumns.Contains("AudioFingerprint"))
                columnsToAdd.Add(("AudioFingerprint", "AudioFingerprint TEXT NULL"));
            if (!existingColumns.Contains("BitrateScore"))
                columnsToAdd.Add(("BitrateScore", "BitrateScore INTEGER NULL"));
            if (!existingColumns.Contains("AnalysisOffset"))
                columnsToAdd.Add(("AnalysisOffset", "AnalysisOffset REAL NULL"));
            
            foreach (var (name, definition) in columnsToAdd)
            {
                _logger.LogWarning("Schema Patch: Adding missing column '{Column}' to PlaylistTracks", name);
                var sql = $"ALTER TABLE PlaylistTracks ADD COLUMN {definition}";
                await context.Database.ExecuteSqlRawAsync(sql);
            }
            
            // Check PlaylistJobs table
            cmd.CommandText = "PRAGMA table_info(PlaylistJobs)";
            existingColumns.Clear();
            
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    existingColumns.Add(reader.GetString(1));
                }
            }
            
            if (!existingColumns.Contains("IsDeleted"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'IsDeleted' to PlaylistJobs");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE PlaylistJobs ADD COLUMN IsDeleted INTEGER DEFAULT 0");
            }
            
            _logger.LogInformation("[{Ms}ms] Database Init: Schema patches completed", sw.ElapsedMilliseconds);
            
            // Session 1: Performance Indexes (Phase 2 Performance Overhaul)
            // These indexes provide 50-100x speedup for common queries
            _logger.LogInformation("Creating performance indexes...");
            await context.Database.ExecuteSqlRawAsync(@"
                -- PlaylistTracks indexes for fast filtering/sorting
                CREATE INDEX IF NOT EXISTS idx_playlist_tracks_playlistid 
                ON PlaylistTracks(PlaylistId);
                
                CREATE INDEX IF NOT EXISTS idx_playlist_tracks_status 
                ON PlaylistTracks(Status);
                
                CREATE INDEX IF NOT EXISTS idx_playlist_tracks_globalid 
                ON PlaylistTracks(GlobalId);
                
                -- PlaylistJobs index for Import History sorting
                CREATE INDEX IF NOT EXISTS idx_playlist_jobs_createdat 
                ON PlaylistJobs(CreatedAt);
                
                -- LibraryEntries index for duplicate detection
                CREATE INDEX IF NOT EXISTS idx_library_entries_globalid 
                ON LibraryEntries(GlobalId);
                
                -- Tracks index for Spotify metadata lookups
                CREATE INDEX IF NOT EXISTS idx_tracks_spotifytrackid 
                ON Tracks(SpotifyTrackId);
            ");
            _logger.LogInformation("[{Ms}ms] Database Init: Performance indexes created", sw.ElapsedMilliseconds);
            
            // Check for ActivityLogs table
            try 
            {
               await context.Database.ExecuteSqlRawAsync("SELECT Id FROM ActivityLogs LIMIT 1");
            }
            catch
            {
                 _logger.LogWarning("Schema Patch: Creating missing table 'ActivityLogs'");
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
            
            // Check for QueueItems table (Phase 0: Queue persistence)
            try 
            {
               await context.Database.ExecuteSqlRawAsync("SELECT Id FROM QueueItems LIMIT 1");
            }
            catch
            {
                 _logger.LogWarning("Schema Patch: Creating missing table 'QueueItems'");
                 var createQueueTableSql = @"
                    CREATE TABLE IF NOT EXISTS QueueItems (
                        Id TEXT NOT NULL CONSTRAINT PK_QueueItems PRIMARY KEY,
                        PlaylistTrackId TEXT NOT NULL,
                        QueuePosition INTEGER NOT NULL,
                        AddedAt TEXT NOT NULL,
                        IsCurrentTrack INTEGER NOT NULL DEFAULT 0,
                        CONSTRAINT FK_QueueItems_PlaylistTracks_PlaylistTrackId FOREIGN KEY (PlaylistTrackId) REFERENCES PlaylistTracks (Id) ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS IX_QueueItems_QueuePosition ON QueueItems (QueuePosition);
                 ";
                 await context.Database.ExecuteSqlRawAsync(createQueueTableSql);
            }

            // Check for SpotifyMetadataCache table
            try
            {
               await context.Database.ExecuteSqlRawAsync("SELECT SpotifyId FROM SpotifyMetadataCache LIMIT 1");
            }
            catch
            {
                 _logger.LogWarning("Schema Patch: Creating missing table 'SpotifyMetadataCache'");
                 var createCacheTableSql = @"
                    CREATE TABLE IF NOT EXISTS SpotifyMetadataCache (
                        SpotifyId TEXT NOT NULL CONSTRAINT PK_SpotifyMetadataCache PRIMARY KEY,
                        DataJson TEXT NOT NULL,
                        CachedAt TEXT NOT NULL,
                        ExpiresAt TEXT NOT NULL
                    );
                 ";
                 await context.Database.ExecuteSqlRawAsync(createCacheTableSql);
            }

            // Patch LibraryEntries columns
            using (var schemaCmd = connection.CreateCommand())
            {
                schemaCmd.CommandText = "PRAGMA table_info(LibraryEntries)";
                var libColumns = new HashSet<string>();
                using (var reader = await schemaCmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        libColumns.Add(reader.GetString(1)); // Name is column 1
                    }
                }

                if (libColumns.Count > 0) // Only patch if table exists
                {
                    var newCols = new List<(string Name, string Def)>();
                    if (!libColumns.Contains("SpotifyTrackId")) newCols.Add(("SpotifyTrackId", "TEXT NULL"));
                    if (!libColumns.Contains("SpotifyAlbumId")) newCols.Add(("SpotifyAlbumId", "TEXT NULL"));
                    if (!libColumns.Contains("SpotifyArtistId")) newCols.Add(("SpotifyArtistId", "TEXT NULL"));
                    if (!libColumns.Contains("AlbumArtUrl")) newCols.Add(("AlbumArtUrl", "TEXT NULL"));
                    if (!libColumns.Contains("ArtistImageUrl")) newCols.Add(("ArtistImageUrl", "TEXT NULL"));
                    if (!libColumns.Contains("Genres")) newCols.Add(("Genres", "TEXT NULL"));
                    if (!libColumns.Contains("Popularity")) newCols.Add(("Popularity", "INTEGER NULL"));
                    if (!libColumns.Contains("CanonicalDuration")) newCols.Add(("CanonicalDuration", "INTEGER NULL"));
                    if (!libColumns.Contains("ReleaseDate")) newCols.Add(("ReleaseDate", "TEXT NULL"));

                    // Phase 0.1: Musical Intelligence & Antigravity
                    if (!libColumns.Contains("MusicalKey")) newCols.Add(("MusicalKey", "TEXT NULL"));
                    if (!libColumns.Contains("BPM")) newCols.Add(("BPM", "REAL NULL"));
                    if (!libColumns.Contains("AudioFingerprint")) newCols.Add(("AudioFingerprint", "TEXT NULL"));

                    foreach (var (col, def) in newCols)
                    {
                         _logger.LogWarning("Schema Patch: Adding missing column '{Col}' to LibraryEntries", col);
                         await context.Database.ExecuteSqlRawAsync($"ALTER TABLE LibraryEntries ADD COLUMN {col} {def}");
                    }
                }

            }

            // Patch Tracks columns
            using (var schemaCmd = connection.CreateCommand())
            {
                schemaCmd.CommandText = "PRAGMA table_info(Tracks)";
                var tracksColumns = new HashSet<string>();
                using (var reader = await schemaCmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        tracksColumns.Add(reader.GetString(1));
                    }
                }

                if (tracksColumns.Count > 0)
                {
                    var newCols = new List<(string Name, string Def)>();
                    if (!tracksColumns.Contains("SpotifyTrackId")) newCols.Add(("SpotifyTrackId", "TEXT NULL"));
                    if (!tracksColumns.Contains("SpotifyAlbumId")) newCols.Add(("SpotifyAlbumId", "TEXT NULL"));
                    if (!tracksColumns.Contains("SpotifyArtistId")) newCols.Add(("SpotifyArtistId", "TEXT NULL"));
                    if (!tracksColumns.Contains("AlbumArtUrl")) newCols.Add(("AlbumArtUrl", "TEXT NULL"));
                    if (!tracksColumns.Contains("ArtistImageUrl")) newCols.Add(("ArtistImageUrl", "TEXT NULL"));
                    if (!tracksColumns.Contains("Genres")) newCols.Add(("Genres", "TEXT NULL"));
                    if (!tracksColumns.Contains("Popularity")) newCols.Add(("Popularity", "INTEGER NULL"));
                    if (!tracksColumns.Contains("CanonicalDuration")) newCols.Add(("CanonicalDuration", "INTEGER NULL"));
                    if (!tracksColumns.Contains("ReleaseDate")) newCols.Add(("ReleaseDate", "TEXT NULL"));
                    // CoverArtUrl was already there but check just in case
                    if (!tracksColumns.Contains("CoverArtUrl")) newCols.Add(("CoverArtUrl", "TEXT NULL"));
                    
                    // Phase 0.1: Musical Intelligence & ORBIT
                    if (!tracksColumns.Contains("MusicalKey")) newCols.Add(("MusicalKey", "TEXT NULL"));
                    if (!tracksColumns.Contains("BPM")) newCols.Add(("BPM", "REAL NULL"));
                    if (!tracksColumns.Contains("CuePointsJson")) newCols.Add(("CuePointsJson", "TEXT NULL"));
                    if (!tracksColumns.Contains("AudioFingerprint")) newCols.Add(("AudioFingerprint", "TEXT NULL"));
                    if (!tracksColumns.Contains("BitrateScore")) newCols.Add(("BitrateScore", "INTEGER NULL"));
                    if (!tracksColumns.Contains("AnalysisOffset")) newCols.Add(("AnalysisOffset", "REAL NULL"));
                    
                    foreach (var (col, def) in newCols)
                    {
                         _logger.LogWarning("Schema Patch: Adding missing column '{Col}' to Tracks", col);
                         await context.Database.ExecuteSqlRawAsync($"ALTER TABLE Tracks ADD COLUMN {col} {def}");
                    }
                }
            }
            
            await connection.CloseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to patch database schema");
        }

        _logger.LogInformation("[{Ms}ms] Database initialized and schema verified.", sw.ElapsedMilliseconds);
    }

    // ===== Track Methods =====

    public async Task<List<TrackEntity>> LoadTracksAsync()
    {
        using var context = new AppDbContext();
        return await context.Tracks.ToListAsync();
    }

    public async Task SaveTrackAsync(TrackEntity track)
    {
        const int maxRetries = 5;
        
        // Serialize all database writes to prevent SQLite locking
        await _writeSemaphore.WaitAsync();
        try
        {
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    using var context = new AppDbContext();

                    // Find existing entity by GlobalId (primary key)
                    var existingTrack = await context.Tracks
                        .FirstOrDefaultAsync(t => t.GlobalId == track.GlobalId);

                    if (existingTrack == null)
                    {
                        // Case 1: New track - INSERT
                        context.Tracks.Add(track);
                    }
                    else
                    {
                        // Case 2: Existing track - UPDATE
                        // Apply only the properties that may change during download lifecycle
                        existingTrack.State = track.State;
                        existingTrack.Filename = track.Filename;
                        existingTrack.ErrorMessage = track.ErrorMessage;
                        existingTrack.CoverArtUrl = track.CoverArtUrl;
                        existingTrack.Artist = track.Artist;
                        existingTrack.Title = track.Title;
                        existingTrack.Size = track.Size;

                        // Phase 0: Persist Spotify Metadata
                        existingTrack.SpotifyTrackId = track.SpotifyTrackId;
                        existingTrack.SpotifyAlbumId = track.SpotifyAlbumId;
                        existingTrack.SpotifyArtistId = track.SpotifyArtistId;
                        existingTrack.AlbumArtUrl = track.AlbumArtUrl;
                        existingTrack.ArtistImageUrl = track.ArtistImageUrl;
                        existingTrack.Genres = track.Genres;
                        existingTrack.Popularity = track.Popularity;
                        existingTrack.CanonicalDuration = track.CanonicalDuration;
                        existingTrack.ReleaseDate = track.ReleaseDate;
                    
                        // Phase 0.1: Musical Intelligence
                        existingTrack.MusicalKey = track.MusicalKey;
                        existingTrack.BPM = track.BPM;
                        existingTrack.AnalysisOffset = track.AnalysisOffset;
                        existingTrack.BitrateScore = track.BitrateScore;
                        existingTrack.AudioFingerprint = track.AudioFingerprint;
                        existingTrack.CuePointsJson = track.CuePointsJson;
                    
                        // Don't update AddedAt - preserve original
                        // context.Tracks.Update() is not needed - EF Core tracks changes automatically
                    }

                    await context.SaveChangesAsync();
                    return; // Success - exit method
                }
                catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqliteEx && sqliteEx.SqliteErrorCode == 5)
                {
                    // SQLite Error 5: Database is locked
                    if (attempt < maxRetries - 1)
                    {
                        _logger.LogWarning(
                            "SQLite database locked saving track {GlobalId}, attempt {Attempt}/{Max}. Retrying...", 
                            track.GlobalId, attempt + 1, maxRetries);
                        await Task.Delay(100 * (attempt + 1)); // Exponential backoff: 100ms, 200ms, 300ms, 400ms
                        continue; // Retry
                    }
                    else
                    {
                        _logger.LogError(ex, 
                            "Failed to save track {GlobalId} after {Max} attempts - database remains locked", 
                            track.GlobalId, maxRetries);
                        throw;
                    }
                }
                catch (DbUpdateConcurrencyException ex)
                {
                    if (attempt < maxRetries - 1)
                    {
                        // Retry after a brief delay
                        _logger.LogWarning("Concurrency conflict saving track {GlobalId}, attempt {Attempt}/{Max}. Retrying...", 
                            track.GlobalId, attempt + 1, maxRetries);
                        await Task.Delay(50 * (attempt + 1)); // Exponential backoff: 50ms, 100ms, 150ms, 200ms
                        continue;
                    }
                    else
                    {
                        // Final attempt failed
                        _logger.LogError(ex, "Failed to save track {GlobalId} after {Max} attempts due to concurrency conflicts", 
                            track.GlobalId, maxRetries);
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error saving track {GlobalId}", track.GlobalId);
                    throw;
                }
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }
    
    public async Task UpdateTrackFilePathAsync(string globalId, string filePath)
    {
        using var context = new AppDbContext();
        var track = await context.Tracks.FirstOrDefaultAsync(t => t.GlobalId == globalId);
        if (track != null)
        {
            track.Filename = filePath;
            await context.SaveChangesAsync();
        }
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

        // Check if track already exists in database to decide between Add (INSERT) or Update (UPDATE)
        var exists = await context.PlaylistTracks.AnyAsync(t => t.Id == track.Id);

        if (exists)
        {
            // Update existing track
            context.PlaylistTracks.Update(track);
        }
        else
        {
            // Add new track
            track.AddedAt = DateTime.UtcNow;
            context.PlaylistTracks.Add(track);
        }

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
                    SortOrder = track.SortOrder,
                    // Phase 0: Spotify Metadata
                    SpotifyTrackId = track.SpotifyTrackId,
                    SpotifyAlbumId = track.SpotifyAlbumId,
                    SpotifyArtistId = track.SpotifyArtistId,
                    AlbumArtUrl = track.AlbumArtUrl,
                    ArtistImageUrl = track.ArtistImageUrl,
                    Genres = track.Genres,
                    Popularity = track.Popularity,
                    CanonicalDuration = track.CanonicalDuration,
                    ReleaseDate = track.ReleaseDate
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
                        SortOrder = track.SortOrder,
                        // Phase 0: Spotify Metadata
                        SpotifyTrackId = track.SpotifyTrackId,
                        SpotifyAlbumId = track.SpotifyAlbumId,
                        SpotifyArtistId = track.SpotifyArtistId,
                        AlbumArtUrl = track.AlbumArtUrl,
                        ArtistImageUrl = track.ArtistImageUrl,
                        Genres = track.Genres,
                        Popularity = track.Popularity,
                        CanonicalDuration = track.CanonicalDuration,
                        ReleaseDate = track.ReleaseDate,
                        
                        // Phase 0.1: Musical Intelligence
                        MusicalKey = track.MusicalKey,
                        BPM = track.BPM,
                        CuePointsJson = track.CuePointsJson,
                        AudioFingerprint = track.AudioFingerprint,
                        BitrateScore = track.BitrateScore,
                        AnalysisOffset = track.AnalysisOffset
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

    // ===== Queue Persistence Methods (Phase 0) =====

    /// <summary>
    /// Saves the current playback queue to the database.
    /// Clears existing queue and saves the new state.
    /// </summary>
    public async Task SaveQueueAsync(List<(Guid trackId, int position, bool isCurrent)> queueItems)
    {
        using var context = new AppDbContext();
        
        // Clear existing queue
        var existingQueue = await context.QueueItems.ToListAsync();
        context.QueueItems.RemoveRange(existingQueue);
        
        // Add new queue items
        foreach (var (trackId, position, isCurrent) in queueItems)
        {
            context.QueueItems.Add(new QueueItemEntity
            {
                PlaylistTrackId = trackId,
                QueuePosition = position,
                IsCurrentTrack = isCurrent,
                AddedAt = DateTime.UtcNow
            });
        }
        
        await context.SaveChangesAsync();
        _logger.LogInformation("Saved queue with {Count} items", queueItems.Count);
    }

    /// <summary>
    /// Loads the saved playback queue from the database.
    /// Returns queue items with their associated track data.
    /// </summary>
    public async Task<List<(PlaylistTrack track, bool isCurrent)>> LoadQueueAsync()
    {
        using var context = new AppDbContext();
        
        var queueItems = await context.QueueItems
            .OrderBy(q => q.QueuePosition)
            .ToListAsync();
            
        var result = new List<(PlaylistTrack, bool)>();
        
        foreach (var queueItem in queueItems)
        {
            var trackEntity = await context.PlaylistTracks
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == queueItem.PlaylistTrackId);
                
            if (trackEntity != null)
            {
                var track = new PlaylistTrack
                {
                    Id = trackEntity.Id,
                    PlaylistId = trackEntity.PlaylistId,
                    Artist = trackEntity.Artist,
                    Title = trackEntity.Title,
                    Album = trackEntity.Album,
                    TrackUniqueHash = trackEntity.TrackUniqueHash,
                    Status = trackEntity.Status,
                    ResolvedFilePath = trackEntity.ResolvedFilePath,
                    TrackNumber = trackEntity.TrackNumber,
                    AddedAt = trackEntity.AddedAt,
                    SortOrder = trackEntity.SortOrder,
                    Rating = trackEntity.Rating,
                    IsLiked = trackEntity.IsLiked,
                    PlayCount = trackEntity.PlayCount,
                    LastPlayedAt = trackEntity.LastPlayedAt,
                    // Spotify metadata
                    SpotifyTrackId = trackEntity.SpotifyTrackId,
                    SpotifyAlbumId = trackEntity.SpotifyAlbumId,
                    SpotifyArtistId = trackEntity.SpotifyArtistId,
                    AlbumArtUrl = trackEntity.AlbumArtUrl,
                    ArtistImageUrl = trackEntity.ArtistImageUrl,
                    Genres = trackEntity.Genres,
                    Popularity = trackEntity.Popularity,
                    CanonicalDuration = trackEntity.CanonicalDuration,
                    ReleaseDate = trackEntity.ReleaseDate,
                    
                    // Phase 0.1: Musical Intelligence
                    MusicalKey = trackEntity.MusicalKey,
                    BPM = trackEntity.BPM,
                    CuePointsJson = trackEntity.CuePointsJson,
                    AudioFingerprint = trackEntity.AudioFingerprint,
                    BitrateScore = trackEntity.BitrateScore,
                    AnalysisOffset = trackEntity.AnalysisOffset
                };
                
                result.Add((track, queueItem.IsCurrentTrack));
            }
        }
        
        _logger.LogInformation("Loaded queue with {Count} items", result.Count);
        return result;
    }

    /// <summary>
    /// Clears the saved playback queue from the database.
    /// </summary>
    public async Task ClearQueueAsync()
    {
        using var context = new AppDbContext();
        var existingQueue = await context.QueueItems.ToListAsync();
        context.QueueItems.RemoveRange(existingQueue);
        await context.SaveChangesAsync();
        _logger.LogInformation("Cleared saved queue");
    }
}


