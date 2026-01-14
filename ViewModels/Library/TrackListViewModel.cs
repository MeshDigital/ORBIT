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
using System.Reactive.Disposables;

using System.Collections.Specialized;

namespace SLSKDONET.ViewModels.Library;

/// <summary>
/// Manages track lists, filtering, and search functionality.
/// Handles track display state and filtering logic.
public class TrackListViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    private bool _isDisposed;

    private readonly ILogger<TrackListViewModel> _logger;
    private readonly ILibraryService _libraryService;
    private readonly DownloadManager _downloadManager;
    private MainViewModel? _mainViewModel; // Injected post-construction
    private readonly ArtworkCacheService _artworkCache;
    private readonly IEventBus _eventBus;
    private readonly AppConfig _config;
    private readonly MetadataEnrichmentOrchestrator _enrichmentOrchestrator;
    private readonly IBulkOperationCoordinator _bulkCoordinator;

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

    private IList<PlaylistTrackViewModel> _filteredTracks = new ObservableCollection<PlaylistTrackViewModel>();
    public IList<PlaylistTrackViewModel> FilteredTracks
    {
        get => _filteredTracks;
        private set 
        {
            if (_filteredTracks is INotifyCollectionChanged oldCol)
            {
                oldCol.CollectionChanged -= OnFilteredTracksChanged;
            }

            this.RaiseAndSetIfChanged(ref _filteredTracks, value);
            
            if (_filteredTracks is INotifyCollectionChanged newCol)
            {
                newCol.CollectionChanged += OnFilteredTracksChanged;
            }
            
            this.RaisePropertyChanged(nameof(LimitedTracks));
        }
    }

    private void OnFilteredTracksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // When the virtualized collection updates (pages load), we must notify the UI 
        // that LimitedTracks (Subset) has hypothetically changed so it re-reads the subset.
        this.RaisePropertyChanged(nameof(LimitedTracks));
    }
    
    /// <summary>
    /// A safe subset of tracks (max 50) for non-virtualized views like the Card View.
    /// Prevents UI freezing with large lists.
    /// </summary>
    public IEnumerable<PlaylistTrackViewModel> LimitedTracks => 
        (FilteredTracks as VirtualizedTrackCollection)?.GetSubset(50) ?? FilteredTracks.Take(50);

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set => this.RaiseAndSetIfChanged(ref _searchText, value);
    }

    // Guard flag to prevent infinite recursion in filter properties
    private bool _updatingFilters = false;

    private bool _isFilterAll = true;
    public bool IsFilterAll
    {
        get => _isFilterAll;
        set
        {
            if (_updatingFilters) return;
            _updatingFilters = true;
            try
            {
                this.RaiseAndSetIfChanged(ref _isFilterAll, value);
                if (value)
                {
                    _isFilterDownloaded = false;
                    this.RaisePropertyChanged(nameof(IsFilterDownloaded));
                    
                    _isFilterPending = false;
                    this.RaisePropertyChanged(nameof(IsFilterPending));
                }
                else if (!IsFilterDownloaded && !IsFilterPending)
                {
                    // If everything is unselected, force All back on
                    _isFilterAll = true;
                    this.RaisePropertyChanged(nameof(IsFilterAll));
                }
            }
            finally { _updatingFilters = false; }
        }
    }

    private bool _isFilterDownloaded;
    public bool IsFilterDownloaded
    {
        get => _isFilterDownloaded;
        set
        {
            if (_updatingFilters) return;
            _updatingFilters = true;
            try
            {
                this.RaiseAndSetIfChanged(ref _isFilterDownloaded, value);
                if (value)
                {
                    _isFilterAll = false;
                    this.RaisePropertyChanged(nameof(IsFilterAll));
                    
                    _isFilterPending = false;
                    this.RaisePropertyChanged(nameof(IsFilterPending));
                }
                else if (!IsFilterPending)
                {
                    // If everything is unselected, force All back on
                    _isFilterAll = true;
                    this.RaisePropertyChanged(nameof(IsFilterAll));
                }
            }
            finally { _updatingFilters = false; }
        }
    }

    private bool _isFilterPending;
    public bool IsFilterPending
    {
        get => _isFilterPending;
        set
        {
            if (_updatingFilters) return;
            _updatingFilters = true;
            try
            {
                this.RaiseAndSetIfChanged(ref _isFilterPending, value);
                if (value)
                {
                    _isFilterAll = false;
                    this.RaisePropertyChanged(nameof(IsFilterAll));
                    
                    _isFilterDownloaded = false;
                    this.RaisePropertyChanged(nameof(IsFilterDownloaded));

                    _isFilterNeedsReview = false;
                    this.RaisePropertyChanged(nameof(IsFilterNeedsReview));
                }
                else if (!IsFilterDownloaded && !IsFilterNeedsReview)
                {
                    // If everything is unselected, force All back on
                    _isFilterAll = true;
                    this.RaisePropertyChanged(nameof(IsFilterAll));
                }
            }
            finally { _updatingFilters = false; }
        }
    }

    private bool _isFilterNeedsReview;
    public bool IsFilterNeedsReview
    {
        get => _isFilterNeedsReview;
        set
        {
            if (_updatingFilters) return;
            _updatingFilters = true;
            try
            {
                this.RaiseAndSetIfChanged(ref _isFilterNeedsReview, value);
                if (value)
                {
                    _isFilterAll = false;
                    this.RaisePropertyChanged(nameof(IsFilterAll));
                    
                    _isFilterDownloaded = false;
                    this.RaisePropertyChanged(nameof(IsFilterDownloaded));
                    
                    _isFilterPending = false;
                    this.RaisePropertyChanged(nameof(IsFilterPending));
                }
                else if (!IsFilterDownloaded && !IsFilterPending)
                {
                    // If everything is unselected, force All back on
                    _isFilterAll = true;
                    this.RaisePropertyChanged(nameof(IsFilterAll));
                }
            }
            finally { _updatingFilters = false; }
        }
    }

    private bool _hasMultiSelection;
    public bool HasMultiSelection
    {
        get => _hasMultiSelection;
        private set => this.RaiseAndSetIfChanged(ref _hasMultiSelection, value);
    }
    
    // Phase 22: Search 2.0 - The Bouncer
    private bool _isBouncerActive;
    public bool IsBouncerActive
    {
        get => _isBouncerActive;
        set
        {
            this.RaiseAndSetIfChanged(ref _isBouncerActive, value);
            RefreshFilteredTracks();
        }
    }

    // Phase 22: Search 2.0 - Vibe Filter
    private string? _vibeFilter;
    public string? VibeFilter
    {
        get => _vibeFilter;
        set
        {
            this.RaiseAndSetIfChanged(ref _vibeFilter, value);
            RefreshFilteredTracks();
        }
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
    
    // ListBox Selection Binding
    private ObservableCollection<PlaylistTrackViewModel> _selectedTracks = new();
    public ObservableCollection<PlaylistTrackViewModel> SelectedTracks 
    { 
        get => _selectedTracks;
        private set
        {
            if (value == null || Equals(_selectedTracks, value)) return;

            if (_selectedTracks != null)
                _selectedTracks.CollectionChanged -= OnSelectionChanged;
            
            this.RaisePropertyChanging();
            _selectedTracks = value;
            this.RaisePropertyChanged();
            
            if (_selectedTracks != null)
                _selectedTracks.CollectionChanged += OnSelectionChanged;
                
            UpdateSelectionState();
        }
    }
    
    // Phase 22: Available Vibes
    public ObservableCollection<string> AvailableVibes { get; } = new ObservableCollection<string>
    {
        "Aggressive", "Chaotic", "Energetic", "Happy", 
        "Party", "Relaxed", "Sad", "Dark"
    };

    public PlaylistTrackViewModel? LeadSelectedTrack => SelectedTracks.FirstOrDefault();

    // Phase 15: Style Filters
    public ObservableCollection<StyleFilterItem> StyleFilters { get; } = new();

    private void OnStyleFilterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StyleFilterItem.IsSelected))
        {
            RefreshFilteredTracks();
        }
    }

    public async Task LoadStyleFiltersAsync()
    {
        try 
        {
            var styles = await _libraryService.GetStyleDefinitionsAsync();
            
            // Updates on UI Thread
            Dispatcher.UIThread.Post(() =>
            {
                // Preserve selection state if possible? 
                // For simplicity, reset or match by ID. 
                // Given this happens rarely (create/delete style), full reload is fine.
                
                foreach (var item in StyleFilters) item.PropertyChanged -= OnStyleFilterChanged;
                StyleFilters.Clear();

                foreach (var style in styles)
                {
                    var item = new StyleFilterItem(style);
                    item.PropertyChanged += OnStyleFilterChanged;
                    StyleFilters.Add(item);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load style filters");
        }
    }

    public System.Windows.Input.ICommand SelectAllTracksCommand { get; }
    public System.Windows.Input.ICommand DeselectAllTracksCommand { get; }
    public System.Windows.Input.ICommand BulkDownloadCommand { get; }
    public System.Windows.Input.ICommand CopyToFolderCommand { get; }
    public System.Windows.Input.ICommand BulkRetryCommand { get; }
    public System.Windows.Input.ICommand BulkCancelCommand { get; }
    public System.Windows.Input.ICommand BulkReEnrichCommand { get; }
    public System.Windows.Input.ICommand SeparateStemsCommand { get; }
    
    // Phase 18: Sonic Match - Find Similar Vibe
    public System.Windows.Input.ICommand FindSimilarCommand { get; }

    public TrackListViewModel(
        ILogger<TrackListViewModel> logger,
        ILibraryService libraryService,
        DownloadManager downloadManager,
        ArtworkCacheService artworkCache,
        IEventBus eventBus,
        AppConfig config,
        MetadataEnrichmentOrchestrator enrichmentOrchestrator,
        AnalysisQueueService analysisQueueService,
        IBulkOperationCoordinator bulkCoordinator)
    {
        _logger = logger;
        _libraryService = libraryService;
        _downloadManager = downloadManager;
        _artworkCache = artworkCache;
        _eventBus = eventBus;
        _enrichmentOrchestrator = enrichmentOrchestrator;
        _config = config;
        _bulkCoordinator = bulkCoordinator;

        Hierarchical = new HierarchicalLibraryViewModel(config, downloadManager, analysisQueueService);
        
        SelectAllTracksCommand = ReactiveCommand.Create(() => 
        {
            // CRITICAL: Create new collection to avoid N notifications from .Add() loop
            // This replaces the entire selection in one go, preventing UI stack overflow
            SelectedTracks = new ObservableCollection<PlaylistTrackViewModel>(FilteredTracks);
        });

        DeselectAllTracksCommand = ReactiveCommand.Create(() => 
        {
            SelectedTracks = new ObservableCollection<PlaylistTrackViewModel>();
        });

        BulkDownloadCommand = ReactiveCommand.CreateFromTask(ExecuteBulkDownloadAsync);
        CopyToFolderCommand = ReactiveCommand.CreateFromTask(ExecuteCopyToFolderAsync);
        BulkRetryCommand = ReactiveCommand.CreateFromTask(ExecuteBulkRetryAsync);
        BulkCancelCommand = ReactiveCommand.CreateFromTask(ExecuteBulkCancelAsync);
        BulkReEnrichCommand = ReactiveCommand.CreateFromTask(ExecuteBulkReEnrichAsync);
        
        // Phase 18: Find Similar - triggers sonic match search
        FindSimilarCommand = ReactiveCommand.Create<PlaylistTrackViewModel>(ExecuteFindSimilar);

        // Selection Change Tracking
        _selectedTracks.CollectionChanged += OnSelectionChanged;

        // Throttled search and filter synchronization
        this.WhenAnyValue(
            x => x.SearchText,
            x => x.IsFilterAll,
            x => x.IsFilterDownloaded,
            x => x.IsFilterPending,
            x => x.IsFilterNeedsReview)
            .Throttle(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RefreshFilteredTracks())
            .DisposeWith(_disposables);

        // Subscribe to global track updates
        _disposables.Add(eventBus.GetEvent<TrackUpdatedEvent>().Subscribe(evt => OnGlobalTrackUpdated(this, evt.Track)));

        // Phase 6D: Local UI sync for track moves
        _disposables.Add(eventBus.GetEvent<TrackMovedEvent>().Subscribe(evt => OnTrackMoved(evt)));

        // Phase 15: Refresh filters when definitions change
        _disposables.Add(eventBus.GetEvent<StyleDefinitionsUpdatedEvent>().Subscribe(evt => { _ = LoadStyleFiltersAsync(); }));
        
        // Phase 11.6: Refresh UI when track is added (cloned)
        _disposables.Add(eventBus.GetEvent<TrackAddedEvent>().Subscribe(OnTrackAdded));

        
        // Initial Load
        _ = LoadStyleFiltersAsync();
    }
    
    // Explicit handler to support attach/detach
    private void OnSelectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateSelectionState();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            _disposables.Dispose();
            
            // Dispose tracks
            foreach (var track in CurrentProjectTracks)
            {
                if (track is IDisposable d) d.Dispose();
            }
            CurrentProjectTracks.Clear();
            
            foreach (var style in StyleFilters)
            {
                style.PropertyChanged -= OnStyleFilterChanged;
            }
            StyleFilters.Clear();
        }

        _isDisposed = true;
    }


    private void OnTrackAdded(TrackAddedEvent evt)
    {
        Dispatcher.UIThread.Post(() => {
            // If the track belongs to the current project, add it
            if (_mainViewModel?.LibraryViewModel?.SelectedProject?.Id == evt.TrackModel.PlaylistId)
            {
                // Prevents duplicates if the event is fired twice
                if (CurrentProjectTracks.Any(t => t.Model.Id == evt.TrackModel.Id)) return;

                var vm = new PlaylistTrackViewModel(evt.TrackModel, _eventBus, _libraryService, _artworkCache);
                
                // Sync Initial State
                if (evt.InitialState.HasValue)
                {
                    vm.State = evt.InitialState.Value;
                }

                CurrentProjectTracks.Add(vm);
                RefreshFilteredTracks();
            }
            // "All Tracks" project (Guid.Empty)
            else if (_mainViewModel?.LibraryViewModel?.SelectedProject?.Id == Guid.Empty)
            {
                // In All Tracks view, we add it if it's a new unique file 
                // but cloned tracks ARE new unique files.
                if (CurrentProjectTracks.Any(t => t.Model.TrackUniqueHash == evt.TrackModel.TrackUniqueHash)) return;

                var vm = new PlaylistTrackViewModel(evt.TrackModel, _eventBus, _libraryService, _artworkCache);
                if (evt.InitialState.HasValue) vm.State = evt.InitialState.Value;
                
                CurrentProjectTracks.Add(vm);
                RefreshFilteredTracks();
            }
        });
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
                    if (track is IDisposable disposable) disposable.Dispose();
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
            // Dispose existing tracks
            foreach (var track in CurrentProjectTracks)
            {
               if (track is IDisposable disposable) disposable.Dispose();
            }
            CurrentProjectTracks.Clear();
            return;
        }

        try
        {
            _logger.LogInformation("Loading tracks for project: {Name} (Virtualized)", job.SourceTitle);
            
            // Cleanup existing
            foreach (var track in CurrentProjectTracks)
            {
               if (track is IDisposable disposable) disposable.Dispose();
            }
            CurrentProjectTracks.Clear();

            // Set up virtualization
            var virtualized = new VirtualizedTrackCollection(
                _libraryService, 
                _eventBus, 
                _artworkCache, 
                job.Id, 
                SearchText, 
                IsFilterDownloaded ? true : (IsFilterPending ? false : null));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                FilteredTracks = virtualized;
                _logger.LogInformation("Virtualized collection initialized for project {Title}", job.SourceTitle);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize virtualized track loading");
        }
    }

    /// <summary>
    /// Phase 23: Loads tracks for a Smart Crate (Dynamic Playlist).
    /// </summary>
    public async Task LoadSmartCrateAsync(List<string> trackGlobalIds)
    {
        try
        {
             _logger.LogInformation("Loading Smart Crate with {Count} tracks", trackGlobalIds.Count);
             
             // Dispose existing
            foreach (var track in CurrentProjectTracks)
            {
               if (track is IDisposable disposable) disposable.Dispose();
            }
            
            var tracks = new ObservableCollection<PlaylistTrackViewModel>();
            
            // Bulk fetch library entries
            var entries = await _libraryService.GetLibraryEntriesByHashesAsync(trackGlobalIds);
            
            _logger.LogInformation("Resolved {Count} library entries for crate", entries.Count);
            
            foreach (var entry in entries)
            {
                 // Create VM (in-memory only, no PlaylistTrack ID relation yet)
                 var vm = new PlaylistTrackViewModel(
                    new PlaylistTrack
                    {
                        Id = Guid.NewGuid(), // Ephemeral ID
                        PlaylistId = Guid.Empty,
                        TrackUniqueHash = entry.UniqueHash,
                        Artist = entry.Artist,
                        Title = entry.Title,
                        Album = entry.Album,
                        Status = TrackStatus.Downloaded, // Assume downloaded
                        ResolvedFilePath = entry.FilePath,
                        Format = entry.Format
                    },
                    _eventBus,
                    _libraryService,
                    _artworkCache
                );
                
                // Try to sync with Global State if available in MainViewModel (for active status)
                // Accessing MainViewModel requires traversing parents or injection.
                // Current architecture: We don't have MainViewModel injected here directly?
                // We do have OnGlobalTrackUpdated event handling though.
                
                tracks.Add(vm);
            }
            
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CurrentProjectTracks = tracks;
            });
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Failed to load Smart Crate");
        }
    }

    /// <summary>
    /// Refreshes the filtered tracks based on current filter settings.
    /// Optimized with batch updates for virtualization performance.
    /// </summary>
    private void SeparateStems()
    {
         if (SelectedTracks == null || !SelectedTracks.Any()) return;

         foreach (var track in SelectedTracks.ToList())
         {
             if (track.SeparateStemsCommand.CanExecute(null))
             {
                 track.SeparateStemsCommand.Execute(null);
             }
         }
    }

    public void RefreshFilteredTracks()
    {
        // For virtualized view, we recreate the collection with the new filters
        // This is efficient because it only loads the first page.
        
        var selectedProjectId = _mainViewModel?.LibraryViewModel?.SelectedProject?.Id ?? Guid.Empty;

        // Dispose existing virtualized collection if any
        if (FilteredTracks is VirtualizedTrackCollection existingVtc)
        {
            existingVtc.Dispose();
        }

        var virtualized = new VirtualizedTrackCollection(
            _libraryService, 
            _eventBus, 
            _artworkCache, 
            selectedProjectId, 
            SearchText, 
            IsFilterDownloaded ? true : (IsFilterPending ? false : null));

        FilteredTracks = virtualized;
        
        // Update limited view for Cards to prevent UI freeze
        this.RaisePropertyChanged(nameof(LimitedTracks));
        
        _logger.LogDebug("RefreshFilteredTracks (Virtualized): Updated filters for project {Id}", selectedProjectId);

        // Notify Hierarchical (TreeDataGrid) - this still needs a full list or smart hierarchy
        // For now, let's skip updating hierarchy for All Tracks if it's too huge, or only update with first page
        // Hierarchical.UpdateTracks(firstPage); // TODO: Hierarchical virtualization
    }

    private bool FilterTracks(object obj)
    {
        if (obj is not PlaylistTrackViewModel track) return false;

        // Apply state filter first
        if (!IsFilterAll)
        {
            if (IsFilterNeedsReview && !track.IsReviewNeeded)
                return false;

            if (IsFilterDownloaded && track.State != PlaylistTrackState.Completed)
                return false;

            if (IsFilterPending && track.State == PlaylistTrackState.Completed)
                return false;
        }

        // Phase 15: Style Filtering
        // If NO styles are selected, show ALL (ignore this filter level).
        // If ANY styles are selected, track must match ONE of them.
        var selectedStyles = StyleFilters.Where(s => s.IsSelected).ToList();
        if (selectedStyles.Any())
        {
            var trackStyle = track.Model.DetectedSubGenre;
            if (string.IsNullOrEmpty(trackStyle)) return false; // No style = filtered out if filter active

            bool match = false;
            foreach (var style in selectedStyles)
            {
                 if (string.Equals(trackStyle, style.Style.Name, StringComparison.OrdinalIgnoreCase))
                 {
                     match = true;
                     break;
                 }
            }
            if (!match) return false;
        }
        
        // Phase 22: The Bouncer (Quality Control)
        if (IsBouncerActive)
        {
             // Filter out < 256kbps or unanalyzed tracks
             // Note: FLAC usually has Bitrate 0 or 1000+ in our simpler model, need to check
             // BitrateScore is usually the robust one.
             if (track.Model.BitrateScore.HasValue && track.Model.BitrateScore.Value < 256)
             {
                 return false;
             }
             // Also filter suspicious integrity if we want to be strict
             if (track.Model.Integrity == Data.IntegrityLevel.Suspicious)
             {
                 return false;
             }
        }
        
        // Phase 22: Vibe Filter (Mood)
        if (!string.IsNullOrEmpty(VibeFilter))
        {
             // Match MoodTag (e.g. "Aggressive", "Chill")
             if (!string.Equals(track.Model.MoodTag, VibeFilter, StringComparison.OrdinalIgnoreCase))
             {
                 return false;
             }
        }

        // Apply search filter
        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        var search = SearchText.Trim();
        return (track.Artist?.Contains(search, StringComparison.OrdinalIgnoreCase) == true) ||
               (track.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
    }

    private void UpdateSelectionState()
    {
        var count = SelectedTracks.Count;
        HasSelectedTracks = count > 0;
        HasMultiSelection = count > 1;
        SelectedCountText = $"{count} tracks selected";
        this.RaisePropertyChanged(nameof(LeadSelectedTrack));
    }

    private async Task ExecuteBulkDownloadAsync()
    {
        var selectedTracks = SelectedTracks.ToList();
        if (!selectedTracks.Any()) return;

        if (_bulkCoordinator.IsRunning) return;

        await _bulkCoordinator.RunOperationAsync(
            selectedTracks,
            async (track, ct) =>
            {
                _downloadManager.QueueTracks(new System.Collections.Generic.List<PlaylistTrack> { track.Model });
                return true;
            },
            "Bulk Download"
        );

        SelectedTracks.Clear();
    }

    private async Task ExecuteCopyToFolderAsync()
    {
        try
        {
            // Get selected completed tracks only
            var selectedTracks = SelectedTracks
                .Where(t => t.State == PlaylistTrackState.Completed && !string.IsNullOrEmpty(t.Model?.ResolvedFilePath))
                .ToList();
            
            if (!selectedTracks.Any())
            {
                _logger.LogWarning("No completed tracks selected for copy");
                return;
            }

            _logger.LogInformation("Copy to folder: {Count} tracks selected", selectedTracks.Count);

            // Show folder picker dialog
            var folderTask = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var dialog = new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = "Select destination folder for tracks",
                    AllowMultiple = false
                };

                var mainWindow = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                if (mainWindow == null) return null;

                var result = await mainWindow.StorageProvider.OpenFolderPickerAsync(dialog);
                return result?.FirstOrDefault()?.Path.LocalPath;
            });

            var targetFolder = await folderTask;
            if (string.IsNullOrEmpty(targetFolder))
            {
                _logger.LogInformation("Copy cancelled - no folder selected");
                return;
            }

            _logger.LogInformation("Copying {Count} files to: {Folder}", selectedTracks.Count, targetFolder);

            await _bulkCoordinator.RunOperationAsync(
                selectedTracks,
                async (track, ct) =>
                {
                    try
                    {
                        var sourceFile = track.Model?.ResolvedFilePath;
                        if (string.IsNullOrEmpty(sourceFile) || !System.IO.File.Exists(sourceFile))
                        {
                            return false;
                        }

                        var fileName = System.IO.Path.GetFileName(sourceFile);
                        var targetFile = System.IO.Path.Combine(targetFolder, fileName);

                        // Handle duplicate filenames
                        int suffix = 1;
                        while (System.IO.File.Exists(targetFile))
                        {
                            var nameWithoutExt = System.IO.Path.GetFileNameWithoutExtension(fileName);
                            var ext = System.IO.Path.GetExtension(fileName);
                            targetFile = System.IO.Path.Combine(targetFolder, $"{nameWithoutExt} ({suffix}){ext}");
                            suffix++;
                        }

                        System.IO.File.Copy(sourceFile, targetFile, false);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to copy track: {Title}", track.Title);
                        return false;
                    }
                },
                "Copy to Folder"
            );

            SelectedTracks.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Copy to folder operation failed");
        }
    }

    private async Task ExecuteBulkRetryAsync()
    {
        var selectedTracks = SelectedTracks
            .Where(t => t.State == PlaylistTrackState.Failed || t.State == PlaylistTrackState.Cancelled)
            .ToList();
        
        if (!selectedTracks.Any()) return;

        if (_bulkCoordinator.IsRunning) return;

        await _bulkCoordinator.RunOperationAsync(
            selectedTracks,
            async (track, ct) =>
            {
                track.Resume();
                return true;
            },
            "Bulk Retry"
        );
        
        // Ensure DownloadManager resumes if paused
        _ = _downloadManager.StartAsync();
        SelectedTracks.Clear();
    }
    
    private async Task ExecuteBulkCancelAsync()
    {
        var selectedTracks = SelectedTracks
            .Where(t => t.IsActive)
            .ToList();
        
        if (!selectedTracks.Any()) return;

        if (_bulkCoordinator.IsRunning) return;

        await _bulkCoordinator.RunOperationAsync(
            selectedTracks,
            async (track, ct) =>
            {
                track.Cancel();
                return true;
            },
            "Bulk Cancel"
        );
        SelectedTracks.Clear();
    }

    private async Task ExecuteBulkReEnrichAsync()
    {
        var selectedTracks = SelectedTracks.ToList();
        if (!selectedTracks.Any()) return;

        if (_bulkCoordinator.IsRunning) return;

        await _bulkCoordinator.RunOperationAsync(
            selectedTracks,
            async (track, ct) =>
            {
                if (track.Model != null)
                {
                    await _enrichmentOrchestrator.QueueForEnrichmentAsync(track.Model.TrackUniqueHash, track.Model.PlaylistId);
                    return true;
                }
                return false;
            },
            "Bulk Re-Enrich"
        );
        
        SelectedTracks.Clear();
    }

    private void OnGlobalTrackUpdated(object? sender, PlaylistTrackViewModel e)
    {
        // Track updates are handled by the ViewModel itself via binding
    }

    /// <summary>
    /// Phase 18: Publishes FindSimilarRequestEvent to trigger sonic match search.
    /// </summary>
    private void ExecuteFindSimilar(PlaylistTrackViewModel? track)
    {
        if (track == null) 
        {
            // If no parameter, use lead selected track
            track = LeadSelectedTrack;
        }
        
        if (track == null || track.Model == null)
        {
            _logger.LogWarning("FindSimilar called with no track selected");
            return;
        }

        _logger.LogInformation("🎵 Find Similar: Publishing event for {Artist} - {Title}", track.Artist, track.Title);
        
        // Use existing Models.FindSimilarRequestEvent with PlaylistTrack and UseAi=true for AI matching
        _eventBus.Publish(new FindSimilarRequestEvent(track.Model, useAi: true));
    }
}
