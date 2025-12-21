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

    // Concurrency control
    private readonly CancellationTokenSource _globalCts = new();
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
        MetadataEnrichmentOrchestrator enrichmentOrchestrator)
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
                _logger.LogInformation("Processing DownloadAlbumRequest for Project: {Title}", job.SourceTitle);
                // If it's a full project, we might need to load tracks if they aren't populated?
                // QueueProject handles this if OriginalTracks are present.
                // But usually this comes from Library where PlaylistTracks should be loaded?
                // If checking LibraryViewModel, DownloadAlbumCommand passes the Job.
                
                // Ensure tracks are loaded
                 _ = Task.Run(async () => 
                 {
                     var tracks = await _libraryService.LoadPlaylistTracksAsync(job.Id);
                     if (tracks.Any())
                     {
                         QueueTracks(tracks);
                         _logger.LogInformation("Queued {Count} tracks for project {Title}", tracks.Count, job.SourceTitle);
                     }
                     else
                     {
                         _logger.LogWarning("No tracks found for project {Title} (ID: {Id})", job.SourceTitle, job.Id);
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
                    
                    // Publish event
                    _eventBus.Publish(new Events.TrackAddedEvent(model));
                }
            }
            _logger.LogInformation("Hydrated {Count} tracks from database.", tracks.Count);
            
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
                return;
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
        lock (_collectionLock)
        {
            foreach (var track in tracks)
            {
                var ctx = new DownloadContext(track);
                _downloads.Add(ctx);
                
                // Publish Event
                _eventBus.Publish(new Events.TrackAddedEvent(track));
                
                // Persist new track
                _ = SaveTrackToDb(ctx);
                
                // Only queue enrichment if it's new/pending
                if (ctx.State == PlaylistTrackState.Pending || ctx.State == PlaylistTrackState.Searching)
                {
                    _enrichmentOrchestrator.QueueForEnrichment(ctx.Model);
                }
            }
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
            _eventBus.Publish(new Events.TrackRemovedEvent(globalId));
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
                SpotifyTrackId = ctx.Model.SpotifyTrackId
            });
        }  
        catch (Exception ex)
        {
            _logger.LogError(ex, "DB Save Failed");
        }
    }

    public void PauseTrack(string globalId)
    {
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(t => t.GlobalId == globalId);
        
        if (ctx != null)
        {
            ctx.CancellationTokenSource?.Cancel();
            _ = UpdateStateAsync(ctx, PlaylistTrackState.Paused);
            _logger.LogInformation("Paused track: {Artist} - {Title}", ctx.Model.Artist, ctx.Model.Title);
        }
    }

    public void ResumeTrack(string globalId)
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
                    nextContext = _downloads.FirstOrDefault(t => 
                        t.State == PlaylistTrackState.Pending && 
                        (!t.NextRetryTime.HasValue || t.NextRetryTime.Value <= DateTime.Now));
                }

                if (nextContext == null)
                {
                    await Task.Delay(500, token);
                    continue;
                }

                int currentActive = 0;
                lock (_collectionLock)
                {
                    currentActive = _downloads.Count(t => t.IsActive);
                }

                if (currentActive >= MaxActiveDownloads)
                {
                    await Task.Delay(1000, token);
                    continue;
                }

                if (nextContext.State != PlaylistTrackState.Pending) continue;

                // Transition state via update method
                await UpdateStateAsync(nextContext, PlaylistTrackState.Searching);

                // Start processing in background
                _ = Task.Run(async () => await ProcessTrackAsync(nextContext, token), token);
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
                if (ctx.RetryCount < 5)
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

        var finalPath = ctx.Model.ResolvedFilePath;
        if (string.IsNullOrEmpty(finalPath))
        {
            finalPath = Path.Combine(_config.DownloadDirectory ?? "Downloads",
                _fileNameFormatter.Format(_config.NameFormat ?? "{artist} - {title}", bestMatch) + $".{bestMatch.GetExtension()}");
        }

        var dir = Path.GetDirectoryName(finalPath);
        if (dir != null) Directory.CreateDirectory(dir);

        var progress = new Progress<double>(p => 
        {
            ctx.Progress = p * 100;
            // Publish Throttled Event? or Raw? User asked for raw publishing, UI handles throttling.
            _eventBus.Publish(new Events.TrackProgressChangedEvent(ctx.GlobalId, ctx.Progress));
        });

        var success = await _soulseek.DownloadAsync(
            bestMatch.Username!,
            bestMatch.Filename!,
            finalPath,
            bestMatch.Size,
            progress,
            ct
        );

        if (success)
        {
            ctx.Model.ResolvedFilePath = finalPath;
            ctx.Progress = 100;
            await UpdateStateAsync(ctx, PlaylistTrackState.Completed); // Triggers DB and UI

            // Phase 3.1: Finalize with Metadata Service (Tagging)
             await _enrichmentOrchestrator.FinalizeDownloadedTrackAsync(ctx.Model);
        }
        else
        {
            await UpdateStateAsync(ctx, PlaylistTrackState.Failed, "Download transfer failed or was cancelled.");
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

