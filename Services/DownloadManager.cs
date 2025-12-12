
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services.InputParsers;
using SLSKDONET.Utils;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Services;

/// <summary>
/// Orchestrates the download process for projects and individual tracks.
/// Manages the global state of all active and past downloads.
/// </summary>
public class DownloadManager : INotifyPropertyChanged, IDisposable
{
    private readonly ILogger<DownloadManager> _logger;
    private readonly AppConfig _config;
    private readonly SoulseekAdapter _soulseek;
    private readonly FileNameFormatter _fileNameFormatter;
    private readonly ITaggerService _taggerService;
    private readonly DatabaseService _databaseService;
    private readonly IMetadataService _metadataService;
    private readonly ILibraryService _libraryService;

    // Concurrency control
    private readonly SemaphoreSlim _concurrencySemaphore;
    private readonly CancellationTokenSource _globalCts = new();
    private Task? _processingTask;

    // Global State
    // Using BindingOperations for thread safety is best practice for ObservableCollection accessed from threads
    public ObservableCollection<PlaylistTrackViewModel> AllGlobalTracks { get; } = new();
    private readonly object _collectionLock = new object();
    
    // Event for new project creation
    public event EventHandler<ProjectEventArgs>? ProjectAdded;

    public DownloadManager(
        ILogger<DownloadManager> logger,
        AppConfig config,
        SoulseekAdapter soulseek,
        FileNameFormatter fileNameFormatter,
        ITaggerService taggerService,
        DatabaseService databaseService,
        IMetadataService metadataService,
        ILibraryService libraryService)
    {
        _logger = logger;
        _config = config;
        _soulseek = soulseek;
        _fileNameFormatter = fileNameFormatter;
        _taggerService = taggerService;
        _databaseService = databaseService;
        _metadataService = metadataService;
        _libraryService = libraryService;

        _concurrencySemaphore = new SemaphoreSlim(_config.MaxConcurrentDownloads);

        // Enable cross-thread collection access
        System.Windows.Data.BindingOperations.EnableCollectionSynchronization(AllGlobalTracks, _collectionLock);
    }
    
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
                    // Map Entity -> ViewModel
                    var vm = new PlaylistTrackViewModel(new PlaylistTrack 
                    { 
                        Artist = t.Artist, 
                        Title = t.Title, 
                        TrackUniqueHash = t.GlobalId,
                        Status = t.State == "Completed" ? TrackStatus.Downloaded : TrackStatus.Missing,
                        ResolvedFilePath = t.Filename
                    });
                    
                    vm.State = Enum.TryParse<PlaylistTrackState>(t.State, out var s) ? s : PlaylistTrackState.Pending;
                    vm.GlobalId = t.GlobalId;
                    vm.ErrorMessage = t.ErrorMessage;
                    vm.CoverArtUrl = t.CoverArtUrl; // Hydrate Art
                    
                    // Reset transient states that don't make sense on restart
                    if (vm.State == PlaylistTrackState.Downloading || vm.State == PlaylistTrackState.Searching)
                    {
                        vm.State = PlaylistTrackState.Pending;
                    }

