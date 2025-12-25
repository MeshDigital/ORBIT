using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json; // Phase 2A: Checkpoint serialization
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Serilog.Context;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
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
    private readonly SLSKDONET.Services.IO.IFileWriteService _fileWriteService; // Phase 1A
    private readonly CrashRecoveryJournal _crashJournal; // Phase 2A

    // Phase 2.5: Concurrency control with SemaphoreSlim throttling
    private readonly CancellationTokenSource _globalCts = new();
    private readonly SemaphoreSlim _downloadSemaphore = new(4, 4); // Max 4 concurrent downloads
    private Task? _processingTask;

    // Global State managed via Events
    private readonly List<DownloadContext> _downloads = new();
    private readonly object _collectionLock = new object();
    
    private const int LAZY_QUEUE_BUFFER_SIZE = 100;
    private const int REFILL_THRESHOLD = 20;

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
        PathProviderService pathProvider,
        SLSKDONET.Services.IO.IFileWriteService fileWriteService, // Phase 1A
        CrashRecoveryJournal crashJournal) // Phase 2A
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
        _fileWriteService = fileWriteService; // Phase 1A
        _crashJournal = crashJournal; // Phase 2A

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
    /// Returns a snapshot of all current downloads for ViewModel hydration.
    /// </summary>
    public IReadOnlyList<(PlaylistTrack Model, PlaylistTrackState State)> GetAllDownloads()
    {
        lock (_collectionLock)
        {
            return _downloads.Select(ctx => (ctx.Model, ctx.State)).ToList();
        }
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
            
            // Phase 3C.5: Lazy Hydration - Only load active/history and a buffer of pending tracks
            
            // 1. Load History & Active (Status != Missing)
            var nonPendingTracks = await _databaseService.GetNonPendingTracksAsync();
            HydrateAndAddEntities(nonPendingTracks);
            
            _logger.LogInformation("Hydrated {Count} active/history tracks", nonPendingTracks.Count);

            // 2. Initial Refill of Waiting Room (Top N Pending)
            await RefillQueueAsync();

            
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
                
                // Phase 3C.5: Lazy Hydration Guard
                // If buffer is full, don't add new low-priority tracks to memory yet.
                // They are already persisted to DB by QueueProject or SaveTrackToDb below.
                // Exception: Priority 0 (High) should always bypass buffer limit.
                if (_downloads.Count(d => d.State == PlaylistTrackState.Pending) >= LAZY_QUEUE_BUFFER_SIZE 
                    && track.Priority > 0)
                {
                     // Skip adding to memory, it will be picked up by RefillQueueAsync later
                     skipped++; 
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
        
        // Trigger generic refill in case we have capacity
        if (queued > 0)
        {
             _ = RefillQueueAsync();
        }
    }

    private void HydrateAndAddEntities(List<PlaylistTrackEntity> entities)
    {
        lock (_collectionLock)
        {
            foreach (var t in entities)
            {
                // Map PlaylistTrackEntity -> PlaylistTrack Model
                var model = new PlaylistTrack 
                { 
                    Id = t.Id,
                    PlaylistId = t.PlaylistId,
                    Artist = t.Artist, 
                    Title = t.Title,
                    Album = t.Album,
                    TrackUniqueHash = t.TrackUniqueHash,
                    Status = t.Status,
                    ResolvedFilePath = t.ResolvedFilePath,
                    SpotifyTrackId = t.SpotifyTrackId,
                    AlbumArtUrl = t.AlbumArtUrl,
                    Format = t.Format,
                    Bitrate = t.Bitrate,
                    Priority = t.Priority
                };
                
                // Map status to download state
                var ctx = new DownloadContext(model);
                ctx.State = t.Status switch
                {
                    TrackStatus.Downloaded => PlaylistTrackState.Completed,
                    TrackStatus.Failed => PlaylistTrackState.Failed,
                    TrackStatus.Skipped => PlaylistTrackState.Cancelled,
                    _ => PlaylistTrackState.Pending
                };
                
                // Reset transient states
                if (ctx.State == PlaylistTrackState.Downloading || ctx.State == PlaylistTrackState.Searching)
                    ctx.State = PlaylistTrackState.Pending;

                _downloads.Add(ctx);
                
                // Publish event with initial state
                _eventBus.Publish(new TrackAddedEvent(model, ctx.State));
            }
        }
    }

    /// <summary>
    /// Phase 3C.5: "The Waiting Room" - Fetches pending tracks from DB if buffer is low.
    /// Manages memory pressure by ensuring we don't hydrate 50,000 pending tracks.
    /// </summary>
    private async Task RefillQueueAsync()
    {
        try
        {
            List<Guid> excludeIds;
            int needed;

            lock (_collectionLock)
            {
                int pendingCount = _downloads.Count(d => d.State == PlaylistTrackState.Pending);
                if (pendingCount >= LAZY_QUEUE_BUFFER_SIZE) return; // Buffer full enough

                needed = LAZY_QUEUE_BUFFER_SIZE - pendingCount;
                excludeIds = _downloads.Select(d => d.Model.Id).ToList();
            }

            if (needed <= 0) return;

            // Fetch next batch from "Waiting Room" (DB)
            var newTracks = await _databaseService.GetPendingPriorityTracksAsync(needed, excludeIds);
            
            if (newTracks.Any())
            {
                _logger.LogDebug("Refilling queue with {Count} tracks from Waiting Room", newTracks.Count);
                HydrateAndAddEntities(newTracks);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refill queue from database");
        }
    }    
        // Processing loop picks this up automatically

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
    public void PromoteTrackToExpress(string globalId)
    {
        DownloadContext? ctx;
        lock(_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == globalId);
        
        if (ctx != null)
        {
            ctx.Model.Priority = 0;
            _logger.LogInformation("Creating VIP Pass for {Title} (Priority 0)", ctx.Model.Title);
            // In a real implementation, we would persist this to PlaylistTrackEntity here.
        }
    }

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

    /// <summary>
    /// Phase 3B: Health Monitor Intervention
    /// Cancels a stalled download, blacklists the peer, and re-queues it for discovery.
    /// Non-destructive: Does NOT delete the .part file (optimistic resume if new peer has same file).
    /// </summary>
    public async Task AutoRetryStalledDownloadAsync(string globalId)
    {
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(t => t.GlobalId == globalId);
        
        if (ctx == null) return;

        var stalledUser = ctx.CurrentUsername;
        if (!string.IsNullOrEmpty(stalledUser))
        {
            ctx.BlacklistedUsers.Add(stalledUser);
            _logger.LogWarning("⚠️ Health Monitor: Blacklisting peer {User} for {Track}", stalledUser, ctx.Model.Title);
        }

        // 1. Cancel active transfer (stops Soulseek)
        ctx.CancellationTokenSource?.Cancel();
        
        // 2. IMPORTANT: Don't delete files! We want to try to resume from another peer if possible.
        // Wait, Soulseek resume requires same file hash. DiscoveryService might find a different file hash.
        // If different file hash, Resume logic (based on file size match?) might be risky.
        // DownloadFileAsync logic checks .part file size.
        // If new file is different size, it might think it's truncated or ghost.
        // Safe bet: For now, we trust the resume logic to handle mismatches (it checks sizes).

        // 3. Reset state to Pending so ProcessQueueLoop picks it up
        await UpdateStateAsync(ctx, PlaylistTrackState.Pending, "Auto-retrying after stall");
        
        // Reset CTS for next attempt
        ctx.CancellationTokenSource = new CancellationTokenSource();
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
                    // Phase 3C: Multi-Lane Priority Engine
                    // Weighted selection algorithm with slot allocation
                    var eligibleTracks = _downloads.Where(t => 
                        t.State == PlaylistTrackState.Pending && 
                        (!t.NextRetryTime.HasValue || t.NextRetryTime.Value <= DateTime.Now) &&
                        (t.Model.IsEnriched || (DateTime.Now - t.Model.AddedAt).TotalMinutes > 5))
                        .ToList();

                    if (eligibleTracks.Any())
                    {
                        // Get current slot allocation by priority
                        var activeByPriority = GetActiveDownloadsByPriority();
                        
                        // Try to find next track respecting lane limits
                        nextContext = SelectNextTrackWithLaneAllocation(eligibleTracks, activeByPriority);
                    }
                    
                    // Phase 3C.5: Check if we need to release the hounds (Refill)
                    var pendingCount = _downloads.Count(d => d.State == PlaylistTrackState.Pending);
                    if (pendingCount < REFILL_THRESHOLD)
                    {
                         // Trigger background refill
                         _ = Task.Run(() => RefillQueueAsync());
                    }
                }

                if (nextContext == null)
                {
                    await Task.Delay(500, token);
                    continue;
                }

                // CRITICAL: Wait for one of the 4 semaphore slots to open up
                // This blocks until a slot is available, ensuring max 4 concurrent downloads
                await _downloadSemaphore.WaitAsync(token);

                // Phase 3C Hardening: Race Condition Check
                // After waiting, the world may have changed (e.g., lane filled by stealth/high prio).
                // We MUST re-confirm this track is still the best choice and valid.
                DownloadContext? confirmedContext = null;
                lock (_collectionLock)
                {
                    // Update Active map with new reality
                    var activeByPriority = GetActiveDownloadsByPriority();
                    
                    // Check if our pre-selected 'nextContext' is still valid and optimal
                    // Or simply re-run selection to be safe
                    var eligibleTracks = _downloads.Where(t => 
                        t.State == PlaylistTrackState.Pending && 
                        (!t.NextRetryTime.HasValue || t.NextRetryTime.Value <= DateTime.Now))
                        .ToList();

                    confirmedContext = SelectNextTrackWithLaneAllocation(eligibleTracks, activeByPriority);
                }

                if (confirmedContext == null)
                {
                    // False alarm or lane filled up while waiting
                    _logger.LogDebug("Race Condition: Slot acquired but no eligible track found after wait. Releasing.");
                    _downloadSemaphore.Release();
                    await Task.Delay(100, token); // Backoff
                    continue;
                }

                // If we switched tracks (e.g. a higher priority one came in), use the new one.
                // If confirmedContext matches nextContext, great. If not, confirmedContext is better.
                nextContext = confirmedContext;

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
                // Pre-check: Already downloaded in this project
                if (ctx.Model.Status == TrackStatus.Downloaded && File.Exists(ctx.Model.ResolvedFilePath))
                {
                    await UpdateStateAsync(ctx, PlaylistTrackState.Completed);
                    return;
                }

                // Phase 0: Check if file already exists in global library (cross-project deduplication)
                var existingEntry = await _libraryService.FindLibraryEntryAsync(ctx.Model.TrackUniqueHash);
                if (existingEntry != null && File.Exists(existingEntry.FilePath))
                {
                    _logger.LogInformation("♻️ Track already in library: {Artist} - {Title}, reusing file: {Path}", 
                        ctx.Model.Artist, ctx.Model.Title, existingEntry.FilePath);
                    
                    // Reuse existing file instead of downloading
                    ctx.Model.ResolvedFilePath = existingEntry.FilePath;
                    ctx.Model.Status = TrackStatus.Downloaded;
                    await _libraryService.UpdatePlaylistTrackAsync(ctx.Model);
                    await UpdateStateAsync(ctx, PlaylistTrackState.Completed);
                    return;
                }

                // Phase 3.1: Use Detection Service (Searching State)
                // Refactor Note: DiscoveryService now takes PlaylistTrack (Decoupled).
                // Phase 3B: Pass Blacklisted users for Health Monitor retries
                var bestMatch = await _discoveryService.FindBestMatchAsync(ctx.Model, trackCt, ctx.BlacklistedUsers);

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
                // Fix #3: Preemption-aware cancellation handling
                // If this was a background (Priority 10+) download that was preempted,
                // defer it back to Pending rather than marking as Cancelled
                if (ctx.Model.Priority >= 10 && ctx.State == PlaylistTrackState.Downloading)
                {
                    _logger.LogInformation("⏸ Download preempted for high-priority work: {Title} - deferring to queue", ctx.Model.Title);
                    await UpdateStateAsync(ctx, PlaylistTrackState.Deferred, "Deferred for high-priority downloads");
                    return;
                }
                
                // User-initiated pause or cancellation
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
        
        // Phase 3B: Track current peer for Health Monitor blacklisting
        ctx.CurrentUsername = bestMatch.Username;

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
            var diskBytes = new FileInfo(partPath).Length;
            
            // Phase 3A: Atomic Handshake - Trust Journal, Truncate Disk
            var confirmedBytes = await _crashJournal.GetConfirmedBytesAsync(ctx.GlobalId);
            long expectedSize = bestMatch.Size ?? 0;

            // Fix: Ghost File Race Condition Check
            // If file is fully downloaded on disk but journal says 99% (crash during finalization),
            // TRUST THE DISK. Do not truncate. Verification step will validate integrity.
            if (expectedSize > 0 && diskBytes >= expectedSize)
            {
                startPosition = diskBytes;
                _logger.LogInformation("👻 Ghost File Detected: Disk ({Disk}) >= Expected ({Expected}). Skipping truncation despite Journal ({Journal}).", 
                    diskBytes, expectedSize, confirmedBytes);
            }
            else if (confirmedBytes > 0 && diskBytes > confirmedBytes)
            {
                // Case 1: Disk has more data than journal (unconfirmed tail)
                // Truncate to confirmed bytes to ensure no corrupt/torn data is kept
                _logger.LogWarning("⚠️ Atomic Resume: Truncating {Diff} bytes of unconfirmed data for {Track}", 
                    diskBytes - confirmedBytes, ctx.Model.Title);
                    
                using (var fs = File.OpenWrite(partPath))
                {
                    fs.SetLength(confirmedBytes);
                }
                startPosition = confirmedBytes;
            }
            else
            {
                // Case 2: Disk <= Journal, or no Journal entry (clean shutdown/new)
                // Resume from what we physically have
                startPosition = diskBytes;
            }

            ctx.IsResuming = true;
            ctx.BytesReceived = startPosition;
            
            _logger.LogInformation("Resuming download from byte {Position} for {Track} (Journal Confirmed: {Confirmed})", 
                startPosition, ctx.Model.Title, confirmedBytes);
        }
        else
        {
            ctx.IsResuming = false;
            ctx.BytesReceived = 0;
        }

        // STEP 3: Set total bytes for progress tracking
        ctx.TotalBytes = bestMatch.Size ?? 0;  // Handle nullable size

        // Phase 2A: CHECKPOINT LOGGING - Log before download starts
        var checkpointState = new DownloadCheckpointState
        {
            TrackGlobalId = ctx.GlobalId,
            Artist = ctx.Model.Artist,
            Title = ctx.Model.Title,
            SoulseekUsername = bestMatch.Username!,
            SoulseekFilename = bestMatch.Filename!,
            ExpectedSize = bestMatch.Size ?? 0,
            PartFilePath = partPath,
            FinalPath = finalPath,
            BytesDownloaded = startPosition // Start with existing progress if resuming
        };

        var checkpoint = new RecoveryCheckpoint
        {
            Id = ctx.GlobalId, // CRITICAL: Use TrackGlobalId to prevent duplicates on retry
            OperationType = OperationType.Download,
            TargetPath = finalPath,
            StateJson = JsonSerializer.Serialize(checkpointState),
            Priority = 10 // High priority - active user download
        };

        string? checkpointId = await _crashJournal.LogCheckpointAsync(checkpoint);
        _logger.LogDebug("✅ Download checkpoint logged: {Id} - {Artist} - {Title}", 
            checkpointId, ctx.Model.Artist, ctx.Model.Title);

        // Phase 2A: PERIODIC HEARTBEAT with stall detection
        using var heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        
        int stallCount = 0;
        long lastHeartbeatBytes = startPosition;
        
        var heartbeatTask = Task.Run(async () =>
        {
            try
            {
                while (await heartbeatTimer.WaitForNextTickAsync(heartbeatCts.Token))
                {
                    // Phase 3A: Finalization Guard - Stop heartbeat immediately if completion logic started
                    if (ctx.IsFinalizing) return;

                    var currentBytes = ctx.BytesReceived; // Thread-safe Interlocked read
                    
                    // STALL DETECTION: 4 heartbeats (1 minute) of no progress
                    if (currentBytes == lastHeartbeatBytes)
                    {
                        stallCount++;
                        if (stallCount >= 4)
                        {
                            _logger.LogWarning("⚠️ Download stalled for 1 minute: {Artist} - {Title} ({Current}/{Total} bytes)",
                                ctx.Model.Artist, ctx.Model.Title, currentBytes, checkpointState.ExpectedSize);
                            // Skip heartbeat update to save SSD writes
                            continue;
                        }
                    }
                    else
                    {
                        stallCount = 0; // Reset on progress
                    }

                    // PERFORMANCE: Only update if progress > 1KB to reduce SQLite overhead
                    if (currentBytes > 0 && currentBytes > lastHeartbeatBytes + 1024)
                    {
                        checkpointState.BytesDownloaded = currentBytes;
                        
                        // SSD OPTIMIZATION: Skip if no meaningful progress (built into UpdateHeartbeatAsync)
                        await _crashJournal.UpdateHeartbeatAsync(
                            checkpointId!,
                            JsonSerializer.Serialize(checkpointState), // Serialize in heartbeat thread
                            lastHeartbeatBytes,
                            currentBytes);
                        
                        lastHeartbeatBytes = currentBytes;
                        
                        _logger.LogTrace("Heartbeat: {Current}/{Total} bytes ({Percent}%)",
                            currentBytes, checkpointState.ExpectedSize, 
                            (currentBytes * 100.0 / checkpointState.ExpectedSize));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on download completion or cancellation
                _logger.LogDebug("Heartbeat cancelled for {GlobalId}", ctx.GlobalId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Heartbeat error for {GlobalId}", ctx.GlobalId);
            }
        }, heartbeatCts.Token);

        try
        {
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
                // Fix: Allow file to be slightly larger (metadata padding)
                // We rely on VerifyAudioFormatAsync later for actual integrity
                if (finalPartSize < bestMatch.Size)
                {
                    throw new InvalidDataException(
                        $"Downloaded file truncated. Expected {bestMatch.Size}, got {finalPartSize}");
                }

                // Clean up old final file if it exists (race condition edge case)
                if (File.Exists(finalPath))
                {
                    _logger.LogWarning("Final file already exists, overwriting: {Path}", finalPath);
                    // File.Delete is handled by MoveAtomicAsync logic (via WriteAtomicAsync)
                }

                // ATOMIC OPERATION: Use SafeWrite to move .part to .mp3
                var moveSuccess = await _fileWriteService.MoveAtomicAsync(partPath, finalPath);
                
                if (!moveSuccess)
                {
                     // If move failed (e.g. disk full during copy phase), throw execution to trigger retry/fail logic
                     throw new IOException($"Failed to atomically move file from {partPath} to {finalPath}");
                }
                
                _logger.LogInformation("Atomic move complete: {Part} → {Final}", 
                    Path.GetFileName(partPath), Path.GetFileName(finalPath));

                // Phase 1A: POST-DOWNLOAD VERIFICATION
                // Verify the downloaded file is valid before adding to library
                try
                {
                    _logger.LogDebug("Verifying downloaded file: {Path}", finalPath);
                    
                    // STEP 1: Verify audio format (ensures file can be opened and has valid properties)
                    var isValidAudio = await SLSKDONET.Services.IO.FileVerificationHelper.VerifyAudioFormatAsync(finalPath);
                    if (!isValidAudio)
                    {
                        _logger.LogWarning("Downloaded file failed audio format verification: {Path}", finalPath);
                        
                        // Delete corrupt file
                        File.Delete(finalPath);
                        
                        // Mark as failed with specific error
                        await UpdateStateAsync(ctx, PlaylistTrackState.Failed, 
                            "File verification failed: Invalid audio format or corrupted file");
                        return;
                    }
                    
                    // STEP 2: Verify minimum file size (prevents 0-byte or tiny corrupt files)
                    var isValidSize = await SLSKDONET.Services.IO.FileVerificationHelper.VerifyFileSizeAsync(finalPath, 10 * 1024); // 10KB minimum
                    if (!isValidSize)
                    {
                        _logger.LogWarning("Downloaded file too small (< 10KB): {Path}", finalPath);
                        
                        // Delete invalid file
                        File.Delete(finalPath);
                        
                        // Mark as failed
                        await UpdateStateAsync(ctx, PlaylistTrackState.Failed, 
                            "File verification failed: File too small or empty");
                        return;
                    }
                    
                    _logger.LogInformation("✅ File verification passed: {Path}", finalPath);
                }
                catch (Exception verifyEx)
                {
                    _logger.LogError(verifyEx, "File verification error for {Path}", finalPath);
                    
                    // If verification crashes, treat as corrupt and clean up
                    try { File.Delete(finalPath); } catch { }
                    
                    await UpdateStateAsync(ctx, PlaylistTrackState.Failed, 
                        $"File verification error: {verifyEx.Message}");
                    return;
                }

                ctx.Model.ResolvedFilePath = finalPath;
                ctx.Progress = 100;
                ctx.BytesReceived = bestMatch.Size ?? 0;  // Handle nullable size
                await UpdateStateAsync(ctx, PlaylistTrackState.Completed);

                // Phase 2A: Complete checkpoint on success
                if (checkpointId != null)
                {
                    // Phase 3A: Sentinel Flag - Prevent heartbeat from re-creating checkpoint
                    ctx.IsFinalizing = true;
                    
                    await _crashJournal.CompleteCheckpointAsync(checkpointId);
                    _logger.LogDebug("✅ Download checkpoint completed: {Id}", checkpointId);
                }

                // CRITICAL: Create LibraryEntry for global index (enables All Tracks view + cross-project deduplication)
                var libraryEntry = new LibraryEntry
                {
                    UniqueHash = ctx.Model.TrackUniqueHash,
                    Artist = ctx.Model.Artist,
                    Title = ctx.Model.Title,
                    Album = ctx.Model.Album ?? "Unknown",
                    FilePath = finalPath,
                    Format = Path.GetExtension(finalPath).TrimStart('.'),
                    Bitrate = bestMatch.Bitrate
                };
                await _libraryService.SaveOrUpdateLibraryEntryAsync(libraryEntry);
                _logger.LogInformation("📚 Added to library: {Artist} - {Title}", ctx.Model.Artist, ctx.Model.Title);

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
    finally
    {
        // Phase 2A: CRITICAL CLEANUP - Stop heartbeat timer
        heartbeatCts.Cancel(); // Signal heartbeat to stop
        heartbeatTimer.Dispose();
        
        try
        {
            await heartbeatTask; // Wait for heartbeat task to complete
        }
        catch (OperationCanceledException)
        {
            // Expected when heartbeat is cancelled
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error waiting for heartbeat task cleanup");
        }
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
            // Phase 3C Hardening: Enforce Priority 0 (Express Lane) and persistence
            ctx.Model.Priority = 0;
            // Persist valid priority for restart resilience
            await _databaseService.UpdatePlaylistTrackPriorityAsync(ctx.Model.Id, 0); 
            
            // Allow loop to pick it up naturally (respecting semaphore)
            await UpdateStateAsync(ctx, PlaylistTrackState.Pending);
            
            // Check if we need to preempt immediately (wake up loop)
            // The loop runs every 500ms when idle, so latent pickup is fast.
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

            // 3. Set High Priority and Queue
            ctx.Model.Priority = 0;
            await _databaseService.UpdatePlaylistTrackPriorityAsync(ctx.Model.Id, 0); 

            await UpdateStateAsync(ctx, PlaylistTrackState.Pending);
        });
    }

    private void OnUpgradeAvailable(UpgradeAvailableEvent e)
    {
        // For now just log, could trigger a notification in future
        _logger.LogInformation("Upgrade Available (Manual Approval Needed): {TrackId} - {BestMatch}", 
            e.TrackGlobalId, e.BestMatch.Filename);
    }

    // ========================================
    // Phase 3C: Multi-Lane Priority Engine
    // ========================================

    private const int HIGH_PRIORITY_SLOTS = 2;
    private const int STANDARD_PRIORITY_SLOTS = 2;

    /// <summary>
    /// Gets count of active downloads grouped by priority level.
    /// Returns dictionary: Priority -> Count
    /// </summary>
    private Dictionary<int, int> GetActiveDownloadsByPriority()
    {
        var activeDownloads = _downloads
            .Where(d => d.State == PlaylistTrackState.Searching || d.State == PlaylistTrackState.Downloading)
            .GroupBy(d => d.Model.Priority)
            .ToDictionary(g => g.Key, g => g.Count());

        return activeDownloads;
    }

    /// <summary>
    /// Selects next track respecting lane allocation limits.
    /// Lane A (Priority 0): 2 slots max
    /// Lane B (Priority 1): 2 slots max  
    /// Lane C (Priority 10+): Remaining slots
    /// </summary>
    private DownloadContext? SelectNextTrackWithLaneAllocation(
        List<DownloadContext> eligibleTracks,
        Dictionary<int, int> activeByPriority)
    {
        // Sort by Priority (ascending), then AddedAt (FIFO within priority)
        var sortedTracks = eligibleTracks
            .OrderBy(t => t.Model.Priority)
            .ThenBy(t => t.Model.AddedAt)
            .ToList();

        foreach (var track in sortedTracks)
        {
            var priority = track.Model.Priority;
            var currentCount = activeByPriority.GetValueOrDefault(priority, 0);

            // Check lane limits
            if (priority == 0) // High Priority
            {
                if (currentCount < HIGH_PRIORITY_SLOTS)
                {
                    _logger.LogDebug("Selected High Priority track: {Title} (Lane A: {Count}/2)",
                        track.Model.Title, currentCount + 1);
                    return track;
                }
            }
            else if (priority == 1) // Standard
            {
                if (currentCount < STANDARD_PRIORITY_SLOTS)
                {
                    _logger.LogDebug("Selected Standard Priority track: {Title} (Lane B: {Count}/2)",
                        track.Model.Title, currentCount + 1);
                    return track;
                }
            }
            else // Background (Priority 10+)
            {
                // Background can use any remaining slots
                var totalActive = activeByPriority.Values.Sum();
                if (totalActive < 4) // MAX_CONCURRENT_DOWNLOADS
                {
                    _logger.LogDebug("Selected Background Priority track: {Title} (Lane C)",
                        track.Model.Title);
                    return track;
                }
            }
        }

        return null; // All lanes at capacity
    }

    /// <summary>
    /// Prioritizes all tracks from a specific project by bumping to Priority 0 (High).
    /// Phase 3C: The "VIP Pass" - allows user to jump queue with specific playlist.
    /// Hardening Fix #1: Now persists to database for crash resilience.
    /// </summary>
    public async Task PrioritizeProjectAsync(Guid playlistId)
    {
        _logger.LogInformation("🚀 Prioritizing project: {PlaylistId}", playlistId);

        // Fix #1: Persist to database FIRST for crash resilience
        await _databaseService.UpdatePlaylistTracksPriorityAsync(playlistId, 0);
        
        // Update in-memory contexts
        int updatedCount = 0;
        lock (_collectionLock)
        {
            foreach (var download in _downloads.Where(d => d.Model.PlaylistId == playlistId && d.State == PlaylistTrackState.Pending))
            {
                download.Model.Priority = 0;
                updatedCount++;
            }
        }

        _logger.LogInformation("✅ Prioritized {Count} tracks from project {PlaylistId} (database + in-memory)",
            updatedCount, playlistId);
    }

    /// <summary>
    /// Pauses the lowest priority active download to free a slot for high-priority track.
    /// Phase 3C: Preemption support.
    /// </summary>
    private async Task PauseLowestPriorityDownloadAsync()
    {
        DownloadContext? lowestPriority = null;

        lock (_collectionLock)
        {
            lowestPriority = _downloads
                .Where(d => d.State == PlaylistTrackState.Downloading || d.State == PlaylistTrackState.Searching)
                .OrderByDescending(d => d.Model.Priority) // Highest priority value = lowest priority
                .ThenBy(d => d.Model.AddedAt)
                .FirstOrDefault();
        }

        if (lowestPriority != null && lowestPriority.Model.Priority > 0) // Preempt anything lower than High Priority (0)
        {
            _logger.LogInformation("⏸ Preempting lower priority download (Priority {Prio}): {Title}", 
                lowestPriority.Model.Priority, lowestPriority.Model.Title);
            await PauseTrackAsync(lowestPriority.Model.TrackUniqueHash);
        }
    }


    public void Dispose()
    {
        _globalCts.Cancel();
        _globalCts.Dispose();
        _processingTask?.Wait();
        _enrichmentOrchestrator.Dispose();
    }
}

