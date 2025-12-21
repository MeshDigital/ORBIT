using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Utils;
using Avalonia.Threading;

namespace SLSKDONET.Services;

/// <summary>
/// Concrete implementation of ILibraryService.
/// Manages persistent library data (LibraryEntry, PlaylistJob, PlaylistTrack).
/// Now UI-agnostic and focused purely on data management.
/// </summary>
public class LibraryService : ILibraryService
{
    private readonly ILogger<LibraryService> _logger;
    private readonly DatabaseService _databaseService;
    private readonly AppConfig _appConfig;
    private readonly IEventBus _eventBus;
    private readonly LibraryCacheService _cache; // Session 1: Performance cache

    // Events now published via IEventBus (ProjectDeletedEvent, ProjectUpdatedEvent)

    /// <summary>
    /// Reactive observable collection of all playlists - single source of truth.
    /// Auto-syncs with SQLite database.
    /// </summary>
    public ObservableCollection<PlaylistJob> Playlists { get; } = new();

    public LibraryService(
        ILogger<LibraryService> logger, 
        DatabaseService databaseService, 
        AppConfig appConfig, 
        IEventBus eventBus,
        LibraryCacheService cache) // Session 1: Inject cache
    {
        _logger = logger;
        _databaseService = databaseService;
        _appConfig = appConfig;
        _eventBus = eventBus;
        _cache = cache;

        _logger.LogDebug("LibraryService initialized (Data Only) with caching enabled");
    }

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
            await _databaseService.LogActivityAsync(log).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log playlist activity");
        }
    }

    // ===== INDEX 1: LibraryEntry (Main Global Index - DB backed) =====

    public async Task<LibraryEntry?> FindLibraryEntryAsync(string uniqueHash)
    {
        var entity = await _databaseService.FindLibraryEntryAsync(uniqueHash).ConfigureAwait(false);
        return entity != null ? EntityToLibraryEntry(entity) : null;
    }

    public async Task<List<LibraryEntry>> LoadAllLibraryEntriesAsync()
    {
        var entities = await _databaseService.LoadAllLibraryEntriesAsync().ConfigureAwait(false);
        return entities.Select(EntityToLibraryEntry).ToList();
    }

    public async Task SaveOrUpdateLibraryEntryAsync(LibraryEntry entry)
    {
        try
        {
            var entity = LibraryEntryToEntity(entry);
            entity.LastUsedAt = DateTime.UtcNow;

            await _databaseService.SaveLibraryEntryAsync(entity).ConfigureAwait(false);
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
        try 
        {
            var entities = await _databaseService.LoadAllPlaylistJobsAsync().ConfigureAwait(false);
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
            var entities = await _databaseService.LoadAllPlaylistJobsAsync().ConfigureAwait(false);
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
            // Session 1: Try cache first
            var cached = _cache.GetProject(playlistId);
            if (cached != null)
            {
                _logger.LogDebug("Cache HIT for project {Id}", playlistId);
                return cached;
            }
            
            // Cache miss - load from database
            _logger.LogDebug("Cache MISS for project {Id}, loading from database", playlistId);
            var entity = await _databaseService.LoadPlaylistJobAsync(playlistId).ConfigureAwait(false);
            
            if (entity != null)
            {
                var job = EntityToPlaylistJob(entity);
                _cache.CacheProject(job); // Cache for next time
                return job;
            }
            
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load playlist job {Id}", playlistId);
            return null;
        }
    }

    public async Task<PlaylistJob?> FindPlaylistJobBySourceTypeAsync(string sourceType)
    {
        try
        {
            // Efficiency: Loading all jobs to filter in memory isn't ideal but acceptable for small number of playlists.
            // A dedicated DB query would be better long term.
            var entities = await _databaseService.LoadAllPlaylistJobsAsync().ConfigureAwait(false);
            var entity = entities.FirstOrDefault(e => e.SourceType == sourceType);
            return entity != null ? EntityToPlaylistJob(entity) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find playlist job by source type {Type}", sourceType);
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
                FailedCount = 0,

                AlbumArtUrl = job.AlbumArtUrl,
                SourceUrl = job.SourceUrl
            };

            await _databaseService.SavePlaylistJobAsync(entity).ConfigureAwait(false);
            
            // Session 1: Invalidate cache on save
            _cache.InvalidateProject(job.Id);
            _logger.LogInformation("Saved playlist job: {Title} ({Id}), cache invalidated", job.SourceTitle, job.Id);

            // Notify listeners (UI updates)
            // Legacy event removed: PlaylistAdded?.Invoke(this, job);
            _eventBus.Publish(new ProjectAddedEvent(job.Id));
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
            
            // 2. Invalidate Cache
            _cache.InvalidateProject(job.Id);

            // 3. Notify listeners
            // Legacy event removed: PlaylistAdded?.Invoke(this, job);
            _eventBus.Publish(new ProjectAddedEvent(job.Id));
            _logger.LogInformation("Saved playlist job with tracks and notified listeners: {Title}", job.SourceTitle);
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
            await _databaseService.SoftDeletePlaylistJobAsync(playlistId).ConfigureAwait(false);
            
            // Session 1: Invalidate cache on delete
            _cache.InvalidateProject(playlistId);
            _logger.LogInformation("Deleted playlist job: {Id}, cache invalidated", playlistId);

            // REACTIVE: Auto-remove from observable collection
            Dispatcher.UIThread.Post(() =>
            {
                var jobToRemove = Playlists.FirstOrDefault(p => p.Id == playlistId);
                if (jobToRemove != null)
                {
                    Playlists.Remove(jobToRemove);
                    _logger.LogInformation("Removed playlist '{Title}' from reactive collection", jobToRemove.SourceTitle);
                }
            });

            // Emit the event so subscribers (like LibraryViewModel) can react.
            _eventBus.Publish(new ProjectDeletedEvent(playlistId));
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
        await SavePlaylistJobWithTracksAsync(job).ConfigureAwait(false);
        
        return job;
    }

    public async Task SaveTrackOrderAsync(Guid playlistId, IEnumerable<PlaylistTrack> tracks)
    {
        try
        {
            // Convert to models and persist batch
            var entities = tracks.Select(PlaylistTrackToEntity).ToList();
            await _databaseService.SavePlaylistTracksAsync(entities).ConfigureAwait(false);
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
            var entities = await _databaseService.LoadPlaylistTracksAsync(playlistId).ConfigureAwait(false);
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
            var entities = await _databaseService.GetAllPlaylistTracksAsync().ConfigureAwait(false);
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
            await _databaseService.SavePlaylistTrackAsync(entity).ConfigureAwait(false);
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
         await _databaseService.DeletePlaylistTracksAsync(jobId).ConfigureAwait(false);
    }
    
    public async Task DeletePlaylistTrackAsync(Guid playlistTrackId)
    {
        await _databaseService.DeleteSinglePlaylistTrackAsync(playlistTrackId).ConfigureAwait(false);
    }

    public async Task UpdatePlaylistTrackAsync(PlaylistTrack track)
    {
        try
        {
            var entity = PlaylistTrackToEntity(track);
            await _databaseService.SavePlaylistTrackAsync(entity).ConfigureAwait(false);
            _logger.LogDebug("Updated playlist track status: {Hash} = {Status}", track.TrackUniqueHash, track.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update playlist track");
            throw;
        }
    }
    
    public async Task SavePlaylistTracksAsync(List<PlaylistTrack> tracks)
    {
        try
        {
            var entities = tracks.Select(PlaylistTrackToEntity).ToList();
            await _databaseService.SavePlaylistTracksAsync(entities).ConfigureAwait(false);
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
        var entities = await _databaseService.LoadAllLibraryEntriesAsync().ConfigureAwait(false);
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

            await SaveOrUpdateLibraryEntryAsync(entry).ConfigureAwait(false);
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
            FailedCount = entity.FailedCount,

            AlbumArtUrl = entity.AlbumArtUrl,
            SourceUrl = entity.SourceUrl
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
            Rating = entity.Rating,
            IsLiked = entity.IsLiked,
            PlayCount = entity.PlayCount,
            LastPlayedAt = entity.LastPlayedAt,
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
            Rating = track.Rating,
            IsLiked = track.IsLiked,
            PlayCount = track.PlayCount,
            LastPlayedAt = track.LastPlayedAt,
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




    /// <summary>
    /// Updates the file path for a library entry and persists the change.
    /// </summary>
    public async Task UpdateLibraryEntryPathAsync(string uniqueHash, string newPath)
    {
        try
        {
            var entity = await _databaseService.FindLibraryEntryAsync(uniqueHash);
            if (entity != null)
            {
                entity.FilePath = newPath;
                await _databaseService.SaveLibraryEntryAsync(entity);
                _logger.LogInformation("Updated file path for {Hash}: {NewPath}", uniqueHash, newPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update library entry path");
            throw;
        }
    }
}
