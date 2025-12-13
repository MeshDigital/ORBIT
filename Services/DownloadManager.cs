using System;
using System.Collections.Concurrent;
using Avalonia.Threading;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly CancellationTokenSource _globalCts = new();
    private Task? _processingTask;

    // Global State
    // Using BindingOperations for thread safety is best practice for ObservableCollection accessed from threads
    public ObservableCollection<PlaylistTrackViewModel> AllGlobalTracks { get; } = new();
    private readonly object _collectionLock = new object();
    
    // Event for new project creation
    public event EventHandler<ProjectEventArgs>? ProjectAdded;

    // Event for project status update (used by LibraryViewModel to refresh counts)
    public event EventHandler<Guid>? ProjectUpdated;
    
    // Expose download directory from config
    public string? DownloadDirectory => _config.DownloadDirectory;

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
        _libraryService = libraryService;

        // Initialize from config, but allow runtime changes
        MaxActiveDownloads = _config.MaxConcurrentDownloads > 0 ? _config.MaxConcurrentDownloads : 3;

        // Note: WPF collection synchronization is not needed for Avalonia
        // The collection is accessed via async/await patterns which handle threading properly
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
        // Robustness: If the job comes from an import preview, it will have OriginalTracks
        // but no PlaylistTracks. We must convert them before proceeding.
        if (job.PlaylistTracks.Count == 0 && job.OriginalTracks.Count > 0)
        {
            _logger.LogInformation("QueueProject: Converting {Count} OriginalTracks to PlaylistTracks for job '{Title}'.", job.OriginalTracks.Count, job.SourceTitle);
            var playlistTracks = new List<PlaylistTrack>();
            int idx = 1;
            foreach (var track in job.OriginalTracks)
            {
                var pt = new PlaylistTrack
                {
                    Id = Guid.NewGuid(),
                    PlaylistId = job.Id,
                    Artist = track.Artist ?? string.Empty,
                    Title = track.Title ?? string.Empty,
                    Album = track.Album ?? string.Empty,
                    TrackUniqueHash = track.UniqueHash,
                    Status = TrackStatus.Missing,
                    ResolvedFilePath = string.Empty, // Will be determined during download
                    TrackNumber = idx++
                };
                playlistTracks.Add(pt);
            }
            job.PlaylistTracks = playlistTracks;
        }

        _logger.LogInformation(
            "Queueing project. Title: {Title}, TrackCount: {Count}, JobId: {JobId}, Thread: {ThreadId}",
            job.SourceTitle,
            job.PlaylistTracks.Count,
            job.Id,
            Thread.CurrentThread.ManagedThreadId);

        // 1. Persist the job header and all associated tracks via LibraryService
        // This ensures the Library UI (Left Sidebar) is updated immediately.
        try
        {
            await _libraryService.SavePlaylistJobWithTracksAsync(job);
            
            _logger.LogInformation(
                "Saved PlaylistJob to Library. Title: {Title}, TrackCount: {Count}, JobId: {JobId}, Thread: {ThreadId}",
                job.SourceTitle,
                job.PlaylistTracks.Count,
                job.Id,
                Thread.CurrentThread.ManagedThreadId);

            // Run diagnostic log right after saving
            await _databaseService.LogPlaylistJobDiagnostic(job.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist PlaylistJob and its tracks: {Id}", job.Id);
            // This is a critical error, as the tracks won't be processed. We should not continue.
            return;
        }

        // 3. Queue the tracks using the internal method for collection add and individual track persistence
        QueueTracks(job.PlaylistTracks);
        
        // 4. Fire event for Library UI to refresh
        ProjectAdded?.Invoke(this, new ProjectEventArgs(job));
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

    public async Task DeleteTrackFromDiskAndHistoryAsync(PlaylistTrackViewModel vm)
    {
        if (vm == null) return;
        
        _logger.LogInformation("Deleting track from disk and history: {Artist} - {Title} ({Id})", vm.Artist, vm.Title, vm.GlobalId);

        // 1. Cancel active download
        if (vm.CanCancel)
        {
            vm.Cancel();
        }

        // 2. Delete Physical Files
        if (!string.IsNullOrEmpty(vm.Model.ResolvedFilePath))
        {
            try
            {
                if (File.Exists(vm.Model.ResolvedFilePath))
                {
                    File.Delete(vm.Model.ResolvedFilePath);
                    _logger.LogInformation("Deleted file: {Path}", vm.Model.ResolvedFilePath);
                }
                
                // Attempt to delete partial download if exists
                var partPath = vm.Model.ResolvedFilePath + ".part"; // Convention used by Transfer
                if (File.Exists(partPath))
                {
                    File.Delete(partPath);
                        _logger.LogInformation("Deleted partial file: {Path}", partPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete file(s) for track {Id}", vm.GlobalId);
                // Continue to remove from history even if file delete fails (orphaned file risk vs stuck DB risk)
            }
        }

        // 3. Remove from Global History (DB)
        await _databaseService.RemoveTrackAsync(vm.GlobalId);

        // 4. Update references in Playlists (DB)
        // Mark as Missing so user sees it's gone but metadata remains
        await _databaseService.UpdatePlaylistTrackStatusAndRecalculateJobsAsync(vm.GlobalId, TrackStatus.Missing, string.Empty);

        // 5. Remove from Memory (Global Cache)
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            AllGlobalTracks.Remove(vm);
        });
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
                // 1. Save to Global Track Cache (Existing logic)
                await SaveTrackToDb(vm);

                // 2. NEW: Sync with Playlist Data and Recalculate Jobs
                // Only needed if the state implies a Status change (Completed/Failed)
                if (vm.State == PlaylistTrackState.Completed || 
                    vm.State == PlaylistTrackState.Failed || 
                    vm.State == PlaylistTrackState.Cancelled)
                {
                    try
                    {
                        var dbStatus = vm.State switch
                        {
                            PlaylistTrackState.Completed => TrackStatus.Downloaded,
                            PlaylistTrackState.Failed => TrackStatus.Failed,
                            PlaylistTrackState.Cancelled => TrackStatus.Skipped, // Map Cancelled to Skipped
                            _ => vm.Model.Status
                        };

                        var updatedJobIds = await _databaseService.UpdatePlaylistTrackStatusAndRecalculateJobsAsync(
                            vm.GlobalId, 
                            dbStatus, 
                            vm.Model.ResolvedFilePath
                        );

                        // 3. Notify the Library UI to refresh the specific Project Header
                        foreach (var jobId in updatedJobIds)
                        {
                            ProjectUpdated?.Invoke(this, jobId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to sync playlist track status for {Id}", vm.GlobalId);
                    }
                }
                }
            
            // Persist Metadata Changes (Interactive Editing)
            if (e.PropertyName == "Artist" || e.PropertyName == "Title" || e.PropertyName == "Album")
            {
                 // 1. Update Global Cache
                 await SaveTrackToDb(vm);

                 // 2. Update PlaylistTrack Relation (Database)
                 // This ensures the Library View shows new metadata on restart
                 await _libraryService.UpdatePlaylistTrackAsync(vm.Model);

                 // 3. Update Audio File Tags (if downloaded)
                 // Only if file exists
                 if (!string.IsNullOrEmpty(vm.Model.ResolvedFilePath) && File.Exists(vm.Model.ResolvedFilePath))
                 {
                      try 
                      {
                          // Create a lightweight Track object for tagging
                          var trackInfo = new Track 
                          { 
                              Artist = vm.Artist, 
                              Title = vm.Title, 
                              Album = vm.Album 
                          };
                          await _taggerService.TagFileAsync(trackInfo, vm.Model.ResolvedFilePath);
                          _logger.LogInformation("Updated ID3 tags for {File}", vm.Model.ResolvedFilePath);
                      }
                      catch(Exception ex) 
                      {
                          _logger.LogWarning("Failed to update ID3 tags: {Msg}", ex.Message);
                      }
                 }
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

                // Check concurrency limit (Dynamic)
                int currentActive = 0;
                lock (_collectionLock)
                {
                    currentActive = AllGlobalTracks.Count(t => t.IsActive);
                }

                if (currentActive >= MaxActiveDownloads)
                {
                    await Task.Delay(1000, token);
                    continue;
                }

                // Double check status (race condition)
                if (nextTrack.State != PlaylistTrackState.Pending)
                {
                    continue;
                }

                // Mark as Searching immediately to prevent duplicate scheduling in the loop
                nextTrack.State = PlaylistTrackState.Searching;

                // Start processing in background (Fire & Forget)
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
                        // No semaphore to release
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

            // --- 1. Search for potential files ---
            var searchResults = await SearchForTrackAsync(track, trackCt);
            if (searchResults == null || !searchResults.Any())
            {
                track.State = PlaylistTrackState.Failed;
                track.ErrorMessage = "No results found";
                return;
            }

            // --- 2. Select the best match from the results ---
            var bestMatch = SelectBestMatch(searchResults);
            if (bestMatch == null)
            {
                track.State = PlaylistTrackState.Failed;
                track.ErrorMessage = "No suitable match found";
                return;
            }

            // --- 2. Download Phase ---
            await DownloadFileAsync(track, bestMatch, trackCt);
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
            // State change will be persisted by OnTrackPropertyChanged
        }
        catch (Exception ex)
        {
            track.State = PlaylistTrackState.Failed;
            track.ErrorMessage = ex.Message;
            _logger.LogError(ex, "ProcessTrackAsync fatal error");
            // State change will be persisted by OnTrackPropertyChanged
        }
    }

    private async Task<IProducerConsumerCollection<Track>?> SearchForTrackAsync(PlaylistTrackViewModel track, CancellationToken ct)
    {
        track.State = PlaylistTrackState.Searching;
        track.Progress = 0;

        var query = $"{track.Artist} {track.Title}";
        var results = new ConcurrentBag<Track>();

        using var searchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        searchCts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            await _soulseek.SearchAsync(
                query,
                _config.PreferredFormats?.ToArray(),
                (_config.PreferredMinBitrate, null),
                DownloadMode.Normal,
                (found) => {
                    foreach (var f in found) results.Add(f);
                },
                searchCts.Token
            );
        }
        catch (OperationCanceledException) { /* Timeout or user cancellation is expected */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Search for '{Query}' failed.", query);
        }

        return results.IsEmpty ? null : results;
    }

    private Track? SelectBestMatch(IProducerConsumerCollection<Track> results)
    {
        var minBitrate = _config.PreferredMinBitrate;

        var bestMatch = results
            .Where(t => t.Bitrate >= minBitrate)
            .OrderByDescending(t => t.Bitrate)
            .ThenByDescending(t => t.Length ?? 0)
            .FirstOrDefault();

        // If no match meets the criteria, relax the constraints and take the highest bitrate available.
        return bestMatch ?? results.OrderByDescending(t => t.Bitrate).FirstOrDefault();
    }

    private async Task DownloadFileAsync(PlaylistTrackViewModel track, Track bestMatch, CancellationToken ct)
    {
        track.State = PlaylistTrackState.Downloading;

        var finalPath = track.Model.ResolvedFilePath;
        if (string.IsNullOrEmpty(finalPath))
        {
            finalPath = Path.Combine(_config.DownloadDirectory ?? "Downloads",
                _fileNameFormatter.Format(_config.NameFormat ?? "{artist} - {title}", bestMatch) + $".{bestMatch.GetExtension()}");
        }

        var dir = Path.GetDirectoryName(finalPath);
        if (dir != null) Directory.CreateDirectory(dir);

        var progress = new Progress<double>(p => track.Progress = p * 100);

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
            // CRITICAL: Set the file path BEFORE changing state
            // State change triggers OnTrackPropertyChanged which saves to DB
            // So we must set ResolvedFilePath first to ensure it's included in the save
            track.Model.ResolvedFilePath = finalPath;
            track.Progress = 100;
            track.State = PlaylistTrackState.Completed; // This triggers DB save

            try
            {
                await _taggerService.TagFileAsync(bestMatch, finalPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tagging failed for {File}", finalPath);
            }
        }
        else
        {
            track.State = PlaylistTrackState.Failed;
            track.ErrorMessage = "Download transfer failed or was cancelled.";
        }
        
        // The state change will trigger OnTrackPropertyChanged, which handles all persistence.
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
        _globalCts.Dispose();
    }
}
