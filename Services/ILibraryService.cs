using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Interface for library persistence and management.
/// Manages three distinct indexes: LibraryEntry (main), PlaylistJob (header), PlaylistTrack (relational).
/// </summary>
public interface ILibraryService
{
    /// <summary>
    /// Fired when a playlist job is successfully soft-deleted.
    /// </summary>
    event EventHandler<Guid>? ProjectDeleted;

    /// <summary>
    /// Fired whenever a playlist job's progress or metadata changes.
    /// </summary>
    event EventHandler<ProjectEventArgs>? ProjectUpdated;

    // ===== INDEX 1: LibraryEntry (Main Global Index) =====
    
    /// <summary>
    /// Retrieves a library entry by its UniqueHash (asynchronous).
    /// </summary>
    Task<LibraryEntry?> FindLibraryEntryAsync(string uniqueHash);

    /// <summary>
    /// Loads all library entries (main global index).
    /// </summary>
    Task<List<LibraryEntry>> LoadAllLibraryEntriesAsync();

    /// <summary>
    /// Atomically saves (inserts or updates) a library entry based on its UniqueHash.
    /// This replaces separate Add and Update methods to prevent race conditions.
    /// </summary>
    Task SaveOrUpdateLibraryEntryAsync(LibraryEntry entry);

    // ===== INDEX 2: PlaylistJob (Playlist Headers) =====

    /// <summary>
    /// Loads all playlist jobs (playlist history).
    /// Used to populate the Playlist List view in the UI.
    /// </summary>
    Task<List<PlaylistJob>> LoadAllPlaylistJobsAsync();

    /// <summary>
    /// Loads a specific playlist job by ID (asynchronous).
    /// Includes related PlaylistTrack entries.
    /// </summary>
    Task<PlaylistJob?> FindPlaylistJobAsync(Guid playlistId);

    /// <summary>
    /// Saves a new or existing playlist job.
    /// Called when importing a new source.
    /// </summary>
    Task SavePlaylistJobAsync(PlaylistJob job);

    /// <summary>
    /// Deletes a playlist job and its related PlaylistTrack entries.
    /// </summary>
    Task DeletePlaylistJobAsync(Guid playlistId);

    // ===== INDEX 3: PlaylistTrack (Relational Index) =====

    /// <summary>
    /// Loads all tracks for a specific playlist job.
    /// Used for the Playlist Track Detail view.
    /// </summary>
    Task<List<PlaylistTrack>> LoadPlaylistTracksAsync(Guid playlistId);

    /// <summary>
    /// Saves a single playlist track entry.
    /// Called during import to create the relational index.
    /// </summary>
    Task SavePlaylistTrackAsync(PlaylistTrack track);

    /// <summary>
    /// Updates a playlist track (e.g., status or resolved path).
    /// Called when a download completes.
    /// </summary>
    Task UpdatePlaylistTrackAsync(PlaylistTrack track);

    /// <summary>
    /// Bulk save multiple playlist tracks.
    /// Called after initial import to create all relational entries at once.
    /// </summary>
    Task SavePlaylistTracksAsync(List<PlaylistTrack> tracks);

    // ===== Legacy / Compatibility =====

    /// <summary>
    /// Async version of LoadDownloadedTracks.
    /// </summary>
    Task<List<LibraryEntry>> LoadDownloadedTracksAsync();

    /// <summary>
    /// Adds a track to the library with optional source playlist reference (legacy).
    /// </summary>
    Task AddTrackAsync(Track track, string actualFilePath, Guid sourcePlaylistId);
}
