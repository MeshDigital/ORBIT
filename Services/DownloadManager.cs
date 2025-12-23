using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Serilog.Context;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services.InputParsers;
using SLSKDONET.Utils;
using SLSKDONET.Services.Models;
using Microsoft.EntityFrameworkCore;


namespace SLSKDONET.Services;

/// <summary>
/// Orchestrates the download process for projects and individual tracks.
/// "The Conductor" - manages the state machine and queue.
/// Delegates search to DownloadDiscoveryService and enrichment to MetadataEnrichmentOrchestrator.
/// </summary>
public class DownloadManager : INotifyPropertyChanged, IDisposable
{
    private readonly ILogger<DownloadManager> _logger;
    private readonly AppConfig _config;
    private readonly SoulseekAdapter _soulseek;
    private readonly FileNameFormatter _fileNameFormatter;
    // Removed ITaggerService dependency (moved to Enricher)
    private readonly DatabaseService _databaseService;
    // Removed ISpotifyMetadataService dependency (moved to Enricher)
    private readonly ILibraryService _libraryService;
    private readonly IEventBus _eventBus;
    
    // NEW Services
    private readonly DownloadDiscoveryService _discoveryService;
    private readonly MetadataEnrichmentOrchestrator _enrichmentOrchestrator;
    private readonly PathProviderService _pathProvider;

    // Phase 2.5: Concurrency control with SemaphoreSlim throttling
    private readonly CancellationTokenSource _globalCts = new();
    private readonly SemaphoreSlim _downloadSemaphore = new(4, 4); // Max 4 concurrent downloads
    private Task? _processingTask;

    // Global State managed via Events
    private readonly List<DownloadContext> _downloads = new();
    private readonly object _collectionLock = new object();
    
    // Expose read-only copy for internal checks
    public IReadOnlyList<DownloadContext> ActiveDownloads 
    {
        get { lock(_collectionLock) { return _downloads.ToList(); } }
    }
    
    // Expose download directory from config
    public string? DownloadDirectory => _config.DownloadDirectory;

    public DownloadManager(
        ILogger<DownloadManager> logger,
        AppConfig config,
        SoulseekAdapter soulseek,
        FileNameFormatter fileNameFormatter,
        DatabaseService databaseService,
        ILibraryService libraryService,
        IEventBus eventBus,
        DownloadDiscoveryService discoveryService,
        MetadataEnrichmentOrchestrator enrichmentOrchestrator,
        PathProviderService pathProvider)
    {
        _logger = logger;
        _config = config;
        _soulseek = soulseek;
        _fileNameFormatter = fileNameFormatter;
        _databaseService = databaseService;
        _libraryService = libraryService;
        _eventBus = eventBus;
        _discoveryService = discoveryService;
        _enrichmentOrchestrator = enrichmentOrchestrator;
        _pathProvider = pathProvider;

        // Initialize from config, but allow runtime changes
        MaxActiveDownloads = _config.MaxConcurrentDownloads > 0 ? _config.MaxConcurrentDownloads : 3;

        // Phase 8: Automation Subscriptions
        _eventBus.GetEvent<AutoDownloadTrackEvent>().Subscribe(OnAutoDownloadTrack);
        _eventBus.GetEvent<AutoDownloadUpgradeEvent>().Subscribe(OnAutoDownloadUpgrade);
        _eventBus.GetEvent<UpgradeAvailableEvent>().Subscribe(OnUpgradeAvailable);
        // Phase 6: Library Interactions
        _eventBus.GetEvent<DownloadAlbumRequestEvent>().Subscribe(OnDownloadAlbumRequest);
    }

