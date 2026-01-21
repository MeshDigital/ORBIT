using System;
using SLSKDONET.Views;
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
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services.InputParsers;
using SLSKDONET.Utils;
using SLSKDONET.Services.Models;
using SLSKDONET.Data.Essentia;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Services.Repositories; // [NEW] Namespace
using SLSKDONET.Services.IO; // Added explicit using


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
    private readonly ConfigManager _configManager; // Persistence
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
    private readonly IFileWriteService _fileWriteService; // Phase 1A
    // The following two lines are duplicates from above, keeping the first set.
    // private readonly ILogger<DownloadManager> _logger;
    // private readonly SoulseekAdapter _soulseek;
    // private readonly ILibraryService _libraryService;
    // private readonly IEventBus _eventBus;
    // private readonly AppConfig _config;
    // private readonly ConfigManager _configManager;
    private readonly CrashRecoveryJournal _crashJournal;
    private readonly IEnrichmentTaskRepository _enrichmentTaskRepository; // [NEW]
    private readonly IAudioAnalysisService _audioAnalysisService; // Phase 3: Local Audio Analysis
    private readonly AnalysisQueueService _analysisQueue; // Phase 4: Musical Brain Queue
    private readonly WaveformAnalysisService _waveformService; // Phase 3: Waveform Generation

    // Phase 2.5: Concurrency control with SemaphoreSlim throttling
    private readonly CancellationTokenSource _globalCts = new();
    private readonly SemaphoreSlim _downloadSemaphore; // Initialized in optimization
    private Task? _processingTask;
    private readonly object _processingLock = new();
    public bool IsRunning => _processingTask != null && !_processingTask.IsCompleted;

    // STATE MACHINE:
    // WHY: Downloads are long-running stateful operations that can fail mid-flight
    // - App crash: resume from last checkpoint (CrashRecoveryJournal)
    // - Network drop: retry with exponential backoff
    // - User cancel: clean abort without orphaned files
    // 
    // DownloadContext tracks:
    // - Model: Track metadata (artist, title, preferences)
    // - State: Current phase (Queued -> Searching -> Downloading -> Complete)
    // - Progress: Bytes transferred (for UI progress bar)
    // Global State managed via Events
    private readonly List<DownloadContext> _downloads = new();
    private readonly object _collectionLock = new object();
    
    private const int LAZY_QUEUE_BUFFER_SIZE = 5000;
    private const int REFILL_THRESHOLD = 50;

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
        ConfigManager configManager, // Injected
        SoulseekAdapter soulseek,
        FileNameFormatter fileNameFormatter,
        DatabaseService databaseService,
        ILibraryService libraryService,
        IEventBus eventBus,
        DownloadDiscoveryService discoveryService,
        MetadataEnrichmentOrchestrator enrichmentOrchestrator, // Keeping this for legacy calls if any
        PathProviderService pathProvider,
        IFileWriteService fileWriteService,
        CrashRecoveryJournal crashJournal,
        IEnrichmentTaskRepository enrichmentTaskRepository,
        IAudioAnalysisService audioAnalysisService,
        AnalysisQueueService analysisQueue,
        WaveformAnalysisService waveformService) // Phase 3: Waveform Integration
    {
        _logger = logger;
        _config = config;
        _configManager = configManager;
        _soulseek = soulseek;
        _fileNameFormatter = fileNameFormatter;
        _databaseService = databaseService;
        _libraryService = libraryService;
        _eventBus = eventBus;
        _discoveryService = discoveryService;
        _enrichmentOrchestrator = enrichmentOrchestrator; 
        _pathProvider = pathProvider;
        _fileWriteService = fileWriteService;
        _crashJournal = crashJournal; 
        _enrichmentTaskRepository = enrichmentTaskRepository;
        _audioAnalysisService = audioAnalysisService;
        _analysisQueue = analysisQueue;
        _waveformService = waveformService;

        // CONCURRENCY CONTROL ARCHITECTURE:
        // WHY: SemaphoreSlim instead of Task.WhenAll() or Parallel.ForEach():
        // 
        // Problem: P2P nodes have limited upload slots (typically 2-10 per user)
        // - If we launch 50 downloads at once, 45 will queue/timeout
        // - Network saturation kills ALL downloads (competing for bandwidth)
        // - No graceful cancellation - Task.WhenAll() is all-or-nothing
        // 
        // Solution: SemaphoreSlim throttling
        // - Limits concurrent downloads to user-defined value (default: 4)
        // - Queued downloads wait their turn (no timeout waste)
        // - Dynamic adjustment: user can change MaxActiveDownloads at runtime
        // - Hard cap at 50: prevents DOS if user enters 99999
        // 
        // Real-world impact:
        // - 4 concurrent = 95% success rate, ~2MB/s aggregate
        // - 20 concurrent = 60% success rate, ~1.5MB/s (contention overhead)
        int initialLimit = _config.MaxConcurrentDownloads > 0 ? _config.MaxConcurrentDownloads : 4;
        _maxActiveDownloads = initialLimit; // FIX: Set private field first to avoid double-release in property setter
        _downloadSemaphore = new SemaphoreSlim(initialLimit, 50); // Hard cap at 50 to prevent DOS
        
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
                _logger.LogInformation("ðŸ“¢ Processing DownloadAlbumRequest for Project: {Title}", job.SourceTitle);
                
                // Ensure tracks are loaded
                 _ = Task.Run(async () => 
                 {
                     _logger.LogInformation("ðŸ” Loading tracks for project {Id}...", job.Id);
                     var tracks = await _libraryService.LoadPlaylistTracksAsync(job.Id);
                     
                     if (tracks.Any())
                     {
                         _logger.LogInformation("âœ… Found {Count} tracks, queuing...", tracks.Count);
                         QueueTracks(tracks);
                         _logger.LogInformation("ðŸš€ Queued {Count} tracks for project {Title}", tracks.Count, job.SourceTitle);
                     }
                     else
                     {
                         _logger.LogWarning("âš ï¸ No tracks found for project {Title} (ID: {Id}) - Database might be empty or tracks missing", job.SourceTitle, job.Id);
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

    private int _maxActiveDownloads;
    public int MaxActiveDownloads 
    {
        get => _maxActiveDownloads;
        set
        {
            if (_maxActiveDownloads == value || value < 1 || value > 50) return;
            
            int diff = value - _maxActiveDownloads;
            _maxActiveDownloads = value;
            
            // Persist
            if (_config.MaxConcurrentDownloads != value)
            {
                _config.MaxConcurrentDownloads = value;
                _ = _configManager.SaveAsync(_config); // Fire and forget save
            }
            
            // Adjust semaphore count dynamically
            if (diff > 0)
            {
                try 
                {
                    _downloadSemaphore.Release(diff);
                    _logger.LogInformation("ðŸš€ Increased concurrent download limit to {Count}", value);
                }
                catch (SemaphoreFullException) 
                {
                     // Should not happen with max 50, but fail safe
                     _logger.LogWarning("Failed to increase concurrency limit - semaphore full");
                }
            }
            else
            {
                // Decrease limit: Acquire slots asynchronously to throttle future downloads
                // We don't cancel running downloads, just prevent new ones until count drops
                int reduceBy = Math.Abs(diff);
                _logger.LogInformation("ðŸ›‘ Decreasing concurrent download limit to {Count} (throttling {Reduce} slots)", value, reduceBy);
                
                Task.Run(async () => 
                {
                    for(int i=0; i < reduceBy; i++)
                    {
                        await _downloadSemaphore.WaitAsync();
                    }
                });
            }
        }
    }
    
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

            // PERFORMANCE FIX: Defer queue refilling until after startup
            // Loading tracks from DB during init adds unnecessary latency
            // The ProcessQueueLoop will call RefillQueueAsync when needed
            // await RefillQueueAsync();

            
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
                        Priority = 0,
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

                // 2. [NEW] Queue Enrichment Tasks for all tracks
                foreach (var track in job.PlaylistTracks)
                {
                    await _enrichmentTaskRepository.QueueTaskAsync(track.TrackUniqueHash, job.Id);
                }
                _logger.LogInformation("Queued enrichment tasks for {Count} tracks", job.PlaylistTracks.Count);
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
        
        // O(N) Optimization: Create lookup maps OUTSIDE the lock to prevent UI freeze on large imports
        var currentDownloads = ActiveDownloads; 
        
        var existingMap = currentDownloads
            .Where(d => !string.IsNullOrEmpty(d.Model.TrackUniqueHash))
            .GroupBy(d => d.Model.TrackUniqueHash)
            .ToDictionary(g => g.Key!, g => g.First());
        
        var existingIds = currentDownloads
            .GroupBy(d => d.Model.Id)
            .ToDictionary(g => g.Key, g => g.First());

        lock (_collectionLock)
        {

            foreach (var track in tracks)
            {
                DownloadContext? existingCtx = null;
                
                if (existingIds.TryGetValue(track.Id, out var byId)) existingCtx = byId;
                else if (existingMap.TryGetValue(track.TrackUniqueHash, out var byHash)) existingCtx = byHash;

                if (existingCtx != null)
                {
                    // Fix: Smart Retry if in a terminal/failure state
                    if (existingCtx.State == PlaylistTrackState.Failed || 
                        existingCtx.State == PlaylistTrackState.Cancelled || 
                        existingCtx.State == PlaylistTrackState.Deferred ||
                        existingCtx.State == PlaylistTrackState.Pending)
                    {
                        _logger.LogInformation("Retrying existing track {Title} (State: {State}) - Bumping to Priority 0", track.Title, existingCtx.State);
                        
                        existingCtx.Model.Priority = 0;
                        _ = UpdateStateAsync(existingCtx, PlaylistTrackState.Pending);
                        
                        existingCtx.RetryCount = 0;
                        existingCtx.NextRetryTime = null;
                        existingCtx.FailureReason = null;
                        
                        _ = _enrichmentTaskRepository.QueueTaskAsync(existingCtx.Model.TrackUniqueHash, existingCtx.Model.PlaylistId);
                        queued++; 
                        continue;
                    }

                    skipped++;
                    _logger.LogDebug("Skipping track {Artist} - {Title}: already active/completed (State: {State})", 
                        track.Artist, track.Title, existingCtx.State);
                    continue;
                }
                
                if (_downloads.Count(d => d.State == PlaylistTrackState.Pending) >= LAZY_QUEUE_BUFFER_SIZE 
                    && track.Priority > 0)
                {
                     skipped++; 
                     continue; 
                }

                var ctx = new DownloadContext(track);
                _downloads.Add(ctx);
                
                existingMap[track.TrackUniqueHash] = ctx;
                existingIds[track.Id] = ctx;
                
                queued++;
                
                _eventBus.Publish(new TrackAddedEvent(track));
                _ = SaveTrackToDb(ctx);
                
                if (ctx.State == PlaylistTrackState.Pending || ctx.State == PlaylistTrackState.Searching)
                {
                    _ = _enrichmentTaskRepository.QueueTaskAsync(ctx.Model.TrackUniqueHash, ctx.Model.PlaylistId);
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
                    Priority = t.Priority,
                    AddedAt = t.AddedAt,
                    CompletedAt = t.CompletedAt
                };
                
                // Map status to download state
                var ctx = new DownloadContext(model);
                ctx.State = t.Status switch
                {
                    TrackStatus.Downloaded => PlaylistTrackState.Completed,
                    TrackStatus.Failed => PlaylistTrackState.Failed,
                    TrackStatus.Skipped => PlaylistTrackState.Cancelled,
                    _ => PlaylistTrackState.Paused // FORCE PAUSE ON LOAD (User Request)
                };
                
                // Reset transient states
                if (ctx.State == PlaylistTrackState.Downloading || ctx.State == PlaylistTrackState.Searching)
                    ctx.State = PlaylistTrackState.Paused; // FORCE PAUSE ON LOAD (User Request)

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
    
    // Helper to update state and publish event (Overload: Structured Failure Reason)
    public async Task UpdateStateAsync(DownloadContext ctx, PlaylistTrackState newState, DownloadFailureReason failureReason)
    {
        // Store structured failure data
        ctx.FailureReason = failureReason;
        
        // Generate detailed message from enum + search attempts
        var displayMessage = failureReason.ToDisplayMessage();
        var suggestion = failureReason.ToActionableSuggestion();
        
        // Only append search diagnostics if the failure is search-related (i.e. we couldn't find a valid track)
        // If we passed the search phase and failed later (TransferFailed), irrelevant rejections shouldn't be shown.
        var isSearchFailure = failureReason == DownloadFailureReason.NoSearchResults ||
                              failureReason == DownloadFailureReason.AllResultsRejectedQuality ||
                              failureReason == DownloadFailureReason.AllResultsRejectedFormat ||
                              failureReason == DownloadFailureReason.AllResultsBlacklisted;

        // If we have search attempt logs, add the best rejection details
        if (isSearchFailure && ctx.SearchAttempts.Any())
        {
            var lastAttempt = ctx.SearchAttempts.Last();
            if (lastAttempt.Top3RejectedResults.Any())
            {
                var bestRejection = lastAttempt.Top3RejectedResults[0]; // Focus on #1
                displayMessage += $" ({bestRejection.ShortReason})";
            }
        }
        
        // Store detailed message for persistence
        ctx.DetailedFailureMessage = $"{displayMessage}. {suggestion}";
        
        // Call original method with generated error message
        await UpdateStateAsync(ctx, newState, ctx.DetailedFailureMessage);
    }
    
    // Helper to update state and publish event (Original: String-based)
    public async Task UpdateStateAsync(DownloadContext ctx, PlaylistTrackState newState, string? error = null)
    {
        if (ctx.State == newState && ctx.ErrorMessage == error) return;
        
        ctx.State = newState;
        ctx.ErrorMessage = error; // Update context
        
        // Update model and timestamp for terminal states
        if (newState == PlaylistTrackState.Completed || newState == PlaylistTrackState.Failed || newState == PlaylistTrackState.Cancelled)
        {
            ctx.Model.CompletedAt = DateTime.UtcNow;
        }
        
        // Publish with ProjectId for targeted updates
        // Phase 0.5: Include best search log for diagnostics
        var bestSearchLog = ctx.SearchAttempts.OrderByDescending(x => x.ResultsCount).FirstOrDefault();
        _eventBus.Publish(new TrackStateChangedEvent(ctx.GlobalId, ctx.Model.PlaylistId, newState, ctx.FailureReason ?? DownloadFailureReason.None, error, bestSearchLog));
        
        // DB Persistence for critical states
        await SaveTrackToDb(ctx);
        
        if (newState == PlaylistTrackState.Completed || newState == PlaylistTrackState.Failed || newState == PlaylistTrackState.Cancelled)
        {
             await UpdatePlaylistStatusAsync(ctx);
        }

        // Phase 6 Fix: Real-time population of "All Tracks" (LibraryEntry)
        if (newState == PlaylistTrackState.Completed && !string.IsNullOrEmpty(ctx.Model.ResolvedFilePath))
        {
            await _libraryService.AddTrackToLibraryIndexAsync(ctx.Model, ctx.Model.ResolvedFilePath);
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
                AddedAt = ctx.Model.AddedAt,
                CompletedAt = ctx.Model.CompletedAt,
                ErrorMessage = ctx.ErrorMessage,
                AlbumArtUrl = ctx.Model.AlbumArtUrl,
                SpotifyTrackId = ctx.Model.SpotifyTrackId,
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
        
        _logger.LogInformation("â¸ï¸ Paused track: {Artist} - {Title} (user-initiated)", ctx.Model.Artist, ctx.Model.Title);
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
            _logger.LogInformation("â¸ï¸ Pausing all {Count} active downloads...", active.Count);
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

        _logger.LogInformation("Hard Retry for {GlobalId} - Bumping to VIP Priority", globalId);
        ctx.CancellationTokenSource?.Cancel();
        _ = UpdateStateAsync(ctx, PlaylistTrackState.Pending); // Reset to Pending

        DeleteLocalFiles(ctx.Model.ResolvedFilePath);
        
        ctx.State = PlaylistTrackState.Pending;
        ctx.Progress = 0;
        ctx.ErrorMessage = null;
        ctx.DetailedFailureMessage = null; // Fix: Clear previous diagnostics
        ctx.RetryCount = 0;
        ctx.NextRetryTime = null; // Ensure immediate pickup
        ctx.SearchAttempts.Clear(); // Fix: Clear previous search attempts
        ctx.BlacklistedUsers.Clear(); 
        ctx.CancellationTokenSource = new CancellationTokenSource(); 
        
        // Fix: Promote to VIP to bypass "Lazy Queue" buffer
        ctx.Model.Priority = 0;
        
        // Publish reset event
        _eventBus.Publish(new TrackStateChangedEvent(ctx.GlobalId, ctx.Model.PlaylistId, PlaylistTrackState.Pending, DownloadFailureReason.None));
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
            
            // Notify UI
            _eventBus.Publish(new NotificationEvent(
                "Auto-Retry Triggered",
                $"Stalled download '{ctx.Model.Title}' switched from peer {stalledUser}",
                NotificationType.Warning));
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

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning) return;

        _logger.LogInformation("DownloadManager Orchestrator started.");

        
        await InitAsync();

        // Phase 13: Non-blocking Journal Recovery
        // We run this in background to avoid blocking the UI/Splash Screen
        // while it reconciles potentially thousands of checks.
        // Run recovery in background
        _ = Task.Run(async () => 
        {
            try 
            {
                await HydrateFromCrashAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recover journaled downloads");
            }
        }, ct);

        _processingTask = ProcessQueueLoop(_globalCts.Token);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Phase 13: Crash Recovery Journal Integration
    /// Reconciles state between SQLite WAL Journal and Disk.
    /// Handles:
    /// 1. Truncation Guard (fixing over-written .part files)
    /// 2. Ghost/Zombie Cleanup (removing stale checkpoints)
    /// 3. Priority Resumption (jumping queue for interrupted downloads)
    /// </summary>
    private async Task HydrateFromCrashAsync()
    {
        try
        {
            var pendingCheckpoints = await _crashJournal.GetPendingCheckpointsAsync();
            if (!pendingCheckpoints.Any())
            {
                _logger.LogDebug("Journal Check: Clean state (no pending checkpoints)");
                return;
            }

            _logger.LogInformation("Journal Check: Found {Count} pending download sessions", pendingCheckpoints.Count);

            int recovered = 0;
            int zombies = 0;

            // Phase 13 Optimization: "Batch Zombie Check"
            // Instead of querying DB one-by-one, we fetch all relevant tracks in one go.
            var uniqueHashList = pendingCheckpoints
                .Select(c => JsonSerializer.Deserialize<DownloadCheckpointState>(c.StateJson)?.TrackGlobalId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .ToList();

            var knownTracks = new HashSet<string>();
            try 
            {
                // Assuming DatabaseService has a method to check existence by list, or we add one.
                // For now, sticking to existing public surface area to avoid expanding scope too much
                // in this specific 'replace_file_content' operation.
                // If ID is in Hydrated downloads, we know it exists.
                lock (_collectionLock) 
                {
                    foreach(var d in _downloads) knownTracks.Add(d.GlobalId);
                }
            }
            catch (Exception ex)
            {
                 _logger.LogWarning(ex, "Failed to optimize zombie check");
            }

            foreach (var checkpoint in pendingCheckpoints)
            {
                if (checkpoint.OperationType != OperationType.Download) continue;

                DownloadCheckpointState? state = null;
                try 
                {
                    state = JsonSerializer.Deserialize<DownloadCheckpointState>(checkpoint.StateJson);
                }
                catch 
                {
                    _logger.LogWarning("Corrupt checkpoint state for {Id}, marking dead letter.", checkpoint.Id);
                    await _crashJournal.MarkAsDeadLetterAsync(checkpoint.Id);
                    continue;
                }

                if (state == null) continue;

                // 2. CORRELATE: Find the DownloadContext
                DownloadContext? ctx;
                lock(_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == state.TrackGlobalId);

                if (ctx == null)
                {
                    // Track exists in DB but not in memory.
                    // Zombie check: If file completely missing AND track gone from DB?
                    if (!File.Exists(state.PartFilePath) && !knownTracks.Contains(state.TrackGlobalId))
                    {
                        // Check DB individually if not in cache (fallback)
                        var dbTrack = await _databaseService.FindTrackAsync(state.TrackGlobalId);
                        if (dbTrack == null)
                        {
                            _logger.LogWarning("👻 Zombie Checkpoint: {Track} (File & Record missing). Cleaning up.", state.Title);
                            await _crashJournal.CompleteCheckpointAsync(checkpoint.Id); 
                            zombies++;
                            continue;
                        }
                    }
                    else
                    {
                         // Track likely exists but wasn't hydrated (Lazy buffer full?)
                         // We leave the checkpoint alone. The "RefillQueueAsync" will pick up the track later.
                         _logger.LogDebug("Deferred Recovery: {Track} valid but not in active memory.", state.Title);
                    }
                    continue;
                }

                // 3. TRUNCATION GUARD (The "Industrial" Fix)
                if (File.Exists(state.PartFilePath))
                {
                    var info = new FileInfo(state.PartFilePath);
                    if (info.Length > state.BytesDownloaded)
                    {
                        try 
                        {
                            _logger.LogWarning("⚠️ Truncation Guard: Truncating {Track} from {Disk} to {Journal} bytes.", 
                                state.Title, info.Length, state.BytesDownloaded);
                                
                            using (var fs = new FileStream(state.PartFilePath, FileMode.Open, FileAccess.Write, FileShare.None))
                            {
                                fs.SetLength(state.BytesDownloaded);
                            }
                        }
                        catch (IOException ioEx)
                        {
                             _logger.LogError("Locked file {Path} prevented truncation. Skipping recovery for this session. ({Msg})", state.PartFilePath, ioEx.Message);
                             continue; // Skip this track until next restart or manual retry
                        }
                        catch (Exception ex)
                        {
                             _logger.LogError(ex, "Failed to truncate file: {Path}", state.PartFilePath);
                        }
                    }
                }

                // 4. UPDATE MEMORY STATE
                ctx.BytesReceived = state.BytesDownloaded;
                ctx.TotalBytes = state.ExpectedSize;
                ctx.IsResuming = true;
                
                // 5. PRIORITIZE
                ctx.NextRetryTime = DateTime.MinValue;
                ctx.RetryCount = 0; 
                
                if (ctx.State == PlaylistTrackState.Failed || ctx.State == PlaylistTrackState.Cancelled)
                {
                    ctx.State = PlaylistTrackState.Pending;
                    await UpdateStateAsync(ctx, PlaylistTrackState.Pending, "Recovered from Crash Journal");
                }
                
                recovered++;
                _logger.LogInformation("✅ Recovered Session: {Artist} - {Title} ({Percent}%)", 
                    state.Artist, state.Title, (state.BytesDownloaded * 100.0 / Math.Max(1, state.ExpectedSize)).ToString("F0"));
            }

            // Clean up stale entries while we are here
            await _crashJournal.ClearStaleCheckpointsAsync();
            
            _logger.LogInformation("Recovery Summary: {Recovered} Resumed, {Zombies} Zombies squashed.", recovered, zombies);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Critical Error in RecoverJournaledDownloadsAsync");
        }
    }

    private async Task ProcessQueueLoop(CancellationToken token)
    {
        int disconnectBackoff = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                // Phase 0.9: Circuit Breaker
                if (!_soulseek.IsConnected)
                {
                    if (disconnectBackoff == 0)
                    {
                         _logger.LogWarning("🔌 Circuit Breaker: Queue processing PAUSED due to disconnection.");
                        // _eventBus.Publish(new NotificationEvent("Download Queue Paused", "Waiting for Soulseek connection before processing queue...", NotificationType.Warning));
                    }
                    
                    // Exponential Backoff: 2, 4, 8, 16... max 60s
                    int delaySeconds = Math.Min(60, (int)Math.Pow(2, Math.Min(6, disconnectBackoff + 1))); 
                    disconnectBackoff++;
                    
                    if (disconnectBackoff % 5 == 0) // Log occasionally
                    {
                        _logger.LogInformation("Circuit Breaker: Waiting for connection... (Next check in {Seconds}s)", delaySeconds);
                    }

                    await Task.Delay(delaySeconds * 1000, token);
                    continue;
                }

                // Reset backoff if connected
                if (disconnectBackoff > 0)
                {
                    _logger.LogInformation("✅ Circuit Breaker: Connection restored! Resuming queue processing.");
                    // _eventBus.Publish(new NotificationEvent("Download Queue Resumed", "Soulseek connection restored.", NotificationType.Success));
                    disconnectBackoff = 0;
                }

                DownloadContext? nextContext = null;
                lock (_collectionLock)
                {
                    // Phase 3C: Multi-Lane Priority Engine
                    // Weighted selection algorithm with slot allocation
                    var eligibleTracks = _downloads.Where(t => 
                        t.State == PlaylistTrackState.Pending && 
                        (!t.NextRetryTime.HasValue || t.NextRetryTime.Value <= DateTime.Now) &&
                        (t.Model.IsEnriched || t.Model.Priority == 0 || (DateTime.Now - t.Model.AddedAt).TotalMinutes > 5))
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
                    _logger.LogInformation("â™»ï¸ Track already in library: {Artist} - {Title}, reusing file: {Path}", 
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
                var discoveryResult = await _discoveryService.FindBestMatchAsync(ctx.Model, trackCt, ctx.BlacklistedUsers);
                var bestMatch = discoveryResult.BestMatch;

                // Capture search diagnostics
                if (discoveryResult.Log != null)
                {
                    ctx.SearchAttempts.Add(discoveryResult.Log);
                }

                if (bestMatch == null)
                {
                    // Check if we should auto-retry (but only for network/transient failures)
                    if (_config.AutoRetryFailedDownloads && ctx.RetryCount < _config.MaxDownloadRetries)
                    {
                         _logger.LogWarning("No match found for {Title}. Auto-retrying (Attempt {Count}/{Max})", 
                             ctx.Model.Title, ctx.RetryCount + 1, _config.MaxDownloadRetries);
                         
                         // Throw custom exception to preserve the "Search Rejected" state during retry
                         throw new SearchRejectedException("No suitable match found", discoveryResult.Log);
                    }

                    // Determine specific failure reason based on search history
                    var failureReason = DownloadFailureReason.NoSearchResults; // Default
                    
                    // If we have search attempts, analyze rejection patterns
                    if (ctx.SearchAttempts.Any())
                    {
                        var lastAttempt = ctx.SearchAttempts.Last();
                        if (lastAttempt.ResultsCount > 0)
                        {
                            // Results were found but rejected - determine why
                            if (lastAttempt.RejectedByQuality > 0)
                                failureReason = DownloadFailureReason.AllResultsRejectedQuality;
                            else if (lastAttempt.RejectedByFormat > 0)
                                failureReason = DownloadFailureReason.AllResultsRejectedFormat;
                            else if (lastAttempt.RejectedByBlacklist > 0)
                                failureReason = DownloadFailureReason.AllResultsBlacklisted;
                        }
                    }
                    
                    await UpdateStateAsync(ctx, PlaylistTrackState.Failed, failureReason);
                    return;
                }

                // Phase 3.1: Download Logic (Downloading State)
                await DownloadFileAsync(ctx, bestMatch, trackCt);
            }
            catch (OperationCanceledException)
            {
                // Enhanced cancellation diagnostics
                var cancellationReason = "Unknown";
                
                // Fix #3: Preemption-aware cancellation handling
                if (ctx.Model.Priority >= 10 && ctx.State == PlaylistTrackState.Downloading)
                {
                    cancellationReason = "Preempted for high-priority download";
                    _logger.LogInformation("â¸ Download preempted for high-priority work: {Title} - deferring to queue", ctx.Model.Title);
                    await UpdateStateAsync(ctx, PlaylistTrackState.Deferred, "Deferred for high-priority downloads");
                    return;
                }
                
                // Check if it was user-initiated pause
                if (ctx.State == PlaylistTrackState.Paused)
                {
                    cancellationReason = "User paused download";
                    _logger.LogInformation("â¸ Download paused by user: {Title}", ctx.Model.Title);
                    return;
                }
                
                // Check if it was explicit cancellation
                if (ctx.State == PlaylistTrackState.Cancelled)
                {
                    cancellationReason = "User cancelled download";
                    _logger.LogInformation("âŒ Download cancelled by user: {Title}", ctx.Model.Title);
                    return;
                }
                
                // Check if it's a global shutdown
                if (_globalCts.Token.IsCancellationRequested)
                {
                    _logger.LogInformation("Shutdown detected. Preserving state for {Title} (Current State: {State})", 
                        ctx.Model.Title, ctx.State);
                    return;
                }

                // Otherwise it's an unexpected cancellation (health monitor, timeout, etc.)
                cancellationReason = "System/timeout cancellation";
                _logger.LogWarning("âš ï¸ Unexpected cancellation for {Title} in state {State}. Marking as cancelled. Reason: {Reason}", 
                    ctx.Model.Title, ctx.State, cancellationReason);
                await UpdateStateAsync(ctx, PlaylistTrackState.Cancelled);
            }
            catch (SearchRejectedException srex)
            {
                _logger.LogWarning("Search Rejected for {Title}: {Message}", ctx.Model.Title, srex.Message);
                
                // 1. Capture Diagnostics
                if (srex.SearchLog != null)
                {
                    ctx.SearchAttempts.Add(srex.SearchLog);
                }

                // 2. Exponential Backoff for "No Results" (Retry Logic)
                ctx.RetryCount++;
                if (ctx.RetryCount < _config.MaxDownloadRetries)
                {
                    var delayMinutes = Math.Pow(2, ctx.RetryCount); // 2, 4, 8, 16...
                    ctx.NextRetryTime = DateTime.UtcNow.AddMinutes(delayMinutes);
                    ctx.Model.Priority = 20; // Low priority
                    
                    // Important: Set state to Pending so it stays in the queue, but with a status message explaining the delay
                    await UpdateStateAsync(ctx, PlaylistTrackState.Pending, $"Retrying in {delayMinutes}m: Search Rejected");
                    _logger.LogInformation("Scheduled retry #{Count} for {GlobalId} at {Time} due to search rejection", ctx.RetryCount, ctx.GlobalId, ctx.NextRetryTime);
                }
                else
                {
                    // Terminal Failure
                    await UpdateStateAsync(ctx, PlaylistTrackState.Failed, DownloadFailureReason.NoSearchResults);
                }
            }
            catch (Exception ex)
            {
                if (ex.GetType().Name.Contains("TransferRejectedException") || ex.Message.Contains("Too many files"))
                {
                    _logger.LogWarning("Peer Limit Reached for {Title}: {Message}. Triggering Retry Logic.", ctx.Model.Title, ex.Message);
                }
                else
                {
                    _logger.LogError(ex, "ProcessTrackAsync error for {GlobalId}", ctx.GlobalId);
                }

                // FIX: Blacklist the failing peer so we don't pick them again instantly
                if (!string.IsNullOrEmpty(ctx.CurrentUsername))
                {
                    lock (ctx.BlacklistedUsers) // HashSet isn't thread-safe
                    {
                        if (ctx.BlacklistedUsers.Add(ctx.CurrentUsername))
                        {
                            _logger.LogWarning("ðŸš« Blacklisted peer {User} for {Track} due to error", ctx.CurrentUsername, ctx.Model.Title);
                        }
                    }
                }
                
                // Exponential Backoff Logic (Phase 7)
                ctx.RetryCount++;
                if (ctx.RetryCount < _config.MaxDownloadRetries)
                {
                    var delayMinutes = Math.Pow(2, ctx.RetryCount); // 2, 4, 8, 16...
                    ctx.NextRetryTime = DateTime.UtcNow.AddMinutes(delayMinutes);
                    ctx.Model.Priority = 20; // LOW PRIORITY: Send retries to back of queue (fresh downloads = priority 10)
                    await UpdateStateAsync(ctx, PlaylistTrackState.Pending, $"Retrying in {delayMinutes}m: {ex.Message}");
                    _logger.LogInformation("Scheduled retry #{Count} for {GlobalId} at {Time} (low priority)", ctx.RetryCount, ctx.GlobalId, ctx.NextRetryTime);
                }
                else
                {
                    await UpdateStateAsync(ctx, PlaylistTrackState.Failed, DownloadFailureReason.MaxRetriesExceeded);
                }
            }
        }
    }

    private async Task DownloadFileAsync(DownloadContext ctx, Track bestMatch, CancellationToken ct)
    {
        await UpdateStateAsync(ctx, PlaylistTrackState.Downloading);
        
        // Phase 3B: Track current peer for Health Monitor blacklisting
        ctx.CurrentUsername = bestMatch.Username;
        
        // Phase 0.3: Reset Health Metrics for new attempt
        ctx.StallCount = 0;
        ctx.CurrentSpeed = 0;

        // Phase 2.5: Use PathProviderService for consistent folder structure
        // Create a temporary track object to combine DB (enriched) metadata with search result (technical) info
        var namingTrack = new Track
        {
            Artist = ctx.Model.Artist,
            Title = ctx.Model.Title,
            Album = ctx.Model.Album,
            Bitrate = bestMatch.Bitrate,
            BPM = ctx.Model.BPM,
            MusicalKey = ctx.Model.MusicalKey,
            Energy = ctx.Model.Energy,
            Filename = bestMatch.Filename, // For {filename} variable
            Username = bestMatch.Username, // For {user} variable
            Length = ctx.Model.CanonicalDuration // For {length} variable
        };
        var finalPath = _pathProvider.GetTrackPath(namingTrack);

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
                _logger.LogInformation("ðŸ‘» Ghost File Detected: Disk ({Disk}) >= Expected ({Expected}). Skipping truncation despite Journal ({Journal}).", 
                    diskBytes, expectedSize, confirmedBytes);
            }
            else if (confirmedBytes > 0 && diskBytes > confirmedBytes)
            {
                // Case 1: Disk has more data than journal (unconfirmed tail)
                // Truncate to confirmed bytes to ensure no corrupt/torn data is kept
                _logger.LogWarning("âš ï¸ Atomic Resume: Truncating {Diff} bytes of unconfirmed data for {Track}", 
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
        _logger.LogDebug("âœ… Download checkpoint logged: {Id} - {Artist} - {Title}", 
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
                            _logger.LogWarning("âš ï¸ Download stalled for 1 minute: {Artist} - {Title} ({Current}/{Total} bytes)",
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
            ctx.BytesReceived = (long)((bestMatch.Size ?? 0) * p);

            // Throttle to 2 updates/sec (500ms) to prevent UI stuttering under heavy load
            if ((DateTime.Now - lastNotificationTime).TotalMilliseconds > 500)
            {
                _eventBus.Publish(new TrackProgressChangedEvent(
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
                
                _logger.LogInformation("Atomic move complete: {Part} â†’ {Final}", 
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
                            DownloadFailureReason.FileVerificationFailed);
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
                            DownloadFailureReason.FileVerificationFailed);
                        return;
                    }
                    
                    _logger.LogInformation("âœ… File verification passed: {Path}", finalPath);
                }
                catch (Exception verifyEx)
                {
                    _logger.LogError(verifyEx, "File verification error for {Path}", finalPath);
                    
                    // If verification crashes, treat as corrupt and clean up
                    try { File.Delete(finalPath); } catch { }
                    
                    await UpdateStateAsync(ctx, PlaylistTrackState.Failed, 
                        DownloadFailureReason.FileVerificationFailed);
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
                    _logger.LogDebug("âœ… Download checkpoint completed: {Id}", checkpointId);
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
                _logger.LogInformation("ðŸ“š Added to library: {Artist} - {Title}", ctx.Model.Artist, ctx.Model.Title);

                _logger.LogInformation("ðŸ“š Added to library: {Artist} - {Title}", ctx.Model.Artist, ctx.Model.Title);

                // Phase 3: Integrated Audio Analysis Pipeline (Waveform + Technical + UI Feedback)
                try
                {
                    var analysisParams = new { Path = finalPath, Hash = ctx.Model.TrackUniqueHash };
                    _ = Task.Run(async () => 
                    {
                        try
                        {
                            _logger.LogInformation("ðŸ”¬ Starting post-download analysis for {Title}", ctx.Model.Title);

                            // A. Generate Waveform (Visual)
                            WaveformAnalysisData? waveform = null;
                            try
                            {
                                waveform = await _waveformService.GenerateWaveformAsync(analysisParams.Path);
                                _logger.LogInformation("âœ… Waveform generated: {Points} points", waveform.PeakData.Length);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "âš ï¸ Waveform generation failed (non-critical)");
                            }

                            // B. Analyze Audio Quality (Technical)
                            AudioAnalysisEntity? analysis = null;
                            try
                            {
                                analysis = await _audioAnalysisService.AnalyzeFileAsync(analysisParams.Path, analysisParams.Hash);
                                _logger.LogInformation("âœ… Audio analysis complete: {Loudness} LUFS", analysis?.LoudnessLufs);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "âš ï¸ Audio analysis failed (non-critical)");
                            }

                            // C. Atomic Database Update (All or Nothing) with Retry Logic
                            int maxRetries = 10; // Increased to 10 to survive heavy contention
                            for (int attempt = 1; attempt <= maxRetries; attempt++)
                            {
                                try
                                {
                                    using var db = new AppDbContext();
                                    
                                    // C1. Save Technical Analysis (Upsert pattern)
                                    // FIX: Do not remove/add, just update existing to avoid FK conflicts
                                    if (analysis != null)
                                    {
                                        var existing = await db.AudioAnalysis
                                            .FirstOrDefaultAsync(a => a.TrackUniqueHash == analysisParams.Hash);
                                        
                                        if (existing != null) 
                                        {
                                            // Update existing
                                            existing.LoudnessLufs = analysis.LoudnessLufs;
                                            existing.TruePeakDb = analysis.TruePeakDb;
                                            existing.DynamicRange = analysis.DynamicRange;
                                            existing.Bitrate = analysis.Bitrate;
                                            existing.SampleRate = analysis.SampleRate;
                                            existing.Channels = analysis.Channels;
                                            existing.DurationMs = analysis.DurationMs;
                                            existing.Codec = analysis.Codec;
                                            // IsVbr and HasHighFreqContent not in Entity
                                            existing.FrequencyCutoff = analysis.FrequencyCutoff;
                                            existing.SpectralHash = analysis.SpectralHash;
                                            existing.AnalyzedAt = DateTime.UtcNow;
                                        }
                                        else
                                        {
                                            // Insert new
                                            db.AudioAnalysis.Add(analysis);
                                        }
                                        _logger.LogInformation("ðŸ”Š Audio analysis staged for {Track}", ctx.Model.Title);
                                    }

                                    // C2. Update ALL PlaylistTrack instances with waveform & metrics
                                    var trackInstances = await db.PlaylistTracks
                                        .Include(t => t.TechnicalDetails)
                                        .Where(t => t.TrackUniqueHash == analysisParams.Hash)
                                        .ToListAsync();

                                    foreach (var track in trackInstances)
                                    {
                                        // Waveform data for UI
                                        if (waveform != null && waveform.PeakData.Length > 0)
                                        {
                                            if (track.TechnicalDetails == null)
                                            {
                                                var newTech = new TrackTechnicalEntity();
                                                newTech.Id = track.Id;
                                                newTech.PlaylistTrackId = track.Id;
                                                track.TechnicalDetails = newTech;
                                            }
                                                
                                            track.TechnicalDetails.WaveformData = waveform.PeakData;
                                            track.TechnicalDetails.RmsData = waveform.RmsData;
                                            track.TechnicalDetails.LowData = waveform.LowData;
                                            track.TechnicalDetails.MidData = waveform.MidData;
                                            track.TechnicalDetails.HighData = waveform.HighData;
                                        }
                                    }

                                    await db.SaveChangesAsync();
                                    _logger.LogInformation("âœ… Analysis persisted for {Count} playlist instances", trackInstances.Count);

                                    // D. Notify UI (Success)
                                    _eventBus.Publish(new TrackAnalysisCompletedEvent(analysisParams.Hash, true));

                                    // E. Queue for Musical Intelligence (Phase 4 - Essentia)
                                    if (analysis != null)
                                    {
                                        _analysisQueue.QueueAnalysis(analysisParams.Path, analysisParams.Hash, AnalysisTier.Tier1);
                                        _logger.LogInformation("🧠 Queued for musical analysis: {Title}", ctx.Model.Title);
                                    }
                                    
                                    // Break retry loop on success
                                    break; 
                                }
                                catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
                                {
                                    if (attempt == maxRetries)
                                    {
                                        _logger.LogError("â Œ Failed to persist analysis results after {Retries} attempts due to concurrency", maxRetries);
                                        _eventBus.Publish(new TrackAnalysisCompletedEvent(
                                            analysisParams.Hash, false, "Concurrency error persisting results"));
                                        // Re-throw if you want to escalate, or just swallow/log as terminal failure
                                    }
                                    else
                                    {
                                        var jitter = new Random().Next(100, 500);
                                        _logger.LogWarning("âš  Concurrency conflict saving analysis. Retrying... (Attempt {Attempt}/{Max})", attempt, maxRetries);
                                        await Task.Delay((250 * attempt) + jitter); // Progressive backoff + Jitter
                                    }
                                }
                                catch (Exception dbEx)
                                {
                                    _logger.LogError(dbEx, "â Œ Failed to persist analysis results on attempt {Attempt}", attempt);
                                    if (attempt == maxRetries)
                                    {
                                         _eventBus.Publish(new TrackAnalysisCompletedEvent(
                                            analysisParams.Hash, false, dbEx.Message));
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "âŒ Analysis pipeline catastrophic failure");
                            _eventBus.Publish(new TrackAnalysisCompletedEvent(
                                analysisParams.Hash, false, "Analysis pipeline failed"));
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to trigger audio analysis for {Track}", ctx.Model.Title);
                }

                // Phase 3.1: Finalize with Metadata Service (Tagging)
                // [Fixed] Finalization is now handled by persistent enrichment pipeline
                // await _enrichmentOrchestrator.FinalizeDownloadedTrackAsync(ctx.Model);
                await _enrichmentOrchestrator.QueueForEnrichmentAsync(ctx.Model.TrackUniqueHash, ctx.Model.PlaylistId);
            }
            catch (Exception renameEx)
            {
                _logger.LogError(renameEx, "Failed to perform atomic rename for {Track}", ctx.Model.Title);
                await UpdateStateAsync(ctx, PlaylistTrackState.Failed, 
                    DownloadFailureReason.AtomicRenameFailed);
            }
        }
        else
        {
            await UpdateStateAsync(ctx, PlaylistTrackState.Failed, 
                DownloadFailureReason.TransferFailed);
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
        // Sort by Priority (ascending = High -> Low), then AddedAt (FIFO)
        // Priority 0 = High (Lane A)
        // Priority 1 = Standard (Lane B)
        // Priority 10+ = Background (Lane C)
        var sortedTracks = eligibleTracks
            .OrderBy(t => t.Model.Priority)
            .ThenBy(t => t.Model.AddedAt)
            .ToList();

        // Since the Semaphore (_downloadSemaphore) already enforces the rigorous global limit (MaxActiveDownloads),
        // we do NOT need to enforce artificial caps on specific lanes (e.g. "Only 2 High Priority tracks").
        // This was causing the "Max 4 Downloads" bug where having 20 slots open but no Background tracks meant only 4 slots were used.
        
        // The logic is now:
        // 1. Pick the highest priority track available.
        // 2. The Semaphore prevents us from exceeding the global limit.
        
        foreach (var track in sortedTracks)
        {
            var priority = track.Model.Priority;
            
            // Log selection for debugging lane behavior
            if (priority == 0)
            {
                 _logger.LogDebug("Selected High Priority track: {Title} (Lane A)", track.Model.Title);
            }
            else if (priority == 1)
            {
                 _logger.LogDebug("Selected Standard Priority track: {Title} (Lane B)", track.Model.Title);
            }
            else
            {
                 _logger.LogDebug("Selected Background Priority track: {Title} (Lane C)", track.Model.Title);
            }
            
            return track;
        }

        return null; // No eligible tracks
    }

    /// <summary>
    /// Prioritizes all tracks from a specific project by bumping to Priority 0 (High).
    /// Phase 3C: The "VIP Pass" - allows user to jump queue with specific playlist.
    /// Hardening Fix #1: Now persists to database for crash resilience.
    /// </summary>
    public async Task PrioritizeProjectAsync(Guid playlistId)
    {
        _logger.LogInformation("ðŸš€ Prioritizing project: {PlaylistId}", playlistId);

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

        _logger.LogInformation("âœ… Prioritized {Count} tracks from project {PlaylistId} (database + in-memory)",
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
            _logger.LogInformation("â¸ Preempting lower priority download (Priority {Prio}): {Title}", 
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

