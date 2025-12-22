using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Library;

/// <summary>
/// Manages track lists, filtering, and search functionality.
/// Handles track display state and filtering logic.
public class TrackListViewModel : ReactiveObject
{
    private readonly ILogger<TrackListViewModel> _logger;
    private readonly ILibraryService _libraryService;
    private readonly DownloadManager _downloadManager;
    private MainViewModel? _mainViewModel; // Injected post-construction
    private readonly ArtworkCacheService _artworkCache;
    private readonly IEventBus _eventBus;
    private readonly AppConfig _config;

    public HierarchicalLibraryViewModel Hierarchical { get; }

    private ObservableCollection<PlaylistTrackViewModel> _currentProjectTracks = new();
    public ObservableCollection<PlaylistTrackViewModel> CurrentProjectTracks
    {
        get => _currentProjectTracks;
        set
        {
            this.RaiseAndSetIfChanged(ref _currentProjectTracks, value);
            RefreshFilteredTracks();
        }
    }

    private ObservableCollection<PlaylistTrackViewModel> _filteredTracks = new();
    public ObservableCollection<PlaylistTrackViewModel> FilteredTracks
    {
        get => _filteredTracks;
        private set => this.RaiseAndSetIfChanged(ref _filteredTracks, value);
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set => this.RaiseAndSetIfChanged(ref _searchText, value);
    }

    private bool _isFilterAll = true;
    public bool IsFilterAll
    {
        get => _isFilterAll;
        set => this.RaiseAndSetIfChanged(ref _isFilterAll, value);
    }

    private bool _isFilterDownloaded;
    public bool IsFilterDownloaded
    {
        get => _isFilterDownloaded;
        set => this.RaiseAndSetIfChanged(ref _isFilterDownloaded, value);
    }

    private bool _isFilterPending;
    public bool IsFilterPending
    {
        get => _isFilterPending;
        set => this.RaiseAndSetIfChanged(ref _isFilterPending, value);
    }

    private bool _hasMultiSelection;
    public bool HasMultiSelection
    {
        get => _hasMultiSelection;
        private set => this.RaiseAndSetIfChanged(ref _hasMultiSelection, value);
    }

    private bool _hasSelectedTracks;
    public bool HasSelectedTracks
    {
        get => _hasSelectedTracks;
        private set => this.RaiseAndSetIfChanged(ref _hasSelectedTracks, value);
    }

    private string _selectedCountText = string.Empty;
    public string SelectedCountText
    {
        get => _selectedCountText;
        private set => this.RaiseAndSetIfChanged(ref _selectedCountText, value);
    }

    public System.Windows.Input.ICommand SelectAllTracksCommand { get; }
    public System.Windows.Input.ICommand DeselectAllTracksCommand { get; }
    public System.Windows.Input.ICommand BulkDownloadCommand { get; }
    public System.Windows.Input.ICommand BulkRetryCommand { get; }
    public System.Windows.Input.ICommand BulkCancelCommand { get; }

    public TrackListViewModel(
        ILogger<TrackListViewModel> logger,
        ILibraryService libraryService,
        DownloadManager downloadManager,
        ArtworkCacheService artworkCache,
        IEventBus eventBus,
        AppConfig config)
    {
        _logger = logger;
        _libraryService = libraryService;
        _downloadManager = downloadManager;
        _artworkCache = artworkCache;
        _eventBus = eventBus;
        _config = config;

        Hierarchical = new HierarchicalLibraryViewModel(config);
        SelectAllTracksCommand = ReactiveCommand.Create(() => 
        {
            var selection = Hierarchical.Selection;
            if (selection != null)
            {
                selection.BeginBatchUpdate();
                try
                {
                    // Selection might not have SelectAll, so we iterate rows
                    for (int i = 0; i < Hierarchical.Source.Rows.Count; i++)
                    {
                        var modelIndex = Hierarchical.Source.Rows.RowIndexToModelIndex(i);
                        if (modelIndex != default)
                            selection.Select(modelIndex);
                    }
                }
                finally
                {
                    selection.EndBatchUpdate();
                }
                UpdateSelectionState();
            }
        });

        DeselectAllTracksCommand = ReactiveCommand.Create(() => 
        {
            Hierarchical.Selection?.Clear();
            UpdateSelectionState();
        });

        BulkDownloadCommand = ReactiveCommand.CreateFromTask(ExecuteBulkDownloadAsync);
        BulkRetryCommand = ReactiveCommand.CreateFromTask(ExecuteBulkRetryAsync);
        BulkCancelCommand = ReactiveCommand.CreateFromTask(ExecuteBulkCancelAsync);

        // Selection Change Tracking
        Hierarchical.Selection.SelectionChanged += (s, e) => UpdateSelectionState();

        // Throttled search and filter synchronization
        this.WhenAnyValue(
            x => x.SearchText,
            x => x.IsFilterAll,
            x => x.IsFilterDownloaded,
            x => x.IsFilterPending)
            .Throttle(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RefreshFilteredTracks());

        // Subscribe to global track updates
        eventBus.GetEvent<TrackUpdatedEvent>().Subscribe(evt => OnGlobalTrackUpdated(this, evt.Track));

        // Phase 6D: Local UI sync for track moves
        eventBus.GetEvent<TrackMovedEvent>().Subscribe(evt => OnTrackMoved(evt));
    }