    /// <summary>
    /// Handles requests to download an entire album (Project or AlbumNode).
    /// </summary>
    private void OnDownloadAlbumRequest(DownloadAlbumRequestEvent e)
    {
        try
        {
            if (e.Album is PlaylistJob job)
            {
                _logger.LogInformation("📢 Processing DownloadAlbumRequest for Project: {Title}", job.SourceTitle);
                
                // Ensure tracks are loaded
                 _ = Task.Run(async () => 
                 {
                     _logger.LogInformation("🔍 Loading tracks for project {Id}...", job.Id);
                     var tracks = await _libraryService.LoadPlaylistTracksAsync(job.Id);
                     
                     if (tracks.Any())
                     {
                         _logger.LogInformation("✅ Found {Count} tracks, queuing...", tracks.Count);
                         QueueTracks(tracks);
                         _logger.LogInformation("🚀 Queued {Count} tracks for project {Title}", tracks.Count, job.SourceTitle);
                     }
                     else
                     {
                         _logger.LogWarning("⚠️ No tracks found for project {Title} (ID: {Id}) - Database might be empty or tracks missing", job.SourceTitle, job.Id);
                     }
                 });
            }
            else if (e.Album is ViewModels.Library.AlbumNode node)
            {
                _logger.LogInformation("Processing DownloadAlbumRequest for AlbumNode: {Title}", node.AlbumTitle);
                var tracks = node.Tracks.Select(vm => vm.Model).ToList();
                if (tracks.Any())
                {
                    QueueTracks(tracks);
                    _logger.LogInformation("Queued {Count} tracks from AlbumNode {Title}", tracks.Count, node.AlbumTitle);
                }
            }
            else
            {
                _logger.LogWarning("Unknown payload type for DownloadAlbumRequestEvent: {Type}", e.Album?.GetType().Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle DownloadAlbumRequestEvent");
        }
    }

    /// <summary>
    /// Gets the number of actively downloading or queued tracks for a specific project.
    /// Used for real-time UI updates in the library sidebar.
    /// </summary>
    public int GetActiveDownloadsCountForProject(Guid projectId)
    {
        lock (_collectionLock)
        {
            return _downloads.Count(d => d.Model.PlaylistId == projectId && d.IsActive);
        }
    }

    /// <summary>
    /// Gets the name of the track currently being downloaded for a project.
    /// </summary>
    public string? GetCurrentlyDownloadingTrackName(Guid projectId)
    {
        lock (_collectionLock)
        {
            var active = _downloads.FirstOrDefault(d => 
                d.Model.PlaylistId == projectId && 
                d.State == PlaylistTrackState.Downloading);
            
            return active != null ? $"{active.Model.Artist} - {active.Model.Title}" : null;
        }
    }

    /// <summary>
    /// Checks if a track is already in the library or download queue.
    /// </summary>
    public bool IsTrackAlreadyQueued(string? spotifyTrackId, string artist, string title)
    {
        lock (_collectionLock)
        {
            if (!string.IsNullOrEmpty(spotifyTrackId))
            {
                if (_downloads.Any(d => d.Model.SpotifyTrackId == spotifyTrackId))
                    return true;
            }

            return _downloads.Any(d => 
                string.Equals(d.Model.Artist, artist, StringComparison.OrdinalIgnoreCase) && 
                string.Equals(d.Model.Title, title, StringComparison.OrdinalIgnoreCase));
        }
    }

    public int MaxActiveDownloads { get; set; }
    
    public async Task InitAsync()
    {
        try
        {
            await _databaseService.InitAsync();
            var tracks = await _databaseService.LoadTracksAsync();
            
            lock (_collectionLock)
            {
                foreach (var t in tracks)
                {
                    // Map Entity -> Model
                    var model = new PlaylistTrack 
                    { 
                        Artist = t.Artist, 
                        Title = t.Title, 
                        TrackUniqueHash = t.GlobalId,
                        Status = t.State == "Completed" ? TrackStatus.Downloaded : TrackStatus.Missing,
                        ResolvedFilePath = t.Filename,
                        SpotifyTrackId = t.SpotifyTrackId,
                        AlbumArtUrl = t.AlbumArtUrl
                    };
                    
                    var ctx = new DownloadContext(model);
                    ctx.State = Enum.TryParse<PlaylistTrackState>(t.State, out var s) ? s : PlaylistTrackState.Pending;
                    ctx.ErrorMessage = t.ErrorMessage;
                    
                    // Reset transient states
                    if (ctx.State == PlaylistTrackState.Downloading || ctx.State == PlaylistTrackState.Searching)
                        ctx.State = PlaylistTrackState.Pending;

                    _downloads.Add(ctx);
                    
                    // Publish event with initial state
                    _eventBus.Publish(new TrackAddedEvent(model, ctx.State));
                }
            }
            _logger.LogInformation("Hydrated {Count} tracks from database.", tracks.Count);
            
            // Phase 2.5: Crash Recovery - Detect orphaned downloads and resume with .part files
            await HydrateFromCrashAsync();
            
            // Phase 2.5: Zombie Cleanup - Delete orphaned .part files older than 24 hours
            // Pro-tip from user: Use case-insensitive HashSet for Windows paths
            var activePartPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_collectionLock)
            {
                foreach (var ctx in _downloads.Where(t => t.State == PlaylistTrackState.Pending || t.State == PlaylistTrackState.Downloading))
                {
                    var partPath = _pathProvider.GetTrackPath(ctx.Model.Artist, ctx.Model.Album ?? "Unknown", ctx.Model.Title, "mp3") + ".part";
                    activePartPaths.Add(partPath);
                }
            }
            await _pathProvider.CleanupOrphanedPartFilesAsync(activePartPaths);
            
            // Start the Enrichment Orchestrator
            _enrichmentOrchestrator.Start();
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Failed to init persistence layer");
        }
    }


    // Track updates now published via IEventBus (TrackUpdatedEvent)

    /// <summary>
    /// Queues a project (a PlaylistJob) for processing and persists the job header and tracks.
    /// This is the preferred entry point for importing new multi-track projects.
    /// </summary>
    public async Task QueueProject(PlaylistJob job)
    {
        // Add correlation context for all logs related to this job
        using (LogContext.PushProperty("PlaylistJobId", job.Id))
        using (LogContext.PushProperty("JobName", job.SourceTitle))
        {
            // Robustness: If the job comes from an import preview, it will have OriginalTracks
            // but no PlaylistTracks. We must convert them before proceeding.
            if (job.PlaylistTracks.Count == 0 && job.OriginalTracks.Count > 0)
            {
                _logger.LogInformation("Gap analysis: Checking for existing tracks in Job {JobId} to avoid duplicates", job.Id);
                
                // Phase 7.1: Robust Deduplication
                // Load existing track hashes for this job to avoid adding duplicates
                var existingHashes = new HashSet<string>();
                try 
                {
                    var existingJob = await _libraryService.FindPlaylistJobAsync(job.Id);
                    if (existingJob != null)
                    {
                        foreach (var t in existingJob.PlaylistTracks)
                        {
                            if (!string.IsNullOrEmpty(t.TrackUniqueHash))
                                existingHashes.Add(t.TrackUniqueHash);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load existing tracks for gap analysis, proceeding cautiously");
                }

                _logger.LogInformation("Converting {OriginalTrackCount} OriginalTracks to PlaylistTracks (Existing: {ExistingCount})", 
                    job.OriginalTracks.Count, existingHashes.Count);
                
                var playlistTracks = new List<PlaylistTrack>();
                int idx = existingHashes.Count + 1;
                foreach (var track in job.OriginalTracks)
                {
                    // SKIP if already in this project
                    if (existingHashes.Contains(track.UniqueHash))
                    {
                        _logger.LogDebug("Skipping track '{Title}' - already exists in this project (or already seen in this batch)", track.Title);
                        continue;
                    }

                    existingHashes.Add(track.UniqueHash);

                    playlistTracks.Add(new PlaylistTrack
                    {
                        Id = Guid.NewGuid(),
                        PlaylistId = job.Id,
                        Artist = track.Artist ?? string.Empty,
                        Title = track.Title ?? string.Empty,
                        Album = track.Album ?? string.Empty,
                        TrackUniqueHash = track.UniqueHash,
                        Status = TrackStatus.Missing,
                        ResolvedFilePath = string.Empty,
                        TrackNumber = idx++,
                        // Map Metadata if available from import
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
                }
                job.PlaylistTracks = playlistTracks;
                job.TotalTracks = existingHashes.Count + playlistTracks.Count;
            }

            _logger.LogInformation("Queueing project with {TrackCount} tracks", job.PlaylistTracks.Count);

            // 0. Set Album Art for the Job from the first track if available
            if (string.IsNullOrEmpty(job.AlbumArtUrl))
            {
                 job.AlbumArtUrl = job.OriginalTracks.FirstOrDefault(t => !string.IsNullOrEmpty(t.AlbumArtUrl))?.AlbumArtUrl 
                                   ?? job.PlaylistTracks.FirstOrDefault(t => !string.IsNullOrEmpty(t.AlbumArtUrl))?.AlbumArtUrl;
            }

            // 1. Persist the job header and all associated tracks via LibraryService
            try
            {
                await _libraryService.SavePlaylistJobWithTracksAsync(job);
                _logger.LogInformation("Saved PlaylistJob to database with {TrackCount} tracks", job.PlaylistTracks.Count);
                await _databaseService.LogPlaylistJobDiagnostic(job.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist PlaylistJob and its tracks");
                throw; // CRITICAL: Propagate error so caller (ImportPreview) knows it failed
            }

            // 3. Queue the tracks using the internal method
            QueueTracks(job.PlaylistTracks);
            
            // 4. Fire event for Library UI to refresh
            // Duplicate event removed: LibraryService already publishes ProjectAddedEvent
            // _eventBus.Publish(new ProjectAddedEvent(job.Id));
        }
    }

    /// <summary>
    /// Internal method to queue a list of individual tracks for processing (e.g. from an existing project or ad-hoc).
    /// </summary>
    public void QueueTracks(List<PlaylistTrack> tracks)
    {
        _logger.LogInformation("Queueing project tracks with {Count} tracks", tracks.Count);
        
        int skipped = 0;
        int queued = 0;
        
        lock (_collectionLock)
        {
            // Build a set of existing track IDs for fast lookup
            var existingTrackIds = new HashSet<Guid>(_downloads.Select(d => d.Model.Id));
            
            foreach (var track in tracks)
            {
                // Skip if already queued
                if (existingTrackIds.Contains(track.Id))
                {
                    skipped++;
                    _logger.LogDebug("Skipping track {Artist} - {Title}: already queued", track.Artist, track.Title);
                    continue;
                }
                
                var ctx = new DownloadContext(track);
                _downloads.Add(ctx);
                existingTrackIds.Add(track.Id); // Add to set for subsequent iterations
                queued++;
                
                // Publish Event
                _eventBus.Publish(new TrackAddedEvent(track));
                
                // Persist new track
                _ = SaveTrackToDb(ctx);
                
                // Only queue enrichment if it's new/pending
                if (ctx.State == PlaylistTrackState.Pending || ctx.State == PlaylistTrackState.Searching)
                {
                    _enrichmentOrchestrator.QueueForEnrichment(ctx.Model);
                }
            }
        }
        
        if (skipped > 0)
        {
            _logger.LogInformation("Queued {Queued} new tracks, skipped {Skipped} already queued tracks", queued, skipped);
        }
        
        // Processing loop picks this up automatically
    }

    // Updated Delete to take GlobalId instead of VM
    public async Task DeleteTrackFromDiskAndHistoryAsync(string globalId)
    {
        DownloadContext? ctx;
        lock(_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == globalId);
        
        if (ctx == null) return;
        
        using (LogContext.PushProperty("TrackHash", globalId))
        {
            _logger.LogInformation("Deleting track from disk and history");

            // 1. Cancel active download
            ctx.CancellationTokenSource?.Cancel();

            // 2. Delete Physical Files
            DeleteLocalFiles(ctx.Model.ResolvedFilePath);

            // 3. Remove from Global History (DB)
            await _databaseService.RemoveTrackAsync(globalId);

            // 4. Update references in Playlists (DB)
            await _databaseService.UpdatePlaylistTrackStatusAndRecalculateJobsAsync(globalId, TrackStatus.Missing, string.Empty);

            // 5. Remove from Memory
            lock (_collectionLock) _downloads.Remove(ctx);
            _eventBus.Publish(new TrackRemovedEvent(globalId));
        }
    }
    
    private void DeleteLocalFiles(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogInformation("Deleted file: {Path}", path);
            }
            
            var partPath = path + ".part";
            if (File.Exists(partPath))
            {
                File.Delete(partPath);
                _logger.LogInformation("Deleted partial file: {Path}", partPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete file(s) for path {Path}", path);
        }
    }

    // Removed OnTrackPropertyChanged - Service no longer listens to VM property changes
    
    // Helper to update state and publish event
    public async Task UpdateStateAsync(DownloadContext ctx, PlaylistTrackState newState, string? error = null)
    {
        if (ctx.State == newState && ctx.ErrorMessage == error) return;
        
        ctx.State = newState;
        ctx.ErrorMessage = error; // Update context
        
        // Publish with ProjectId for targeted updates
        _eventBus.Publish(new Events.TrackStateChangedEvent(ctx.GlobalId, ctx.Model.PlaylistId, newState, error));
        
        // DB Persistence for critical states
        await SaveTrackToDb(ctx);
        
        if (newState == PlaylistTrackState.Completed || newState == PlaylistTrackState.Failed || newState == PlaylistTrackState.Cancelled)
        {
             await UpdatePlaylistStatusAsync(ctx);
        }
    }
    
     private async Task UpdatePlaylistStatusAsync(DownloadContext ctx)
    {
        try
        {
            var dbStatus = ctx.State switch
            {
                PlaylistTrackState.Completed => TrackStatus.Downloaded,
                PlaylistTrackState.Failed => TrackStatus.Failed,
                PlaylistTrackState.Cancelled => TrackStatus.Skipped,
                _ => ctx.Model.Status
            };

            var updatedJobIds = await _databaseService.UpdatePlaylistTrackStatusAndRecalculateJobsAsync(
                ctx.GlobalId, 
                dbStatus, 
                ctx.Model.ResolvedFilePath
            );

            // Notify the Library UI to refresh the specific Project Header
            foreach (var jobId in updatedJobIds)
            {
                _eventBus.Publish(new ProjectUpdatedEvent(jobId));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync playlist track status for {Id}", ctx.GlobalId);
        }
    }

    private async Task SaveTrackToDb(DownloadContext ctx)
    {
        try 
        {
            await _databaseService.SaveTrackAsync(new Data.TrackEntity 
            {
                GlobalId = ctx.GlobalId,
                Artist = ctx.Model.Artist,
                Title = ctx.Model.Title,
                State = ctx.State.ToString(),
                Filename = ctx.Model.ResolvedFilePath,
                Size = 0, 
                AddedAt = DateTime.Now, // ctx doesn't track AddedAt yet, assume Now or Model property
                ErrorMessage = ctx.ErrorMessage,
                AlbumArtUrl = ctx.Model.AlbumArtUrl,
                SpotifyTrackId = ctx.Model.SpotifyTrackId,
                // These are actually only in PlaylistTrackEntity currently, 
                // but TrackEntity (global history) doesn't have them yet.
                // For now, focusing on the PlaylistTrack flow which is the primary failure point.
            });
        }  
        catch (Exception ex)
        {
            _logger.LogError(ex, "DB Save Failed");
        }
    }

    /// <summary>
    /// Phase 2.5: Enhanced pause with immediate cancellation and IsUserPaused tracking.
    /// </summary>
    public async Task PauseTrackAsync(string globalId)
    {
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(t => t.GlobalId == globalId);
        
        if (ctx == null)
        {
            _logger.LogWarning("Cannot pause track {Id}: not found", globalId);
            return;
        }
        
        // CRITICAL: Cancel the CancellationTokenSource immediately
        // This ensures the download stops mid-transfer and preserves the .part file
        ctx.CancellationTokenSource?.Cancel();
        ctx.CancellationTokenSource = new CancellationTokenSource(); // Reset for resume
        
        await UpdateStateAsync(ctx, PlaylistTrackState.Paused);
        
        // Mark as user-paused in DB so hydration knows not to auto-resume
        try
        {
            var job = await _libraryService.FindPlaylistJobAsync(ctx.Model.PlaylistId);
            if (job != null)
            {
                job.IsUserPaused = true;
                // Update via LibraryService (uses Save internally)
                await _libraryService.SavePlaylistJobAsync(job);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mark job as user-paused in DB (non-fatal)");
        }
        
        _logger.LogInformation("⏸️ Paused track: {Artist} - {Title} (user-initiated)", ctx.Model.Artist, ctx.Model.Title);
    }

    /// <summary>
    /// Pauses all active downloads.
    /// </summary>
    public async Task PauseAllAsync() 
    {
        List<DownloadContext> active;
        lock (_collectionLock)
        {
             active = _downloads.Where(d => d.IsActive).ToList();
        }

        if (active.Any())
        {
            _logger.LogInformation("⏸️ Pausing all {Count} active downloads...", active.Count);
            foreach(var d in active) 
            {
                 await PauseTrackAsync(d.GlobalId);
            }
        }
    }

    public async Task ResumeTrackAsync(string globalId)
    {
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(t => t.GlobalId == globalId);
        if (ctx != null)
        {
            _ = UpdateStateAsync(ctx, PlaylistTrackState.Pending);
        }
    }

    public void HardRetryTrack(string globalId)
    {
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(t => t.GlobalId == globalId);
        if (ctx == null) return;

        _logger.LogInformation("Hard Retry for {GlobalId}", globalId);
        ctx.CancellationTokenSource?.Cancel();
        _ = UpdateStateAsync(ctx, PlaylistTrackState.Pending); // Reset to Pending

        DeleteLocalFiles(ctx.Model.ResolvedFilePath);
        
        ctx.State = PlaylistTrackState.Pending;
        ctx.Progress = 0;
        ctx.ErrorMessage = null;
        ctx.CancellationTokenSource = new CancellationTokenSource(); // Reset CTS
        
        // Publish reset event (handled by StateChanged to Pending usually, but verify UI clears error)
        _eventBus.Publish(new Events.TrackStateChangedEvent(ctx.GlobalId, ctx.Model.PlaylistId, PlaylistTrackState.Pending, null));
    }

    public void CancelTrack(string globalId)
    {
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(t => t.GlobalId == globalId);
        if (ctx == null) return;

        _logger.LogInformation("Cancelling track: {Artist} - {Title}", ctx.Model.Artist, ctx.Model.Title);

        ctx.CancellationTokenSource?.Cancel();
        ctx.CancellationTokenSource = new CancellationTokenSource(); // Reset
        
        _ = UpdateStateAsync(ctx, PlaylistTrackState.Cancelled);
        DeleteLocalFiles(ctx.Model.ResolvedFilePath);
    }
    
    public async Task UpdateTrackFiltersAsync(string globalId, string formats, int minBitrate)
    {
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == globalId);
        
        if (ctx != null)
        {
            ctx.Model.PreferredFormats = formats;
            ctx.Model.MinBitrateOverride = minBitrate;
            
            // Persist to DB immediately
            await SaveTrackToDb(ctx);
            
            // If it's a playlist track, update that entity too
            try 
            {
                using var context = new Data.AppDbContext();
                var pt = await context.PlaylistTracks.FirstOrDefaultAsync(t => t.Id == ctx.Model.Id);
                if (pt != null)
                {
                    pt.PreferredFormats = formats;
                    pt.MinBitrateOverride = minBitrate;
                    await context.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update PlaylistTrack filters in DB for {Id}", globalId);
            }
        }
    }
    
    public void EnqueueTrack(Track track)
    {
        var playlistTrack = new PlaylistTrack
        {
             Id = Guid.NewGuid(),
             Artist = track.Artist ?? "Unknown",
             Title = track.Title ?? "Unknown",
             Album = track.Album ?? "Unknown",
             Status = TrackStatus.Missing,
             ResolvedFilePath = Path.Combine(_config.DownloadDirectory!, _fileNameFormatter.Format(_config.NameFormat ?? "{artist} - {title}", track) + "." + track.GetExtension()),
             TrackUniqueHash = track.UniqueHash
        };
        
        QueueTracks(new List<PlaylistTrack> { playlistTrack });
    }

    /// <summary>
    /// Phase 2.5: Hydration - Detects and resumes orphaned downloads from crashed/closed sessions.
    /// Scans for tracks stuck in Downloading/Searching state and checks for existing .part files.
    /// </summary>
    private async Task HydrateFromCrashAsync()
    {
        var orphanedTracks = new List<DownloadContext>();
        
        lock (_collectionLock)
        {
            orphanedTracks = _downloads.Where(t => 
                t.State == PlaylistTrackState.Downloading || 
                t.State == PlaylistTrackState.Searching).ToList();
        }
        
        if (!orphanedTracks.Any())
        {
            _logger.LogInformation("✅ No orphaned downloads detected. Clean startup.");
            return;
        }

        _logger.LogWarning("🔍 Detected {Count} orphaned downloads. Hydrating...", orphanedTracks.Count);
        
        int resumedCount = 0;
        foreach (var ctx in orphanedTracks)
        {
            // Check for existing .part file to resume
            var partPath = _pathProvider.GetTrackPath(
                ctx.Model.Artist, 
                ctx.Model.Album ?? "Unknown Album", 
                ctx.Model.Title, 
                "mp3") + ".part";
            
            if (File.Exists(partPath))
            {
                var partSize = new FileInfo(partPath).Length;
                ctx.BytesReceived = partSize;
                ctx.IsResuming = true;
                resumedCount++;
                
                _logger.LogInformation("📁 Found .part file ({Size:N0} bytes) for {Track}. Will resume.", 
                    partSize, ctx.Model.Title);
            }
            
            // Reset to Pending so ProcessQueueLoop picks it up
            // Pro-tip from user: Avoid firing too many UI events during startup
            ctx.State = PlaylistTrackState.Pending;
        }
        
        // Batch DB update (more efficient than individual updates)
        foreach (var ctx in orphanedTracks)
        {
            await UpdateStateAsync(ctx, PlaylistTrackState.Pending);
        }
        
        // UX: Notify via logs (UI notification event can be added in Step 4: Download Center UI)
        if (orphanedTracks.Count > 0)
        {
            var message = orphanedTracks.Count == 1 
                ? "Resuming 1 interrupted download."
                : $"Resuming {orphanedTracks.Count} interrupted downloads.";
                
            _logger.LogInformation("📢 {Message}", message);
        }
        
        _logger.LogInformation("✅ Hydration complete: {Total} orphaned, {Resumed} with .part files", 
            orphanedTracks.Count, resumedCount);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_processingTask != null) return;

        _logger.LogInformation("DownloadManager Orchestrator started.");
        await InitAsync();
        _processingTask = ProcessQueueLoop(_globalCts.Token);
        await Task.CompletedTask;
    }

    private async Task ProcessQueueLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                DownloadContext? nextContext = null;
                lock (_collectionLock)
                {
                    // Phase 2: Enrichment Gate
                    // Only pick up tracks that are Enriched (have Audio Features + ID)
                    // OR if they have been pending for > 5 minutes (fail-safe for enrichment timeouts)
                    nextContext = _downloads.FirstOrDefault(t => 
                        t.State == PlaylistTrackState.Pending && 
                        (!t.NextRetryTime.HasValue || t.NextRetryTime.Value <= DateTime.Now) &&
                        (t.Model.IsEnriched || (DateTime.Now - t.Model.AddedAt).TotalMinutes > 5));
                }

                if (nextContext == null)
                {
                    await Task.Delay(500, token);
                    continue;
                }

                // CRITICAL: Wait for one of the 4 semaphore slots to open up
                // This blocks until a slot is available, ensuring max 4 concurrent downloads
                await _downloadSemaphore.WaitAsync(token);

                // Transition state via update method
                await UpdateStateAsync(nextContext, PlaylistTrackState.Searching);

                // Fire-and-forget pattern with guaranteed semaphore release
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessTrackAsync(nextContext, token);
                    }
                    finally
                    {
                        // ALWAYS release the semaphore, even if processing crashes
                        _downloadSemaphore.Release();
                        _logger.LogDebug("Released semaphore slot. Available slots: {Available}/4", 
                            _downloadSemaphore.CurrentCount);
                    }
                }, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DownloadManager processing loop exception");
                await Task.Delay(1000, token); // Prevent hot loop on error
            }
        }
    }

    private async Task ProcessTrackAsync(DownloadContext ctx, CancellationToken ct)
    {
        ctx.CancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var trackCt = ctx.CancellationTokenSource.Token;

        using (LogContext.PushProperty("TrackHash", ctx.GlobalId))
        {
            try
            {
                // Pre-check
                if (ctx.Model.Status == TrackStatus.Downloaded && File.Exists(ctx.Model.ResolvedFilePath))
                {
                    await UpdateStateAsync(ctx, PlaylistTrackState.Completed);
                    return;
                }

                // Phase 3.1: Use Detection Service (Searching State)
                // Refactor Note: DiscoveryService now takes PlaylistTrack (Decoupled).
                var bestMatch = await _discoveryService.FindBestMatchAsync(ctx.Model, trackCt);

                if (bestMatch == null)
                {
                    // Check if we should auto-retry
                    if (_config.AutoRetryFailedDownloads && ctx.RetryCount < _config.MaxDownloadRetries)
                    {
                         _logger.LogWarning("No match found for {Title}. Auto-retrying (Attempt {Count}/{Max})", 
                             ctx.Model.Title, ctx.RetryCount + 1, _config.MaxDownloadRetries);
                         
                         // Throw to trigger the exponential backoff logic in catch block
                         throw new Exception("No suitable match found");
                    }

                    await UpdateStateAsync(ctx, PlaylistTrackState.Failed, "No suitable match found");
                    return;
                }

                // Phase 3.1: Download Logic (Downloading State)
                await DownloadFileAsync(ctx, bestMatch, trackCt);
            }
            catch (OperationCanceledException)
            {
                if (ctx.State != PlaylistTrackState.Paused && ctx.State != PlaylistTrackState.Cancelled)
                {
                    await UpdateStateAsync(ctx, PlaylistTrackState.Cancelled);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProcessTrackAsync error for {GlobalId}", ctx.GlobalId);
                
                // Exponential Backoff Logic (Phase 7)
                ctx.RetryCount++;
                if (ctx.RetryCount < _config.MaxDownloadRetries)
                {
                    var delayMinutes = Math.Pow(2, ctx.RetryCount); // 2, 4, 8, 16...
                    ctx.NextRetryTime = DateTime.Now.AddMinutes(delayMinutes);
                    await UpdateStateAsync(ctx, PlaylistTrackState.Pending, $"Retrying in {delayMinutes}m: {ex.Message}");
                    _logger.LogInformation("Scheduled retry #{Count} for {GlobalId} at {Time}", ctx.RetryCount, ctx.GlobalId, ctx.NextRetryTime);
                }
                else
                {
                    await UpdateStateAsync(ctx, PlaylistTrackState.Failed, $"Max retries exceeded: {ex.Message}");
                }
            }
        }
    }

    private async Task DownloadFileAsync(DownloadContext ctx, Track bestMatch, CancellationToken ct)
    {
        await UpdateStateAsync(ctx, PlaylistTrackState.Downloading);

        // Phase 2.5: Use PathProviderService for consistent folder structure
        var finalPath = _pathProvider.GetTrackPath(
            ctx.Model.Artist,
            ctx.Model.Album ?? "Unknown Album",
            ctx.Model.Title,
            bestMatch.GetExtension()
        );

        var partPath = finalPath + ".part";
        long startPosition = 0;

        // STEP 1: Check if final file already exists and is complete
        if (File.Exists(finalPath))
        {
            var existingFileInfo = new FileInfo(finalPath);
            if (existingFileInfo.Length == bestMatch.Size)
            {
                _logger.LogInformation("File already exists and is complete: {Path}", finalPath);
                ctx.Model.ResolvedFilePath = finalPath;
                ctx.Progress = 100;
                await UpdateStateAsync(ctx, PlaylistTrackState.Completed);
                return;
            }
            else
            {
                // File exists but is incomplete (corrupted?) - delete and restart
                _logger.LogWarning("Final file exists but size mismatch (expected {Expected}, got {Actual}). Deleting and restarting.", 
                    bestMatch.Size, existingFileInfo.Length);
                File.Delete(finalPath);
            }
        }

        // STEP 2: Check for existing .part file to resume
        if (File.Exists(partPath))
        {
            var partFileInfo = new FileInfo(partPath);
            startPosition = partFileInfo.Length;
            ctx.IsResuming = true;
            ctx.BytesReceived = startPosition;
            
            _logger.LogInformation("Resuming download from byte {Position} for {Track}", 
                startPosition, ctx.Model.Title);
        }
        else
        {
            ctx.IsResuming = false;
            ctx.BytesReceived = 0;
        }

        // STEP 3: Set total bytes for progress tracking
        ctx.TotalBytes = bestMatch.Size ?? 0;  // Handle nullable size

        // STEP 4: Progress tracking with 100ms throttling
        var lastNotificationTime = DateTime.MinValue;
        var totalFileSize = bestMatch.Size ?? 1;  // Avoid division by zero
        var progress = new Progress<double>(p =>
        {
            ctx.Progress = p * 100;
            ctx.BytesReceived = (long)(bestMatch.Size * p);

            // Throttle to 10 updates/sec to prevent UI stuttering
            if ((DateTime.Now - lastNotificationTime).TotalMilliseconds > 100)
            {
                _eventBus.Publish(new Events.TrackProgressChangedEvent(
                    ctx.GlobalId, 
                    ctx.Progress,
                    ctx.BytesReceived,
                    ctx.TotalBytes
                ));
                
                lastNotificationTime = DateTime.Now;
            }
        });

        // STEP 5: Download to .part file with resume support
        var success = await _soulseek.DownloadAsync(
            bestMatch.Username!,
            bestMatch.Filename!,
            partPath,          // Download to .part file
            bestMatch.Size,
            progress,
            ct,
            startPosition      // Resume from existing bytes
        );

        if (success)
        {
            // STEP 6: Atomic Rename - Only if download completed successfully
            try
            {
                // Brief pause to ensure all file handles are released
                await Task.Delay(100, ct);

                // Verify .part file exists and has correct size
                if (!File.Exists(partPath))
                {
                    throw new FileNotFoundException($"Part file disappeared: {partPath}");
                }

                var finalPartSize = new FileInfo(partPath).Length;
                if (finalPartSize != bestMatch.Size)
                {
                    throw new InvalidDataException(
                        $"Downloaded file size mismatch. Expected {bestMatch.Size}, got {finalPartSize}");
                }

                // Clean up old final file if it exists (race condition edge case)
                if (File.Exists(finalPath))
                {
                    _logger.LogWarning("Final path {Path} already exists during atomic rename. Deleting.", finalPath);
                    File.Delete(finalPath);
                }

                // ATOMIC OPERATION: Only now does .mp3 appear
                File.Move(partPath, finalPath);
                
                _logger.LogInformation("Atomic rename complete: {Part} → {Final}", 
                    Path.GetFileName(partPath), Path.GetFileName(finalPath));

                ctx.Model.ResolvedFilePath = finalPath;
                ctx.Progress = 100;
                ctx.BytesReceived = bestMatch.Size ?? 0;  // Handle nullable size
                await UpdateStateAsync(ctx, PlaylistTrackState.Completed);

                // Phase 3.1: Finalize with Metadata Service (Tagging)
                await _enrichmentOrchestrator.FinalizeDownloadedTrackAsync(ctx.Model);
            }
            catch (Exception renameEx)
            {
                _logger.LogError(renameEx, "Failed to perform atomic rename for {Track}", ctx.Model.Title);
                await UpdateStateAsync(ctx, PlaylistTrackState.Failed, 
                    $"Atomic rename failed: {renameEx.Message}");
            }
        }
        else
        {
            await UpdateStateAsync(ctx, PlaylistTrackState.Failed, 
                "Download transfer failed or was cancelled. .part file retained for resume.");
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void OnAutoDownloadTrack(AutoDownloadTrackEvent e)
    {
        _logger.LogInformation("Auto-Download triggered for {TrackId}", e.TrackGlobalId);
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == e.TrackGlobalId);
        if (ctx == null) return;

        _ = Task.Run(async () => 
        {
            await UpdateStateAsync(ctx, PlaylistTrackState.Downloading);
            await DownloadFileAsync(ctx, e.BestMatch, _globalCts.Token);
        });
    }

    private void OnAutoDownloadUpgrade(AutoDownloadUpgradeEvent e)
    {
        _logger.LogInformation("Auto-Upgrade triggered for {TrackId}", e.TrackGlobalId);
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == e.TrackGlobalId);
        if (ctx == null) return;

        _ = Task.Run(async () => 
        {
            // 1. Delete old file first to avoid confusion
            if (!string.IsNullOrEmpty(ctx.Model.ResolvedFilePath))
            {
                DeleteLocalFiles(ctx.Model.ResolvedFilePath);
            }

            // 2. Clear old quality metrics
            ctx.Model.Bitrate = null;
            ctx.Model.SpectralHash = null;
            ctx.Model.IsTrustworthy = null;

            // 3. Start download of higher quality file
            await UpdateStateAsync(ctx, PlaylistTrackState.Downloading);
            await DownloadFileAsync(ctx, e.BestMatch, _globalCts.Token);
        });
    }

    private void OnUpgradeAvailable(UpgradeAvailableEvent e)
    {
        // For now just log, could trigger a notification in future
        _logger.LogInformation("Upgrade Available (Manual Approval Needed): {TrackId} - {BestMatch}", 
            e.TrackGlobalId, e.BestMatch.Filename);
    }

    public void Dispose()
    {
        _globalCts.Cancel();
        _globalCts.Dispose();
        _processingTask?.Wait();
        _enrichmentOrchestrator.Dispose();
    }
}

