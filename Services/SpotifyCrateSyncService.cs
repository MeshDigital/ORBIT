using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services.ImportProviders;
using SLSKDONET.Configuration;

namespace SLSKDONET.Services;

/// <summary>
/// Background daemon that periodically syncs a Spotify playlist and auto-queues missing tracks.
/// Path C: The Spotify Crate Sync Engine
/// </summary>
public class SpotifyCrateSyncService
{
    private readonly ILogger<SpotifyCrateSyncService> _logger;
    private readonly SpotifyImportProvider _spotifyImportProvider;
    private readonly ILibraryService _libraryService;
    private readonly DownloadManager _downloadManager;
    private readonly Services.Repositories.ITrackRepository _trackRepository;

    public SpotifyCrateSyncService(
        ILogger<SpotifyCrateSyncService> logger,
        SpotifyImportProvider spotifyImportProvider,
        ILibraryService libraryService,
        DownloadManager downloadManager,
        Services.Repositories.ITrackRepository trackRepository)
    {
        _logger = logger;
        _spotifyImportProvider = spotifyImportProvider;
        _libraryService = libraryService;
        _downloadManager = downloadManager;
        _trackRepository = trackRepository;
    }

    /// <summary>
    /// Executes a single sync pass for the given job.
    /// </summary>
    public async Task ExecuteSyncAsync(SpotifySyncJob job, CancellationToken ct)
    {
        if (!job.IsActive)
        {
            _logger.LogInformation("Sync job {Id} is inactive. Skipping.", job.Id);
            return;
        }

        _logger.LogInformation("Starting Crate Sync for '{PlaylistName}' ({Url})", job.PlaylistName, job.PlaylistUrlOrId);

        // 1. FETCH
        // We leverage SpotifyImportProvider because it gracefully handles standard Spotify URLs
        // and automatically falls back to Scraper mode if the API token is expired/missing.
        var importResult = await _spotifyImportProvider.ImportAsync(job.PlaylistUrlOrId);
        
        if (!importResult.Success || importResult.Tracks == null)
        {
            _logger.LogWarning("Spotify sync failed for {Playlist}: {Error}", job.PlaylistName, importResult.ErrorMessage);
            return;
        }

        var incomingTracks = importResult.Tracks.ToList();
        var missingTracks = new List<SearchQuery>();

        // 2. THE DE-DUPE BARRIER
        foreach (var spotifyTrack in incomingTracks)
        {
            ct.ThrowIfCancellationRequested();

            var safeArtist = spotifyTrack.Artist ?? string.Empty;
            var safeTitle = spotifyTrack.Title ?? string.Empty;

            bool existsLocally = await _trackRepository.TrackExistsAsync(safeArtist, safeTitle, ct);

            if (existsLocally)
            {
                _logger.LogTrace("De-dupe matched local library: {Artist} - {Title}", safeArtist, safeTitle);
                continue;
            }

            missingTracks.Add(spotifyTrack);
        }

        _logger.LogInformation("Sync Engine separated {NewCount} missing tracks from {TotalCount} total tracks.", 
            missingTracks.Count, incomingTracks.Count);

        // 3. THE HAND-OFF TO THE ENGINE
        if (missingTracks.Any())
        {
            // We map the missing SearchQueries into standard Tracks and wrap them in a PlaylistJob.
            // By keeping the Job ID consistent with the SyncJob ID, all missing tracks discovered 
            // over time for this playlist will neatly group under the same project in the Library UI!
            var syncProject = new PlaylistJob
            {
                Id = job.Id, 
                SourceTitle = $"[SYNC] {job.PlaylistName}",
                OriginalTracks = new System.Collections.ObjectModel.ObservableCollection<Track>(
                    missingTracks.Select(t => new Track 
                    {
                        Artist = t.Artist ?? string.Empty,
                        Title = t.Title ?? string.Empty,
                        Album = t.Album ?? string.Empty,
                        SpotifyTrackId = t.SpotifyTrackId,
                        AlbumArtUrl = t.AlbumArtUrl,
                        Genres = t.Genres,
                        CanonicalDuration = 0, // Set to 0, will be fetched via normal track enrichment later
                        Popularity = t.Popularity
                    })
                )
            };

            await _downloadManager.QueueProject(syncProject);
            _logger.LogInformation("Handed off {Count} tracks to DownloadManager for background orchestration.", missingTracks.Count);
        }

        // 4. Update memory state
        job.LastSyncedAt = DateTime.UtcNow;
        _logger.LogInformation("Sync pass complete for '{PlaylistName}'.", job.PlaylistName);
    }
}