    private void OnTrackMoved(TrackMovedEvent evt)
    {
        Dispatcher.UIThread.Post(() => {
            // If moved from this project, remove it
            if (_mainViewModel?.LibraryViewModel?.SelectedProject?.Id == evt.OldProjectId)
            {
                var track = CurrentProjectTracks.FirstOrDefault(t => t.GlobalId == evt.TrackGlobalId);
                if (track != null)
                {
                    CurrentProjectTracks.Remove(track);
                    RefreshFilteredTracks();
                }
            }
            // If moved to this project, and it's not already here (sanity check)
            else if (_mainViewModel?.LibraryViewModel?.SelectedProject?.Id == evt.NewProjectId)
            {
                // We might need to load the track from global or reload. 
                // For simplicity, if we are in the target project, a refresh might be needed or just reload.
                // But usually the user is in the source project during drag.
                _ = LoadProjectTracksAsync(_mainViewModel.LibraryViewModel.SelectedProject);
            }
        });
    }

    public void SetMainViewModel(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
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
                    if (_mainViewModel == null) return new List<PlaylistTrackViewModel>();
                    return _mainViewModel.AllGlobalTracks
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
                    // Create Smart VM with EventBus subscription
                    var vm = new PlaylistTrackViewModel(track, _eventBus);

                    // Sync with live MainViewModel state to get initial values
                    // Note: This is still useful for initial state (e.g. if download is 50% done when validation opens)
                    var liveTrack = _mainViewModel?.AllGlobalTracks
                        .FirstOrDefault(t => t.GlobalId == track.TrackUniqueHash);

                    if (liveTrack != null)
                    {
                        vm.State = liveTrack.State;
                        vm.Progress = liveTrack.Progress;
                        vm.CurrentSpeed = liveTrack.CurrentSpeed;
                        vm.ErrorMessage = liveTrack.ErrorMessage;
                        vm.CancellationTokenSource = liveTrack.CancellationTokenSource; // Share token? careful.
                        // Actually, sharing state is enough because events will drive updates.
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
                // RefreshFilteredTracks is called by property setter, so explicit call is redundant
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

        // Phase 6D: Ensure TreeDataGrid source is updated
        Hierarchical.UpdateTracks(filtered);

        this.RaisePropertyChanged(nameof(FilteredTracks));
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

    private void UpdateSelectionState()
    {
        var count = Hierarchical.Selection.SelectedItems.Count;
        HasSelectedTracks = count > 0;
        HasMultiSelection = count > 1;
        SelectedCountText = $"{count} tracks selected";
    }

    private async Task ExecuteBulkDownloadAsync()
    {
        var selectedTracks = Hierarchical.Selection.SelectedItems
            .OfType<PlaylistTrackViewModel>()
            .ToList();
        
        if (!selectedTracks.Any()) return;

        _logger.LogInformation("Bulk download for {Count} tracks", selectedTracks.Count);
        _downloadManager.QueueTracks(selectedTracks.Select(t => t.Model).ToList());
        Hierarchical.Selection.Clear(); // Clear selection after action
    }

    private async Task ExecuteBulkRetryAsync()
    {
        var selectedTracks = Hierarchical.Selection.SelectedItems
            .OfType<PlaylistTrackViewModel>()
            .Where(t => t.State == PlaylistTrackState.Failed || t.State == PlaylistTrackState.Cancelled)
            .ToList();
        
        if (!selectedTracks.Any()) return;

        _logger.LogInformation("Bulk retry for {Count} tracks", selectedTracks.Count);
        foreach (var track in selectedTracks)
        {
            track.Resume();
        }
        
        // Ensure DownloadManager resumes if paused
        _ = _downloadManager.StartAsync();
        Hierarchical.Selection.Clear();
    }
    
    private async Task ExecuteBulkCancelAsync()
    {
        var selectedTracks = Hierarchical.Selection.SelectedItems
            .OfType<PlaylistTrackViewModel>()
            .Where(t => t.IsActive)
            .ToList();
        
        if (!selectedTracks.Any()) return;

        _logger.LogInformation("Bulk cancel for {Count} tracks", selectedTracks.Count);
        foreach (var track in selectedTracks)
        {
            track.Cancel();
        }
        Hierarchical.Selection.Clear();
    }

    private void OnGlobalTrackUpdated(object? sender, PlaylistTrackViewModel e)
    {
        // Track updates are handled by the ViewModel itself via binding
    }
}
