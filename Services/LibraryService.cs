using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Concrete implementation of ILibraryService.
/// Manages three persistent indexes:
/// 1. LibraryEntry (main global index of unique files)
/// 2. PlaylistJob (playlist headers) - Database backed
/// 3. PlaylistTrack (relational index linking playlists to tracks) - Database backed
/// </summary>
public class LibraryService : ILibraryService
{
    private readonly ILogger<LibraryService> _logger;
    private readonly DatabaseService _databaseService;
    // private bool _isInitialized; // Unused

    public event EventHandler<Guid>? ProjectDeleted;
    
    // Unused event required by interface - marking to suppress warning
    #pragma warning disable CS0067
    public event EventHandler<ProjectEventArgs>? ProjectUpdated;
    #pragma warning restore CS0067

    /// <summary>
    /// Reactive observable collection of all playlists - single source of truth.
    /// Auto-syncs with SQLite database.
    /// </summary>
    public ObservableCollection<PlaylistJob> Playlists { get; } = new();

    public LibraryService(ILogger<LibraryService> logger, DatabaseService databaseService)
    {
        _logger = logger;
        _databaseService = databaseService;

        _logger.LogDebug("LibraryService initialized with database persistence for playlists");
    
        // Playlists will be loaded on-demand when accessed
    }

    public Task RefreshPlaylistsAsync() => InitializePlaylistsAsync();

    public async Task LogPlaylistActivityAsync(Guid playlistId, string action, string details)
    {
        try
        {
            var log = new PlaylistActivityLogEntity
            {
                Id = Guid.NewGuid(),
                PlaylistId = playlistId,
                Action = action,
                Details = details,
                Timestamp = DateTime.UtcNow
            };
            await _databaseService.LogActivityAsync(log);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log playlist activity");
        }
    }

    private async Task InitializePlaylistsAsync()
    {
        try
        {
            _logger.LogInformation("Loading playlists from database...");
            var jobs = await _databaseService.LoadAllPlaylistJobsAsync();
            
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                Playlists.Clear();
                foreach (var job in jobs)
                {
                    Playlists.Add(EntityToPlaylistJob(job));
                }
            });
            