                    vm.PropertyChanged += OnTrackPropertyChanged;
                    AllGlobalTracks.Add(vm);
                }
            }
            _logger.LogInformation("Hydrated {Count} tracks from database.", tracks.Count);
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Failed to init persistence layer");
        }
    }


    // Event for external listeners (UI, Notifications)
    public event EventHandler<PlaylistTrackViewModel>? TrackUpdated;

    /// <summary>
    /// Queues a project (a PlaylistJob) for processing and persists the job header and tracks.
    /// This is the preferred entry point for importing new multi-track projects.
    /// </summary>
    public async Task QueueProject(PlaylistJob job)
    {
        _logger.LogInformation("Queueing project header: {Title} with {Count} tracks", job.SourceTitle, job.PlaylistTracks.Count);
        
        // 1. Persist the job header (for Library UI visibility)
        try
        {
            await _libraryService.SavePlaylistJobAsync(job);
            _logger.LogInformation("Saved PlaylistJob to database: {Title}", job.SourceTitle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist PlaylistJob {Id}", job.Id);
            // Non-critical error, continue to queue tracks.
        }

        // 2. Queue the tracks using the internal method for collection add and individual track persistence
        QueueTracks(job.PlaylistTracks);
        
        // 3. Fire event for Library UI to refresh
        ProjectAdded?.Invoke(this, new ProjectEventArgs(job));
    }

    /// <summary>
    /// Internal method to queue a list of individual tracks for processing (e.g. from an existing project or ad-hoc).
    /// </summary>
    private void QueueTracks(List<PlaylistTrack> tracks)
    {
        _logger.LogInformation("Queueing project tracks with {Count} tracks", tracks.Count);
        lock (_collectionLock)
        {
            foreach (var track in tracks)
            {
                var vm = new PlaylistTrackViewModel(track);
                vm.PropertyChanged += OnTrackPropertyChanged;
                AllGlobalTracks.Add(vm);
                
                // Persist new track
                _ = SaveTrackToDb(vm);

                // Fetch Cover Art (Fire & Forget)
                _ = Task.Run(async () => 
                {
                    try 
                    {
                        var url = await _metadataService.GetAlbumArtUrlAsync(vm.Artist, vm.Model.Album);
                        if (!string.IsNullOrEmpty(url))
                        {
                            vm.CoverArtUrl = url;
                            await SaveTrackToDb(vm); // Persist URL
                        }
                    }
                    catch (Exception ex)
                    {
                         _logger.LogWarning("Failed to fetch art for {Artist} - {Title}: {Msg}", vm.Artist, vm.Title, ex.Message);
                    }
                });
            }
        }
        // Processing loop picks this up automatically
    }

    private async void OnTrackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is PlaylistTrackViewModel vm)
        {
            // Re-fire as a global event
            TrackUpdated?.Invoke(this, vm);
            
            // Persist state changes
            if (e.PropertyName == nameof(PlaylistTrackViewModel.State) || 
                e.PropertyName == nameof(PlaylistTrackViewModel.ErrorMessage) ||
                e.PropertyName == nameof(PlaylistTrackViewModel.CoverArtUrl))
            {
                await SaveTrackToDb(vm);
            }
        }
    }
    
    private async Task SaveTrackToDb(PlaylistTrackViewModel vm)
    {
        try 
        {
            await _databaseService.SaveTrackAsync(new Data.TrackEntity 
            {
                GlobalId = vm.GlobalId,
                Artist = vm.Artist,
                Title = vm.Title,
                State = vm.State.ToString(),
                Filename = vm.Model.ResolvedFilePath,
                Size = 0, // Should populate if we have it
                AddedAt = vm.AddedAt,
                ErrorMessage = vm.ErrorMessage,
                CoverArtUrl = vm.CoverArtUrl
            });
        } 
        catch (Exception ex)
        {
            _logger.LogError(ex, "DB Save Failed");
        }
    }

    // Command Logic Helpers (delegating to VM logic)

    public void PauseTrack(string globalId)
    {
        var vm = AllGlobalTracks.FirstOrDefault(t => t.GlobalId == globalId);
        if (vm != null)
        {
            // Cancel the download but keep the partial file
            vm.CancellationTokenSource?.Cancel();
            vm.State = PlaylistTrackState.Paused;
            _logger.LogInformation("Paused track: {Artist} - {Title}", vm.Artist, vm.Title);
        }
    }

    public void ResumeTrack(string globalId)
    {
        var vm = AllGlobalTracks.FirstOrDefault(t => t.GlobalId == globalId);
        vm?.Resume();
    }

    public void HardRetryTrack(string globalId)
    {
        var vm = AllGlobalTracks.FirstOrDefault(t => t.GlobalId == globalId);
        // We can expose the logic from LibraryViewModel here or just call Reset
        // But HardRetry usually involves file cleanup. 
        // Ideally, the cleanup logic should be IN the VM or HERE.
        // Given Phase 2 Prompt: "Cancel token, Delete local part file, reset Status, Re-queue"
        // I will implement the cleanup logic here to centralize it, or defer to VM if VM has it.
        // VM has `Reset` but not file cleanup. LibraryViewModel had file cleanup.
        // Let's implement full Hard Retry here.
        
        if (vm == null) return;

        _logger.LogInformation("Hard Retry (Manager) for {GlobalId}", globalId);
        vm.CancellationTokenSource?.Cancel();
        vm.State = PlaylistTrackState.Cancelled;

        try 
        {
            var path = vm.Model.ResolvedFilePath;
            if (!string.IsNullOrEmpty(path))
            {
                // Delete completed file if exists
                if (File.Exists(path)) 
                {
                    File.Delete(path);
                    _logger.LogInformation("Deleted file: {Path}", path);
                }
                
                // CRITICAL: Delete .part file to force fresh download from new peer
                var partFile = path + ".part";
                if (File.Exists(partFile))
                {
                    File.Delete(partFile);
                    _logger.LogInformation("Deleted partial file: {Path}", partFile);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup file during Hard Retry");
        }

        vm.Reset(); // Sets to Pending, effectively re-queueing
        
        // Force DB deletion or update? 
        // Reset sets state to Pending, so OnTrackPropertyChanged will fire and update DB to Pending. Correct.
    }

    /// <summary>
    /// Cancels a track download and cleans up files, but keeps track in the list with Cancelled state.
    /// Track remains visible so user can retry or manually delete later.
    /// </summary>
    public void CancelTrack(string globalId)
    {
        var vm = AllGlobalTracks.FirstOrDefault(t => t.GlobalId == globalId);
        if (vm == null)
        {
            _logger.LogWarning("Cancel: Track not found: {GlobalId}", globalId);
            return;
        }

        _logger.LogInformation("Cancelling track: {Artist} - {Title}", vm.Artist, vm.Title);

        // 1. Cancel any active download
        vm.CancellationTokenSource?.Cancel();
        vm.CancellationTokenSource?.Dispose();
        vm.CancellationTokenSource = null;
        
        // 2. Set cancelled state (track remains in list!)
        vm.State = PlaylistTrackState.Cancelled;

        // 3. Delete all associated files
        try
        {
            var path = vm.Model?.ResolvedFilePath;
            if (!string.IsNullOrEmpty(path))
            {
                // Delete completed file if exists
                if (File.Exists(path))
                {
                    File.Delete(path);
                    _logger.LogInformation("Deleted file: {Path}", path);
                }
                
                // Delete partial file if exists
                var partFile = path + ".part";
                if (File.Exists(partFile))
                {
                    File.Delete(partFile);
                    _logger.LogInformation("Deleted partial file: {Path}", partFile);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete files during cancel");
        }

        _logger.LogInformation("Track cancelled - remains in list with Cancelled state");
    }

    /// <summary>
    /// Helper to enqueue a single ad-hoc track (e.g. from search results).
    /// </summary>
    public void EnqueueTrack(Track track)
    {
        // Wrap the standard Track in a PlaylistTrack
        var playlistTrack = new PlaylistTrack
        {
             Id = Guid.NewGuid(),
             Artist = track.Artist ?? "Unknown",
             Title = track.Title ?? "Unknown",
             Album = track.Album ?? "Unknown",
             Status = TrackStatus.Missing, // Assume missing until downloaded
             ResolvedFilePath = Path.Combine(_config.DownloadDirectory!, _fileNameFormatter.Format(_config.NameFormat ?? "{artist} - {title}", track) + "." + track.GetExtension()),
             TrackUniqueHash = track.UniqueHash
        };
        
        QueueTracks(new List<PlaylistTrack> { playlistTrack });
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_processingTask != null) return;

        _logger.LogInformation("DownloadManager Orchestrator started.");
        
        // Load persistence
        await InitAsync();
        
        // We link the passed CT with our global CT to allow stopping from either source
        _processingTask = ProcessQueueLoop(_globalCts.Token); // Use global token for the long-running task
        await Task.CompletedTask;
    }

    private async Task ProcessQueueLoop(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                PlaylistTrackViewModel? nextTrack = null;

                lock (_collectionLock)
                {
                    // Find the next Pending track
                    nextTrack = AllGlobalTracks.FirstOrDefault(t => t.State == PlaylistTrackState.Pending);
                }

                if (nextTrack == null)
                {
                    await Task.Delay(500, token);
                    continue;
                }

                // Acquire a concurrency slot
                await _concurrencySemaphore.WaitAsync(token);

                // Double check status (race condition)
                if (nextTrack.State != PlaylistTrackState.Pending)
                {
                    _concurrencySemaphore.Release();
                    continue;
                }

                // Start processing in background (Fire & Forget)
                // We don't await this; we want to continue the loop to find more work if concurrency allows
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessTrackAsync(nextTrack, token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "CRITICAL: Error in ProcessTrack wrapper for {Artist} - {Title}", nextTrack.Artist, nextTrack.Title);
                        nextTrack.State = PlaylistTrackState.Failed;
                        nextTrack.ErrorMessage = "Internal Error: " + ex.Message;
                    }
                    finally
                    {
                        _concurrencySemaphore.Release();
                    }
                }, token);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("DownloadManager processing loop cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DownloadManager processing loop crashed!");
        }
    }

    private async Task ProcessTrackAsync(PlaylistTrackViewModel track, CancellationToken ct)
    {
        track.CancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var trackCt = track.CancellationTokenSource.Token;

        try
        {
            // --- 0. Pre-check ---
            if (track.Model.Status == TrackStatus.Downloaded && File.Exists(track.Model.ResolvedFilePath))
            {
                track.State = PlaylistTrackState.Completed;
                track.Progress = 100;
                return;
            }

            // --- 1. Search Phase ---
            // If the model doesn't have specific file info, we must search.
            // If it DOES (e.g. from manual search), we might skip this. 
            // For Bundle 1, let's assume we always search if it's "Pending" to be robust for projects.
            
            track.State = PlaylistTrackState.Searching;
            track.Progress = 0; // Infinite spinner logic in UI often checks IsActive
            
            var query = $"{track.Artist} {track.Title}";
            var results = new ConcurrentBag<Track>();
            
            // Search with 30s timeout
            using var searchCts = CancellationTokenSource.CreateLinkedTokenSource(trackCt);
            searchCts.CancelAfter(TimeSpan.FromSeconds(30)); 

            try 
            {
                await _soulseek.SearchAsync(
                    query,
                    null, // TODO: Get format filter from Config
                    (null, null), // TODO: Get bitrate filter from Config
                    DownloadMode.Normal,
                    (found) => {
                        foreach (var f in found) results.Add(f);
                    },
                    searchCts.Token
                );
            }
            catch (OperationCanceledException) { /* Timeout or Cancelled */ }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Search error for {Query}", query);
                // Continue if we have any results, else fail
            }

            if (results.IsEmpty)
            {
                track.State = PlaylistTrackState.Failed;
                track.ErrorMessage = "No results found";
                return;
            }

            // Select Best Match
            var preferredFormat = _config.PreferredFormats?.FirstOrDefault() ?? "mp3";
            var minBitrate = _config.PreferredMinBitrate; // Fixed: already int
            
            var bestMatch = results
                .Where(t => t.Bitrate >= minBitrate)
                // .Where(t => t.GetExtension().Contains(preferredFormat)) // Simple filter
                .OrderByDescending(t => t.Bitrate)
                .ThenByDescending(t => t.Length ?? 0)
                .FirstOrDefault();

            if (bestMatch == null)
            {
                // Relaxed fallback
                 bestMatch = results.OrderByDescending(t => t.Bitrate).FirstOrDefault();
            }
            
            if (bestMatch == null)
            {
                track.State = PlaylistTrackState.Failed;
                track.ErrorMessage = "No suitable match found";
                return;
            }

            // --- 2. Download Phase ---
            track.State = PlaylistTrackState.Downloading;
            
            // Update model with the specific file info we found
            track.Model.Status = TrackStatus.Missing; // Still missing until done
            
            // Use resolved path or fallback
            var finalPath = track.Model.ResolvedFilePath;
            if (string.IsNullOrEmpty(finalPath))
            {
                finalPath = Path.Combine(_config.DownloadDirectory ?? "Downloads", 
                    $"{track.Artist} - {track.Title}.{bestMatch.GetExtension()}");
            }
             // Ensure directory exists
            var dir = System.IO.Path.GetDirectoryName(finalPath); // Fixed: Path, not Directory
            if (dir != null) System.IO.Directory.CreateDirectory(dir);

            var progress = new Progress<double>(p => track.Progress = p * 100);
            
            var success = await _soulseek.DownloadAsync(
                bestMatch.Username!,
                bestMatch.Filename!,
                finalPath,
                bestMatch.Size,
                progress,
                trackCt
            );

            if (success)
            {
                track.State = PlaylistTrackState.Completed;
                track.Progress = 100;
                track.Model.Status = TrackStatus.Downloaded;
                track.Model.ResolvedFilePath = finalPath;
                
                // Tagging
                try 
                {
                    await _taggerService.TagFileAsync(bestMatch, finalPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Tagging error: {Msg}", ex.Message);
                }
            }
            else
            {
                track.State = PlaylistTrackState.Failed;
                track.ErrorMessage = "Download failed (Transfer)";
            }
        }
        catch (OperationCanceledException)
        {
            // If the user paused it, the VM state is already Paused.
            // If the user cancelled it, the VM state is already Cancelled (or should be).
            // We only set it to Cancelled if it's not already Paused/Cancelled.
            
            if (track.State != PlaylistTrackState.Paused && track.State != PlaylistTrackState.Cancelled)
            {
                track.State = PlaylistTrackState.Cancelled;
            }
            _logger.LogInformation("Track processing stopped: {Artist} - {Title} ({State})", track.Artist, track.Title, track.State);
        }
        catch (Exception ex)
        {
            track.State = PlaylistTrackState.Failed;
            track.ErrorMessage = ex.Message;
            _logger.LogError(ex, "ProcessTrackAsync fatal error");
        }
    }
    
    // Properties for UI Summary (Aggregated from Collection)
    // Determining these efficiently is tricky with ObservableCollection. 
    // Ideally the UI binds to the Collection directly and filters count.
    // Keeping existing properties for backward compat if needed, but they might be expensive to calc on every change.
    // For Bundle 1, I'll rely on the VM collection.

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _globalCts.Cancel();
        _concurrencySemaphore.Dispose();
    }
}
