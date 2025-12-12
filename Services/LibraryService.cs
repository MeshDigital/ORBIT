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

    public event EventHandler<Guid>? ProjectDeleted;
    public event EventHandler<ProjectEventArgs>? ProjectUpdated;

    public LibraryService(ILogger<LibraryService> logger, DatabaseService databaseService)
    {
        _logger = logger;
        _databaseService = databaseService;

        _logger.LogDebug("LibraryService initialized with database persistence for playlists");
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save playlist job");
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

            // Emit the event so subscribers (like LibraryViewModel) can react.
            ProjectDeleted?.Invoke(this, playlistId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete playlist job");
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
            AddedAt = entity.AddedAt
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
            AddedAt = track.AddedAt
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