            // _isInitialized = true;
            _logger.LogInformation("Loaded {Count} playlists from database", jobs.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize playlists from database");
        }
    }

    // ===== INDEX 1: LibraryEntry (Main Global Index - DB backed) =====

    public async Task<LibraryEntry?> FindLibraryEntryAsync(string uniqueHash)
    {
        var entity = await _databaseService.FindLibraryEntryAsync(uniqueHash);
        return entity != null ? EntityToLibraryEntry(entity) : null;
    }

    public async Task<List<LibraryEntry>> LoadAllLibraryEntriesAsync()
    {
        var entities = await _databaseService.LoadAllLibraryEntriesAsync();
        return entities.Select(EntityToLibraryEntry).ToList();
    }

    public async Task SaveOrUpdateLibraryEntryAsync(LibraryEntry entry)
    {
        try
        {
            var entity = LibraryEntryToEntity(entry);
            entity.LastUsedAt = DateTime.UtcNow;

            // This call will perform an atomic upsert (INSERT or UPDATE)
            // assuming the underlying DatabaseService uses EF Core's Update() or equivalent.
            // If the PK is not found, it will be an INSERT; otherwise, an UPDATE.
            await _databaseService.SaveLibraryEntryAsync(entity);
            _logger.LogDebug("Upserted library entry: {Hash}", entry.UniqueHash);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save or update library entry");
            throw;
        }
    }

    // ===== INDEX 2: PlaylistJob (Playlist Headers - Database Backed) =====

    public async Task<List<PlaylistJob>> GetHistoricalJobsAsync()
    {
        // "History" means completed, failed, or cancelled jobs.
        // For now, let's assume it's all jobs sorted by date, or we can filter.
        // The user request implies "Import Job History".
        // Let's return all jobs, and the VM can filter active vs history?
        // Or better: Let's fetch all jobs that have "CompletedAt" or are "Deleted" (soft) maybe?
        // Actually, LoadAllPlaylistJobsAsync() filters out IsDeleted.
        // Let's add a method to get even deleted ones? Or just non-active?
        
        // Simpler for Phase 1: Return ALL jobs, filtering is done in VM or use existing method.
        // User asked for "GetHistoricalJobsAsync", specifically for Import History.
        
        try 
        {
            var entities = await _databaseService.LoadAllPlaylistJobsAsync();
            // Optional: Filter logic if we defined "Historical" strictly.
            // For now, return all so the user can see everything.
            return entities.Select(EntityToPlaylistJob).OrderByDescending(j => j.CreatedAt).ToList();
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Failed to load historical jobs");
             return new List<PlaylistJob>();
        }
    }

    public async Task<List<PlaylistJob>> LoadAllPlaylistJobsAsync()
    {
        try
        {
            var entities = await _databaseService.LoadAllPlaylistJobsAsync();
            return entities.Select(EntityToPlaylistJob).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load playlist jobs from database");
            return new List<PlaylistJob>();
        }
    }

    public async Task<PlaylistJob?> FindPlaylistJobAsync(Guid playlistId)
    {
        try
        {
            var entity = await _databaseService.LoadPlaylistJobAsync(playlistId);
            return entity != null ? EntityToPlaylistJob(entity) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load playlist job {Id}", playlistId);
            return null;
        }
    }

    public async Task SavePlaylistJobAsync(PlaylistJob job)
    {
        try
        {
            var entity = new PlaylistJobEntity
            {
                Id = job.Id,
                SourceTitle = job.SourceTitle,
                SourceType = job.SourceType,
                DestinationFolder = job.DestinationFolder,
                CreatedAt = job.CreatedAt,
                TotalTracks = job.OriginalTracks.Count,
                SuccessfulCount = 0,
                FailedCount = 0
            };

            await _databaseService.SavePlaylistJobAsync(entity);
            _logger.LogInformation("Saved playlist job: {Title} ({Id})", job.SourceTitle, job.Id);

            // REACTIVE: Auto-add to observable collection if not already there
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (!Playlists.Any(p => p.Id == job.Id))
                {
                    Playlists.Add(job);
                    _logger.LogInformation("Added playlist '{Title}' to reactive collection", job.SourceTitle);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save playlist job");
            throw;
        }
    }

    public async Task SavePlaylistJobWithTracksAsync(PlaylistJob job)
    {
        try
        {
            // 1. Save Header + Tracks to DB atomically
            await _databaseService.SavePlaylistJobWithTracksAsync(job).ConfigureAwait(false);
            
            // 2. Update In-Memory Reactive Collection (Fire & Forget to avoid deadlock)
            _ = System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                try 
                {
                    if (!Playlists.Any(p => p.Id == job.Id))
                    {
                        Playlists.Add(job);
                        _logger.LogInformation("Added playlist '{Title}' (with tracks) to reactive collection", job.SourceTitle);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update reactive collection for {Title}", job.SourceTitle);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save playlist job with tracks {Title}", job.SourceTitle);
            throw;
        }
    }

    public async Task DeletePlaylistJobAsync(Guid playlistId)
    {
        try
        {
            // With soft delete, we just set the flag
            await _databaseService.SoftDeletePlaylistJobAsync(playlistId);
            _logger.LogInformation("Deleted playlist job: {Id}", playlistId);

            // REACTIVE: Auto-remove from observable collection
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                var jobToRemove = Playlists.FirstOrDefault(p => p.Id == playlistId);
                if (jobToRemove != null)
                {
                    Playlists.Remove(jobToRemove);
                    _logger.LogInformation("Removed playlist '{Title}' from reactive collection", jobToRemove.SourceTitle);
                }
            });

            // Emit the event so subscribers (like LibraryViewModel) can react.
            ProjectDeleted?.Invoke(this, playlistId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete playlist job");
            throw;
        }
    }

    public async Task<PlaylistJob> CreateEmptyPlaylistAsync(string title)
    {
        var job = new PlaylistJob
        {
            Id = Guid.NewGuid(),
            SourceTitle = title,
            SourceType = "User",
            CreatedAt = DateTime.UtcNow,
            PlaylistTracks = new List<PlaylistTrack>(),
            TotalTracks = 0
        };

        // Persist and update reactive collection
        await SavePlaylistJobWithTracksAsync(job);
        
        return job;
    }

    public async Task SaveTrackOrderAsync(Guid playlistId, IEnumerable<PlaylistTrack> tracks)
    {
        try
        {
            // Convert to models and persist batch
            var entities = tracks.Select(PlaylistTrackToEntity).ToList();
            await _databaseService.SavePlaylistTracksAsync(entities);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist track order for playlist {Id}", playlistId);
            throw;
        }
    }

    // ===== INDEX 3: PlaylistTrack (Relational Index - Database Backed) =====

    public async Task<List<PlaylistTrack>> LoadPlaylistTracksAsync(Guid playlistId)
    {
        try
        {
            var entities = await _databaseService.LoadPlaylistTracksAsync(playlistId);
            return entities.Select(EntityToPlaylistTrack).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load playlist tracks for {PlaylistId}", playlistId);
            return new List<PlaylistTrack>();
        }
    }

    public async Task<List<PlaylistTrack>> GetAllPlaylistTracksAsync()
    {
        try
        {
            var entities = await _databaseService.GetAllPlaylistTracksAsync();
            return entities.Select(EntityToPlaylistTrack).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load all playlist tracks");
            return new List<PlaylistTrack>();
        }
    }

    public async Task SavePlaylistTrackAsync(PlaylistTrack track)
    {
        try
        {
            var entity = PlaylistTrackToEntity(track);
            await _databaseService.SavePlaylistTrackAsync(entity);
            _logger.LogDebug("Saved playlist track: {PlaylistId}/{Hash}", track.PlaylistId, track.TrackUniqueHash);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save playlist track");
            throw;
        }
    }

    public async Task DeletePlaylistTracksAsync(Guid jobId)
    {
         await _databaseService.DeletePlaylistTracksAsync(jobId);
    }
    
    public async Task DeletePlaylistTrackAsync(Guid playlistTrackId)
    {
        await _databaseService.DeleteSinglePlaylistTrackAsync(playlistTrackId);
    }

    public async Task UpdatePlaylistTrackAsync(PlaylistTrack track)
    {
        try
        {
            var entity = PlaylistTrackToEntity(track);
            await _databaseService.SavePlaylistTrackAsync(entity);
            _logger.LogDebug("Updated playlist track status: {Hash} = {Status}", track.TrackUniqueHash, track.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update playlist track");
            throw;
        }
    }
    
    public async Task UpdateTrackFilePathAsync(string globalId, string filePath)
    {
        try
        {
            await _databaseService.UpdateTrackFilePathAsync(globalId, filePath);
            _logger.LogDebug("Updated file path for track {GlobalId}: {Path}", globalId, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update track file path");
            throw;
        }
    }

    public async Task SavePlaylistTracksAsync(List<PlaylistTrack> tracks)
    {
        try
        {
            var entities = tracks.Select(PlaylistTrackToEntity).ToList();
            await _databaseService.SavePlaylistTracksAsync(entities);
            _logger.LogInformation("Saved {Count} playlist tracks", tracks.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save playlist tracks");
            throw;
        }
    }

    // ===== Legacy / Compatibility Methods =====

    public async Task<List<LibraryEntry>> LoadDownloadedTracksAsync()
    {
        // This now directly loads from the database. The old JSON method is gone.
        var entities = await _databaseService.LoadAllLibraryEntriesAsync();
        return entities.Select(EntityToLibraryEntry).ToList();
    }

    public async Task AddTrackAsync(Track track, string actualFilePath, Guid sourcePlaylistId)
    {
        try
        {
            var entry = new LibraryEntry
            {
                UniqueHash = track.UniqueHash,
                Artist = track.Artist ?? "Unknown",
                Title = track.Title ?? "Unknown",
                Album = track.Album ?? "Unknown",
                FilePath = actualFilePath,
                Bitrate = track.Bitrate,
                DurationSeconds = track.Length,
                Format = track.Format ?? "Unknown"
            };

            await SaveOrUpdateLibraryEntryAsync(entry);
            _logger.LogDebug("Saved/updated track in library: {Hash}", entry.UniqueHash);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add track");
            throw;
        }
    }

    // ===== Helper Conversion Methods =====

    private PlaylistJob EntityToPlaylistJob(PlaylistJobEntity entity)
    {
        var playlistTracks = entity.Tracks?.Select(EntityToPlaylistTrack).ToList() ?? new List<PlaylistTrack>();

        var originalTracks = new ObservableCollection<Track>(playlistTracks.Select(pt => new Track {
            Artist = pt.Artist,
            Title = pt.Title,
            Album = pt.Album,
        }));

        var job = new PlaylistJob
        {
            Id = entity.Id,
            SourceTitle = entity.SourceTitle,
            SourceType = entity.SourceType,
            DestinationFolder = entity.DestinationFolder,
            CreatedAt = entity.CreatedAt,
            OriginalTracks = originalTracks,
            PlaylistTracks = playlistTracks,
            SuccessfulCount = entity.SuccessfulCount,
            FailedCount = entity.FailedCount
        };

        job.MissingCount = entity.TotalTracks - entity.SuccessfulCount - entity.FailedCount;
        job.RefreshStatusCounts();

        return job;
    }

    private PlaylistTrack EntityToPlaylistTrack(PlaylistTrackEntity entity)
    {
        return new PlaylistTrack
        {
            Id = entity.Id,
            PlaylistId = entity.PlaylistId,
            Artist = entity.Artist,
            Title = entity.Title,
            Album = entity.Album,
            TrackUniqueHash = entity.TrackUniqueHash,
            Status = entity.Status,
            ResolvedFilePath = entity.ResolvedFilePath,
            TrackNumber = entity.TrackNumber,
            AddedAt = entity.AddedAt,
            SortOrder = entity.SortOrder
        };
    }

    private PlaylistTrackEntity PlaylistTrackToEntity(PlaylistTrack track)
    {
        return new PlaylistTrackEntity
        {
            Id = track.Id,
            PlaylistId = track.PlaylistId,
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
    }

    // ===== Private Helper Methods (JSON - LibraryEntry only) =====
    
    private LibraryEntry EntityToLibraryEntry(LibraryEntryEntity entity)
    {
        return new LibraryEntry
        {
            UniqueHash = entity.UniqueHash,
            Artist = entity.Artist,
            Title = entity.Title,
            Album = entity.Album,
            FilePath = entity.FilePath,
            Bitrate = entity.Bitrate,
            DurationSeconds = entity.DurationSeconds,
            Format = entity.Format,
            AddedAt = entity.AddedAt
        };
    }

    private LibraryEntryEntity LibraryEntryToEntity(LibraryEntry entry)
    {
        // This is a simplified mapping. In a real scenario, you might use a library like AutoMapper.
        var entity = new LibraryEntryEntity();
        entity.UniqueHash = entry.UniqueHash;
        entity.Artist = entry.Artist;
        entity.Title = entry.Title;
        entity.Album = entry.Album;
        entity.FilePath = entry.FilePath;
        entity.Bitrate = entry.Bitrate;
        entity.DurationSeconds = entry.DurationSeconds;
        entity.Format = entry.Format;
        // Ensure AddedAt is only set on creation, not on update.
        // The DatabaseService logic should handle this. If it doesn't, we can do it here:
        if (entry.AddedAt == default)
        {
            entity.AddedAt = DateTime.UtcNow;
        }
        return entity;
    }
}
