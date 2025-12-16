using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Library;

/// <summary>
/// Manages track lists, filtering, and search functionality.
/// Handles track display state and filtering logic.
/// </summary>
public class TrackListViewModel : INotifyPropertyChanged
{
    private readonly ILogger<TrackListViewModel> _logger;
    private readonly ILibraryService _libraryService;
    private readonly DownloadManager _downloadManager;
    private readonly ArtworkCacheService _artworkCache;

    // Track collections
    private ObservableCollection<PlaylistTrackViewModel> _currentProjectTracks = new();
    public ObservableCollection<PlaylistTrackViewModel> CurrentProjectTracks
    {
        get => _currentProjectTracks;
        set
        {
            _currentProjectTracks = value;
            OnPropertyChanged();
            RefreshFilteredTracks();
        }
    }

    private ObservableCollection<PlaylistTrackViewModel> _filteredTracks = new();
    public ObservableCollection<PlaylistTrackViewModel> FilteredTracks
    {
        get => _filteredTracks;
        private set
        {
            if (_filteredTracks != value)
            {
                _filteredTracks = value;
                OnPropertyChanged();
            }
        }
    }

    // Search and filter state
    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText != value)
            {
                _searchText = value;
                OnPropertyChanged();
                RefreshFilteredTracks();
            }
        }
    }

    // Filter buttons (radio button behavior)
    private bool _isFilterAll = true;
    public bool IsFilterAll
    {
        get => _isFilterAll;
        set
        {
            if (_isFilterAll != value)
            {
                _isFilterAll = value;
                OnPropertyChanged();
                RefreshFilteredTracks();
            }
        }
    }

    private bool _isFilterDownloaded;
    public bool IsFilterDownloaded
    {
        get => _isFilterDownloaded;
        set
        {
            if (_isFilterDownloaded != value)
            {
                _isFilterDownloaded = value;
                OnPropertyChanged();
                RefreshFilteredTracks();
            }
        }
    }

    private bool _isFilterPending;
    public bool IsFilterPending
    {
        get => _isFilterPending;
        set
        {
            if (_isFilterPending != value)
            {
                _isFilterPending = value;
                OnPropertyChanged();
                RefreshFilteredTracks();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TrackListViewModel(
        ILogger<TrackListViewModel> logger,
        ILibraryService libraryService,
        DownloadManager downloadManager,
        ArtworkCacheService artworkCache,
        IEventBus eventBus)
    {
        _logger = logger;
        _libraryService = libraryService;
        _downloadManager = downloadManager;
        _artworkCache = artworkCache;

        // Subscribe to global track updates
        // Subscribe to global track updates
        eventBus.GetEvent<TrackUpdatedEvent>().Subscribe(evt => OnGlobalTrackUpdated(this, evt.Track));
    }

    /// <summary>
    /// Loads tracks for the specified project.
    /// </summary>
    public async Task LoadProjectTracksAsync(PlaylistJob? job)
    {
        if (job == null)
        {
            CurrentProjectTracks.Clear();
            return;
        }

        try
        {
            _logger.LogInformation("Loading tracks for project: {Name}", job.SourceTitle);
            var tracks = new ObservableCollection<PlaylistTrackViewModel>();

            if (job.Id == Guid.Empty) // All Tracks
            {
                var all = await Task.Run(() =>
                {
                    return _downloadManager.AllGlobalTracks
                        .OrderByDescending(t => t.IsActive)
                        .ThenBy(t => t.Artist)
                        .ToList();
                });

                foreach (var t in all) tracks.Add(t);
            }
            else
            {
                // Load from database
                var freshTracks = await _libraryService.LoadPlaylistTracksAsync(job.Id);

                foreach (var track in freshTracks.OrderBy(t => t.TrackNumber))
                {
                    var vm = new PlaylistTrackViewModel(track);

                    // Sync with live DownloadManager state
                    var liveTrack = _downloadManager.AllGlobalTracks
                        .FirstOrDefault(t => t.GlobalId == track.TrackUniqueHash);

                    if (liveTrack != null)
                    {
                        vm.State = liveTrack.State;
                        vm.Progress = liveTrack.Progress;
                        vm.CurrentSpeed = liveTrack.CurrentSpeed;
                        vm.ErrorMessage = liveTrack.ErrorMessage;

                        // Sync file path
                        if (string.IsNullOrEmpty(vm.Model.ResolvedFilePath) &&
                            !string.IsNullOrEmpty(liveTrack.Model?.ResolvedFilePath))
                        {
                            vm.Model.ResolvedFilePath = liveTrack.Model.ResolvedFilePath;

                            // Persist to database
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await _libraryService.UpdatePlaylistTrackAsync(vm.Model);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed to persist ResolvedFilePath for track {Id}", vm.Model.Id);
                                }
                            });
                        }
                    }

                    tracks.Add(vm);

                    // Phase 0: Load album artwork asynchronously
                    _ = vm.LoadAlbumArtworkAsync(_artworkCache);
                }
            }

            // Update UI
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CurrentProjectTracks = tracks;
                RefreshFilteredTracks();
                _logger.LogInformation("Loaded {Count} tracks for project {Title}", tracks.Count, job.SourceTitle);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load project tracks");
        }
    }

    /// <summary>
    /// Refreshes the filtered tracks based on current filter settings.
    /// </summary>
    public void RefreshFilteredTracks()
    {
        var filtered = CurrentProjectTracks.Where(FilterTracks).ToList();

        _logger.LogDebug("RefreshFilteredTracks: {Input} -> {Filtered} tracks",
            CurrentProjectTracks.Count, filtered.Count);

        FilteredTracks.Clear();
        foreach (var track in filtered)
            FilteredTracks.Add(track);

        OnPropertyChanged(nameof(FilteredTracks));
    }

    private bool FilterTracks(object obj)
    {
        if (obj is not PlaylistTrackViewModel track) return false;

        // Apply state filter first
        if (!IsFilterAll)
        {
            if (IsFilterDownloaded && track.State != PlaylistTrackState.Completed)
                return false;

            if (IsFilterPending && track.State == PlaylistTrackState.Completed)
                return false;
        }

        // Apply search filter
        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        var search = SearchText.Trim();
        return (track.Artist?.Contains(search, StringComparison.OrdinalIgnoreCase) == true) ||
               (track.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
    }

    private void OnGlobalTrackUpdated(object? sender, PlaylistTrackViewModel e)
    {
        // Track updates are handled by the ViewModel itself via binding
        // This is just for logging/debugging if needed
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
