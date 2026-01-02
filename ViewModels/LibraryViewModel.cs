using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Views;
using Avalonia.Controls.Selection; // Added for ITreeDataGridSelectionInteraction
using System.Reactive.Linq;
using Avalonia.Threading;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Coordinator ViewModel for the Library page.
/// Delegates responsibilities to child ViewModels following Single Responsibility Principle.
/// </summary>
public class LibraryViewModel : INotifyPropertyChanged
{
    private readonly ILogger<LibraryViewModel> _logger;
    private readonly INavigationService _navigationService;
    private readonly ImportHistoryViewModel _importHistoryViewModel;
    private readonly ILibraryService _libraryService; // Session 1: Critical bug fixes
    private readonly IEventBus _eventBus;
    private readonly Services.Export.RekordboxService _rekordboxService;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;
    private readonly SpotifyEnrichmentService _spotifyEnrichmentService; // Phase 5: Cache-First
    private readonly HarmonicMatchService _harmonicMatchService; // Phase 8: DJ Features
    private readonly AnalysisQueueService _analysisQueueService; // Analysis queue control
    private readonly Services.Library.SmartSorterService _smartSorterService; // Phase 16: Smart Sorter
    private readonly IServiceProvider _serviceProvider;
    private System.Threading.Timer? _selectionDebounceTimer; // Debounce for harmonic matching
    private System.Threading.CancellationTokenSource? _matchLoadCancellation; // Phase 9B: Cancel overlapping operations
    private Views.MainViewModel? _mainViewModel; // Reference to parent
    public Views.MainViewModel? MainViewModel
    {
        get => _mainViewModel;
        private set { _mainViewModel = value; OnPropertyChanged(); }
    }
    private bool _isLoading;

    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    // Phase 9: Mix Helper Sidebar
    private bool _isMixHelperVisible = false;
    public bool IsMixHelperVisible
    {
        get => _isMixHelperVisible;
        set { _isMixHelperVisible = value; OnPropertyChanged(); }
    }

    private PlaylistTrackViewModel? _mixHelperSeedTrack;
    public PlaylistTrackViewModel? MixHelperSeedTrack
    {
        get => _mixHelperSeedTrack;
        set { _mixHelperSeedTrack = value; OnPropertyChanged(); }
    }

    private System.Collections.ObjectModel.ObservableCollection<HarmonicMatchViewModel> _harmonicMatches = new();
    public System.Collections.ObjectModel.ObservableCollection<HarmonicMatchViewModel> HarmonicMatches
    {
        get => _harmonicMatches;
        set { _harmonicMatches = value; OnPropertyChanged(); }
    }

    private bool _isLoadingMatches;
    public bool IsLoadingMatches
    {
        get => _isLoadingMatches;
        set { _isLoadingMatches = value; OnPropertyChanged(); }
    }

    // Child ViewModels (Phase 0: ViewModel Refactoring)
    public Library.ProjectListViewModel Projects { get; }
    public Library.TrackListViewModel Tracks { get; }
    public Library.TrackOperationsViewModel Operations { get; }
    public Library.SmartPlaylistViewModel SmartPlaylists { get; }
    public TrackInspectorViewModel TrackInspector { get; }
    public UpgradeScoutViewModel UpgradeScout { get; }

    // Expose commonly used child properties for backward compatibility
    public PlaylistJob? SelectedProject 
    { 
        get => Projects.SelectedProject;
        set => Projects.SelectedProject = value;
    }
    
    public System.Collections.ObjectModel.ObservableCollection<PlaylistTrackViewModel> CurrentProjectTracks
    {
        get => Tracks.CurrentProjectTracks;
        set => Tracks.CurrentProjectTracks = value;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // UI State Properties
    private bool _isEditMode;
    public bool IsEditMode
    {
        get => _isEditMode;
        set
        {
            if (_isEditMode != value)
            {
                _isEditMode = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _isActiveDownloadsVisible;
    public bool IsActiveDownloadsVisible
    {
        get => _isActiveDownloadsVisible;
        set
        {
            if (_isActiveDownloadsVisible != value)
            {
                _isActiveDownloadsVisible = value;
                OnPropertyChanged();
            }
        }
    }


    // Commands that delegate to child ViewModels or handle coordination
    public System.Windows.Input.ICommand ViewHistoryCommand { get; }
    public System.Windows.Input.ICommand ToggleEditModeCommand { get; }
    public System.Windows.Input.ICommand ToggleActiveDownloadsCommand { get; }
    
    // Session 1: Critical bug fixes (3 commands to unblock user)
    public System.Windows.Input.ICommand PlayTrackCommand { get; }
    public System.Windows.Input.ICommand RefreshLibraryCommand { get; }
    public System.Windows.Input.ICommand DeleteProjectCommand { get; }
    public System.Windows.Input.ICommand PlayAlbumCommand { get; }
    public System.Windows.Input.ICommand DownloadAlbumCommand { get; }
    public System.Windows.Input.ICommand ExportMonthlyDropCommand { get; }
    public System.Windows.Input.ICommand FindHarmonicMatchesCommand { get; }
    public System.Windows.Input.ICommand ToggleMixHelperCommand { get; } // NEW
    public System.Windows.Input.ICommand ToggleInspectorCommand { get; } // Slide-in Inspector
    public System.Windows.Input.ICommand CloseInspectorCommand { get; } // NEW
    public System.Windows.Input.ICommand AnalyzeAlbumCommand { get; } // Queue album for analysis
    public System.Windows.Input.ICommand AnalyzeTrackCommand { get; } // Queue track for analysis
    public System.Windows.Input.ICommand ExportPlaylistCommand { get; } // Export to Rekordbox XML
    public System.Windows.Input.ICommand AutoSortCommand { get; } // Phase 16.1: Smart Sort
    public System.Windows.Input.ICommand LoadDeletedProjectsCommand { get; } // NEW
    public System.Windows.Input.ICommand RestoreProjectCommand { get; } // NEW

    private bool _isRemovalHistoryVisible;
    public bool IsRemovalHistoryVisible
    {
        get => _isRemovalHistoryVisible;
        set { _isRemovalHistoryVisible = value; OnPropertyChanged(); }
    }

    private System.Collections.ObjectModel.ObservableCollection<PlaylistJob> _deletedProjects = new();
    public System.Collections.ObjectModel.ObservableCollection<PlaylistJob> DeletedProjects
    {
        get => _deletedProjects;
        set { _deletedProjects = value; OnPropertyChanged(); }
    }

    private bool _isInspectorOpen;
    public bool IsInspectorOpen
    {
        get => _isInspectorOpen;
        set 
        {
            if (_isInspectorOpen != value)
            {
                _isInspectorOpen = value;
                OnPropertyChanged();
            }
        }
    }

    public LibraryViewModel(
        ILogger<LibraryViewModel> logger,
        Library.ProjectListViewModel projects,
        Library.TrackListViewModel tracks,
        Library.TrackOperationsViewModel operations,
        Library.SmartPlaylistViewModel smartPlaylists,
        INavigationService navigationService,
        ImportHistoryViewModel importHistoryViewModel,
        ILibraryService libraryService,
        IEventBus eventBus,
        PlayerViewModel playerViewModel,
        UpgradeScoutViewModel upgradeScout,
        TrackInspectorViewModel trackInspector,
        Services.Export.RekordboxService rekordboxService,
        IDialogService dialogService,
       INotificationService notificationService,
        SpotifyEnrichmentService spotifyEnrichmentService,
        HarmonicMatchService harmonicMatchService, // Phase 8: DJ Features
        AnalysisQueueService analysisQueueService, // Analysis queue
        Services.Library.SmartSorterService smartSorterService, // Phase 16
        IServiceProvider serviceProvider) // Factory for transient VMs
    {
        _logger = logger;
        _navigationService = navigationService;
        _importHistoryViewModel = importHistoryViewModel;
        _libraryService = libraryService;
        _eventBus = eventBus;
        _rekordboxService = rekordboxService;
        _dialogService = dialogService;
        _notificationService = notificationService;
        _spotifyEnrichmentService = spotifyEnrichmentService;
        _harmonicMatchService = harmonicMatchService;
        _analysisQueueService = analysisQueueService;
        _smartSorterService = smartSorterService;
        _serviceProvider = serviceProvider;
        
        // Assign child ViewModels
        Projects = projects;
        Tracks = tracks;
        Operations = operations;
        SmartPlaylists = smartPlaylists;
        PlayerViewModel = playerViewModel;
        UpgradeScout = upgradeScout;
        TrackInspector = trackInspector;
        
        // Initialize commands
        ViewHistoryCommand = new AsyncRelayCommand(ExecuteViewHistoryAsync);
        ToggleEditModeCommand = new RelayCommand<object>(_ => IsEditMode = !IsEditMode);
        ToggleActiveDownloadsCommand = new RelayCommand<object>(_ => IsActiveDownloadsVisible = !IsActiveDownloadsVisible);
        ToggleActiveDownloadsCommand = new RelayCommand<object>(_ => IsActiveDownloadsVisible = !IsActiveDownloadsVisible);
        
        // Session 1: Critical bug fixes
        PlayTrackCommand = new AsyncRelayCommand<PlaylistTrackViewModel>(ExecutePlayTrackAsync);
        RefreshLibraryCommand = new AsyncRelayCommand(ExecuteRefreshLibraryAsync);
        DeleteProjectCommand = new AsyncRelayCommand<PlaylistJob>(ExecuteDeleteProjectAsync, p => p != null || SelectedProject != null);
        PlayAlbumCommand = new AsyncRelayCommand<PlaylistJob>(ExecutePlayAlbumAsync);
        DownloadAlbumCommand = new AsyncRelayCommand<PlaylistJob>(ExecuteDownloadAlbumAsync);
        ExportMonthlyDropCommand = new AsyncRelayCommand(ExecuteExportMonthlyDropAsync);
        FindHarmonicMatchesCommand = new AsyncRelayCommand<PlaylistTrackViewModel>(ExecuteFindHarmonicMatchesAsync);
        ToggleMixHelperCommand = new RelayCommand<object>(_ => IsMixHelperVisible = !IsMixHelperVisible);
        
        LoadDeletedProjectsCommand = new AsyncRelayCommand(ExecuteLoadDeletedProjectsAsync);
        RestoreProjectCommand = new AsyncRelayCommand<PlaylistJob>(ExecuteRestoreProjectAsync);
        ToggleInspectorCommand = new RelayCommand<object>(param => 
        {
            if (param is PlaylistTrackViewModel track)
            {
                 TrackInspector.Track = track.Model;
                 // Ensure selection follows
                 if (!Tracks.SelectedTracks.Contains(track))
                 {
                     Tracks.SelectedTracks.Clear();
                     Tracks.SelectedTracks.Add(track);
                 }
                 IsInspectorOpen = true;
            }
            else
            {
                IsInspectorOpen = !IsInspectorOpen;
            }
        });
        CloseInspectorCommand = new RelayCommand<object>(_ => IsInspectorOpen = false); // NEW
        IsInspectorOpen = false; // NEW
        AnalyzeAlbumCommand = new AsyncRelayCommand<string>(ExecuteAnalyzeAlbumAsync);
        AnalyzeTrackCommand = new SLSKDONET.Views.RelayCommand<PlaylistTrackViewModel>(ExecuteAnalyzeTrack);
        ExportPlaylistCommand = new AsyncRelayCommand<PlaylistJob>(ExecuteExportPlaylistAsync);
        AutoSortCommand = new AsyncRelayCommand(ExecuteAutoSortAsync);
        
        
        // Wire up events between child ViewModels
        Projects.ProjectSelected += OnProjectSelected;
        SmartPlaylists.SmartPlaylistSelected += OnSmartPlaylistSelected;
        
        _logger.LogInformation("LibraryViewModel initialized with child ViewModels");

        // Subscribe to selection changes in Tracks.SelectedTracks (ListBox)
        Tracks.SelectedTracks.CollectionChanged += OnTrackSelectionChanged;
        
        // Subscribe to UpgradeScout close event
        
        // Phase 3: Post-Import Navigation - Auto-navigate to Library and select imported album
        _eventBus.GetEvent<ProjectAddedEvent>().Subscribe(OnProjectAdded);
        
        // Phase 6: Sync "All Tracks" LibraryEntry index on startup
        // This fixes the issue where the "All Tracks" view is empty because LibraryEntry wasn't populated.
        // It runs in the background and is safe to call repeatedly (idempotent).
        Task.Run(() => _libraryService.SyncLibraryEntriesFromTracksAsync()).ConfigureAwait(false);
    }
    
    private async void OnProjectAdded(ProjectAddedEvent evt)
    {
        try
        {
            _logger.LogInformation("[IMPORT TRACE] LibraryViewModel.OnProjectAdded: Received event for job {JobId}", evt.ProjectId);
            _logger.LogInformation("[IMPORT TRACE] Current AllProjects count: {Count}", Projects.AllProjects.Count);
            
            // Navigate to Library page
            _logger.LogInformation("[IMPORT TRACE] Navigating to Library page");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                _navigationService.NavigateTo("Library");
            });
            _logger.LogInformation("[IMPORT TRACE] Navigation to Library completed");
            
            // Give the UI time to update
            await Task.Delay(300);
            
            // Load projects to ensure the new one is in the list
            // NOTE: This may seem redundant with ProjectListViewModel.OnPlaylistAdded, but it ensures
            // the list is fully loaded when coming from import (LibraryPage.OnLoaded may not have fired yet)
            _logger.LogInformation("[IMPORT TRACE] Calling LoadProjectsAsync to refresh project list");
            await LoadProjectsAsync();
            _logger.LogInformation("[IMPORT TRACE] LoadProjectsAsync completed. AllProjects count: {Count}", Projects.AllProjects.Count);
            
            // Select the newly added project
            _logger.LogInformation("[IMPORT TRACE] Attempting to select project {JobId}", evt.ProjectId);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                var addedProject = Projects.AllProjects.FirstOrDefault(p => p.Id == evt.ProjectId);
                if (addedProject != null)
                {
                    Projects.SelectedProject = addedProject;
                    _logger.LogInformation("Auto-selected imported project: {Title}", addedProject.SourceTitle);
                }
                else
                {
                    _logger.LogWarning("Could not find project {JobId} in AllProjects after import", evt.ProjectId);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle post-import navigation for project {JobId}", evt.ProjectId);
        }
    }

    private async void OnTrackSelectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // We only care about the most recently selected item for the Inspector/Sidebars
        var lastSelected = Tracks.SelectedTracks.LastOrDefault();
        
        if (lastSelected is PlaylistTrackViewModel trackVm)
        {
            TrackInspector.Track = trackVm.Model;
            
            // Handle row expansion (Accordion style - collapse others)
            foreach (var t in Tracks.CurrentProjectTracks)
            {
                if (t.GlobalId != trackVm.GlobalId && t.IsExpanded)
                    t.IsExpanded = false;
            }
            trackVm.IsExpanded = true;
            
            // Phase 5 Hardening: Cache-First Enrichment Proxy
            // Attempt to hydrate from local cache immediately (Optimistic UI)
            if (!trackVm.Model.IsEnriched)
            {
                var cached = await _spotifyEnrichmentService.GetCachedMetadataAsync(trackVm.Artist, trackVm.Title);
                if (cached != null)
                {
                     _logger.LogDebug("Cache-Hit: Instant hydration for {Title}", trackVm.Title);
                     trackVm.Model.BPM = cached.Bpm;
                     trackVm.Model.Energy = cached.Energy;
                     trackVm.Model.Valence = cached.Valence;
                     trackVm.Model.Danceability = cached.Danceability;
                     trackVm.Model.IsEnriched = true;
                     
                     // Force property refresh on Inspector
                     TrackInspector.Track = null;
                     TrackInspector.Track = trackVm.Model;
                }
                else
                {
                    // Cache Miss: Do NOT call API directly.
                    // Let the Background Worker pick it up to preserve quota.
                    // Just show placeholder in UI (handled by TrackInspector properties).
                    _logger.LogDebug("Cache-Miss: Queued for background worker: {Title}", trackVm.Title);
                }
            }

            // Phase 9: Debounced Harmonic Matching
            // Phase 9B: Cancel previous operation and timer to prevent overlapping queries
            _matchLoadCancellation?.Cancel();
            _matchLoadCancellation?.Dispose();
            _matchLoadCancellation = new System.Threading.CancellationTokenSource();
            
            _selectionDebounceTimer = new System.Threading.Timer(
                _ => Avalonia.Threading.Dispatcher.UIThread.Post(() => { _ = LoadHarmonicMatchesAsync(trackVm, _matchLoadCancellation.Token); }),
                null,
                250, // Wait 250ms after last selection change
                System.Threading.Timeout.Infinite);

            // Phase 12.6: Publish global selection event to sync standalone Inspector Page
            _eventBus.Publish(new TrackSelectionChangedEvent(trackVm.Model));
        }
        else
        {
            // No selection
            TrackInspector.Track = null;
            _eventBus.Publish(new TrackSelectionChangedEvent(null));
        }
    }


    /// <summary>
    /// Loads all projects from the database.
    /// Delegates to ProjectListViewModel.
    /// </summary>
    public async Task LoadProjectsAsync()
    {
        await Projects.LoadProjectsAsync();
    }

    /// <summary>
    /// Handles project selection event from ProjectListViewModel.
    /// Coordinates loading tracks in TrackListViewModel.
    /// </summary>
    private async void OnProjectSelected(object? sender, PlaylistJob? project)
    {
        if (project == null) return;

        _logger.LogInformation("Project selected: {Title}", project.SourceTitle);
        IsLoading = true;
        try
        {
            // Deselect smart playlist
            if (SmartPlaylists.SelectedSmartPlaylist != null)
            {
                SmartPlaylists.SelectedSmartPlaylist = null;
            }
            
            // Load tracks for selected project
            await Tracks.LoadProjectTracksAsync(project);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Handles smart playlist selection event from SmartPlaylistViewModel.
    /// Coordinates updating track list.
    /// </summary>
    private void OnSmartPlaylistSelected(object? sender, Library.SmartPlaylist? playlist)
    {
        if (playlist == null) return;

        _logger.LogInformation("Smart playlist selected: {Name}", playlist.Name);
        IsLoading = true;
        try
        {
            // Deselect project
            if (Projects.SelectedProject != null)
            {
                Projects.SelectedProject = null;
            }
            
            // Refresh smart playlist tracks
            var tracks = SmartPlaylists.RefreshSmartPlaylist(playlist);
            Tracks.CurrentProjectTracks = tracks;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Opens the import history view.
    /// </summary>
    private async Task ExecuteViewHistoryAsync()
    {
        try
        {
            _logger.LogInformation("Opening import history");
            _navigationService.NavigateTo("ImportHistory");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open import history");
        }
    }


    // Session 1: Critical command implementations
    
    /// <summary>
    /// Plays a track from the library.
    /// </summary>
    private async Task ExecutePlayTrackAsync(PlaylistTrackViewModel? track)
    {
        if (track == null)
        {
            _logger.LogWarning("PlayTrack called with null track");
            return;
        }
        
        if (string.IsNullOrEmpty(track.Model.ResolvedFilePath))
        {
            _logger.LogWarning("Cannot play track without file path: {Title}", track.Title);
            return;
        }
        
        try
        {
            _logger.LogInformation("Playing track: {Title} from {Path}", track.Title, track.Model.ResolvedFilePath);
            
            // Phase 6B: Decoupled playback request via EventBus
            _eventBus.Publish(new PlayTrackRequestEvent(track));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to play track: {Title}", track.Title);
        }
    }
    
    /// <summary>
    /// Refreshes the library by reloading projects from database.
    /// </summary>
    private async Task ExecuteRefreshLibraryAsync()
    {
        try
        {
            _logger.LogInformation("Refreshing library...");
            await Projects.LoadProjectsAsync();
            
            // If a project is selected, reload its tracks
            if (SelectedProject != null)
            {
                await Tracks.LoadProjectTracksAsync(SelectedProject);
            }
            
            _logger.LogInformation("Library refreshed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh library");
        }
    }
    
    /// <summary>
    /// Deletes a project/playlist from the library.
    /// </summary>
    private async Task ExecuteDeleteProjectAsync(PlaylistJob? project)
    {
        // Use either the passed project or the selected one
        var target = project ?? SelectedProject;
        
        if (target == null)
        {
            _logger.LogWarning("DeleteProject called with null project");
            return;
        }
        
        try
        {
            _logger.LogInformation("Deleting project: {Title}", target.SourceTitle);
            
            // TODO: Add confirmation dialog in Phase 6 redesign
            // For now, delete directly
            await _libraryService.DeletePlaylistJobAsync(target.Id);
            
            // Reload projects list
            await Projects.LoadProjectsAsync();
            
            // Clear selected project if it was deleted
            if (SelectedProject?.Id == target.Id)
            {
                SelectedProject = null;
                Tracks.CurrentProjectTracks.Clear();
            }
            
            _logger.LogInformation("Project deleted successfully: {Title}", target.SourceTitle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete project: {Title}", target.SourceTitle);
        }
    }

    private async Task ExecuteLoadDeletedProjectsAsync()
    {
        try
        {
            IsLoading = true;
            IsRemovalHistoryVisible = true;
            var deleted = await _libraryService.LoadDeletedPlaylistJobsAsync();
            
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                DeletedProjects.Clear();
                foreach (var p in deleted)
                {
                    DeletedProjects.Add(p);
                }
            });
            
            _logger.LogInformation("Loaded {Count} deleted projects", deleted.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load deleted projects");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ExecuteRestoreProjectAsync(PlaylistJob? project)
    {
        if (project == null) return;
        
        try
        {
            _logger.LogInformation("Restoring project: {Title}", project.SourceTitle);
            await _libraryService.RestorePlaylistJobAsync(project.Id);
            
            // Remove from current deleted list
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                DeletedProjects.Remove(project);
            });
            
            // Reload main list
            await Projects.LoadProjectsAsync();
            
            _logger.LogInformation("Project restored successfully: {Title}", project.SourceTitle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore project: {Title}", project.SourceTitle);
        }
    }

    public PlayerViewModel PlayerViewModel { get; }

    private async Task ExecutePlayAlbumAsync(PlaylistJob? job)
    {
        if (job == null) return;
        _logger.LogInformation("Playing album: {Title}", job.SourceTitle);
        
        // Find all tracks for this job and play the first one (or add all to queue)
        var tracks = await _libraryService.LoadPlaylistTracksAsync(job.Id);
        var firstValid = tracks.FirstOrDefault(t => !string.IsNullOrEmpty(t.ResolvedFilePath));
        
        if (firstValid != null)
        {
            // Queue all tracks that have a resolved file path
            var validTracks = tracks.Where(t => !string.IsNullOrEmpty(t.ResolvedFilePath)).ToList();
            if (validTracks.Any())
            {
                _eventBus.Publish(new PlayAlbumRequestEvent(validTracks));
                _logger.LogInformation("Queued {Count} tracks for album {Title}", validTracks.Count, job.SourceTitle);
            }
        }
    }

    private async Task ExecuteDownloadAlbumAsync(PlaylistJob? job)
    {
        if (job == null)
        {
            _logger.LogWarning("❌ ExecuteDownloadAlbumAsync called with NULL job");
            return;
        }
        
        _logger.LogInformation("🔽 DOWNLOAD BUTTON CLICKED: Album: {Title}, JobId: {Id}", job.SourceTitle, job.Id);
        
        try
        {
            // Publish event to DownloadManager  
            _eventBus.Publish(new DownloadAlbumRequestEvent(job));
            _logger.LogInformation("✅ DownloadAlbumRequestEvent published for {Title}", job.SourceTitle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to publish DownloadAlbumRequestEvent for {Title}", job.SourceTitle);
        }
    }

    /// <summary>
    /// Phase 6D: Updates a track's project association (used for D&D).
    /// </summary>
    public async Task UpdateTrackProjectAsync(string trackGlobalId, Guid newProjectId)
    {
        try
        {
            var project = await _libraryService.FindPlaylistJobAsync(newProjectId);
            if (project == null) return;

            // Find track in current project tracks or global
            var track = Tracks.CurrentProjectTracks.FirstOrDefault(t => t.GlobalId == trackGlobalId)
                      ?? _mainViewModel?.AllGlobalTracks.FirstOrDefault(t => t.GlobalId == trackGlobalId);

            if (track != null)
            {
                var oldProjectId = track.Model.PlaylistId;
                if (oldProjectId == newProjectId) return;

                _logger.LogInformation("Moving track {Title} from {Old} to {New}", track.Title, oldProjectId, newProjectId);
                
                // Update DB
                track.Model.PlaylistId = newProjectId;
                await _libraryService.UpdatePlaylistTrackAsync(track.Model);

                // Publish event for local UI sync
                _eventBus.Publish(new TrackMovedEvent(trackGlobalId, oldProjectId, newProjectId));
                
                if (_mainViewModel != null)
                    _mainViewModel.StatusText = $"Moved '{track.Title}' to '{project.SourceTitle}'";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update track project association");
            if (_mainViewModel != null)
                _mainViewModel.StatusText = "Error: Failed to move track.";
        }
    }

    private async Task ExecuteExportMonthlyDropAsync()
    {
        try
        {
            // Prompt user for save location
            var defaultName = $"Orbit Drop {DateTime.Now:MMM yyyy}.xml";
            var savePath = await _dialogService.SaveFileAsync("Export Monthly Drop", defaultName, "xml");
            
            if (string.IsNullOrEmpty(savePath)) return; // User cancelled

            _logger.LogInformation("Exporting Monthly Drop to {Path}", savePath);
            
            var exportedCount = await _rekordboxService.ExportMonthlyDropAsync(30, savePath);
            
            if (exportedCount > 0)
            {
                _notificationService.Show(
                    "Export Successful", 
                    $"Exported {exportedCount} new tracks from the last 30 days",
                    NotificationType.Success);
            }
            else
            {
                _notificationService.Show(
                    "No New Tracks", 
                    "No tracks added in the last 30 days",
                    NotificationType.Warning);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export Monthly Drop");
            _notificationService.Show(
                "Export Failed", 
                $"Error: {ex.Message}",
                NotificationType.Error);
        }
    }

    public void AddToPlaylist(PlaylistJob targetPlaylist, PlaylistTrackViewModel sourceTrack)
    {
        _ = UpdateTrackProjectAsync(sourceTrack.GlobalId, targetPlaylist.Id);
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public void SetMainViewModel(Views.MainViewModel mainViewModel)
    {
        MainViewModel = mainViewModel;
        
        // BUGFIX: Propagate MainViewModel to child ViewModels that depend on it
        // TrackListViewModel needs _mainViewModel for AllGlobalTracks sync
        Tracks.SetMainViewModel(mainViewModel);
    }

    /// <summary>
    /// Phase 16.1: Auto-Sorts Library based on AI predictions.
    /// </summary>
    private async Task ExecuteAutoSortAsync()
    {
        try
        {
            _logger.LogInformation("Initiating Auto-Sort...");
            IsLoading = true;

            // 1. Gather Tracks (Use current project or selection, or all?)
            // For safety, let's start with "Selected Tracks" if any, else "Current View"
            var targetTracks = (Tracks.SelectedTracks.Count > 0 
                ? Tracks.SelectedTracks.Select(vm => vm.Model) 
                : Tracks.CurrentProjectTracks.Select(vm => vm.Model))
                .Select(pt => new Models.LibraryEntry
                {
                    UniqueHash = pt.TrackUniqueHash,
                    Artist = pt.Artist,
                    Title = pt.Title,
                    Album = pt.Album,
                    FilePath = pt.ResolvedFilePath
                })
                .ToList();

            if (!targetTracks.Any())
            {
                _notificationService.Show("No Tracks", "Select tracks or a playlist to organize.", NotificationType.Warning);
                return;
            }

            // 2. Plan the Sort (Dry Run)
            var ops = await _smartSorterService.PlanSortAsync(targetTracks);

            if (!ops.Any())
            {
                _notificationService.Show("Nothing to Sort", "All tracks are already organized or lack high-confidence predictions.", NotificationType.Information);
                return;
            }

            // 3. Show Preview Dialog
            // Use Factory pattern via ServiceProvider
            var vm = _serviceProvider.GetService(typeof(ViewModels.Tools.SortPreviewViewModel)) as ViewModels.Tools.SortPreviewViewModel;
            if (vm == null) throw new InvalidOperationException("Could not create SortPreviewViewModel");
            
            vm.LoadOperations(ops);

            var confirmed = await _dialogService.ShowSortPreviewAsync(vm);
            
            if (confirmed)
            {
                _notificationService.Show("Organization Complete", $"Sorted {ops.Count(o => o.Status == "Success")} tracks.", NotificationType.Success);
                // Refresh list if needed (Files moved, paths changed in DB, but ViewModels might need refresh)
                await Tracks.LoadProjectTracksAsync(SelectedProject); 
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-Sort failed");
            _notificationService.Show("Error", $"Sort failed: {ex.Message}", NotificationType.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Phase 8: Finds harmonically compatible tracks using Camelot Wheel theory.
    /// </summary>
    private async Task ExecuteFindHarmonicMatchesAsync(PlaylistTrackViewModel? seedTrack)
    {
        if (seedTrack == null)
        {
            _logger.LogWarning("No track selected for harmonic matching");
            return;
        }

        try
        {
             // ... existing harmonic implementation ...

            _logger.LogInformation("Finding harmonic matches for track: {TrackTitle}", seedTrack.Title);

            // Find matches using HarmonicMatchService
            var matches = await _harmonicMatchService.GetHarmonicMatchesAsync(
                seedTrack.Model.Id,
                limit: 20,
                includeBpmRange: true,
                includeEnergyMatch: true);

            if (!matches.Any())
            {
                 _notificationService.Show(
                    "Harmonic Matches",
                    $"No compatible tracks found for '{seedTrack.Title}'. Ensure tracks have Key and BPM metadata.",
                    NotificationType.Information);
                return;
            }

            // Build friendly message
            var matchSummary = new System.Text.StringBuilder();
            matchSummary.AppendLine($"🎹 Found {matches.Count} tracks that mix well with:");
            matchSummary.AppendLine($"'{seedTrack.Title}' by {seedTrack.Artist}");
            matchSummary.AppendLine();
            matchSummary.AppendLine("Top Matches:");

            var topMatches = matches.Take(5);
            foreach (var match in topMatches)
            {
                var relationship = match.KeyRelationship switch
                {
                    KeyRelationship.Perfect => "❤️ Perfect",
                    KeyRelationship.Compatible => "💚 Compatible",
                    KeyRelationship.Relative => "💙 Relative",
                    _ => "⚪"
                };

                var bpmInfo = match.BpmDifference.HasValue 
                    ? $" (±{match.BpmDifference:F0} BPM)" 
                    : "";

                matchSummary.AppendLine(
                    $"{relationship} [{match.CompatibilityScore:F0}%] - {match.Track.Artist} - {match.Track.Title}{bpmInfo}");
            }

            if (matches.Count > 5)
            {
                matchSummary.AppendLine($"\n...and {matches.Count - 5} more");
            }

             _notificationService.Show(
                "Harmonic Matches",
                matchSummary.ToString(),
                NotificationType.Success);

            _logger.LogInformation("Found {Count} harmonic matches", matches.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find harmonic matches");
             _notificationService.Show(
                "Harmonic Matches",
                $"Error: {ex.Message}",
                NotificationType.Error);
        }
    }

    /// <summary>
    /// Phase 9: Loads harmonic matches for the Mix Helper sidebar (debounced).
    /// Phase 9B: Now accepts CancellationToken to prevent overlapping operations.
    /// </summary>
    private async Task LoadHarmonicMatchesAsync(PlaylistTrackViewModel seedTrack, System.Threading.CancellationToken cancellationToken = default)
    {
        try
        {
            IsLoadingMatches = true;
            MixHelperSeedTrack = seedTrack;
            HarmonicMatches.Clear();

            _logger.LogDebug("Loading harmonic matches for: {Title}", seedTrack.Title);

            // Check for cancellation before expensive operation
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Harmonic match loading cancelled for: {Title}", seedTrack.Title);
                return;
            }

            // Find matches
            var matches = await _harmonicMatchService.GetHarmonicMatchesAsync(
                seedTrack.Model.Id,
                limit: 10, // Sidebar shows top 10
                includeBpmRange: true,
                includeEnergyMatch: true);

            // Check cancellation again after async operation
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Harmonic match loading cancelled after query for: {Title}", seedTrack.Title);
                return;
            }

            // Convert to ViewModel
            foreach (var match in matches)
            {
                HarmonicMatches.Add(new HarmonicMatchViewModel
                {
                    Track = match.Track,
                    CompatibilityScore = match.CompatibilityScore,
                    Relationship = match.KeyRelationship,
                    BpmDifference = match.BpmDifference
                });
            }

            _logger.LogDebug("Loaded {Count} matches for Mix Helper", matches.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load harmonic matches for sidebar");
        }
        finally
        {
            IsLoadingMatches = false;
        }
    }
    
    // Track Analysis Priority
    private void ExecuteAnalyzeTrack(PlaylistTrackViewModel? track)
    {
        if (track?.Model == null || string.IsNullOrEmpty(track.Model.ResolvedFilePath)) 
        {
            _notificationService.Show("Analysis Failed", "Track must be downloaded and have a valid file path.", NotificationType.Warning);
            return;
        }

        try 
        {
            // Use Priority Queue for manual triggers
            _analysisQueueService.QueueTrackWithPriority(track.Model);
            _notificationService.Show("Analysis Queued", $"Queued '{track.Title}' for priority analysis", SLSKDONET.Views.NotificationType.Success);
            _logger.LogInformation("Queued track '{Title}' for priority analysis", track.Title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue track for analysis");
            _notificationService.Show("Error", "Failed to queue track for analysis", SLSKDONET.Views.NotificationType.Error);
        }
    }

    // Album Analysis Queue Priority
    private async Task ExecuteAnalyzeAlbumAsync(string? albumName)
    {
        if (string.IsNullOrEmpty(albumName)) return;
        
        try
        {
            // Get all tracks for this album from current view
            var albumTracks = Tracks.FilteredTracks
                .Where(t => t.Album == albumName && t.Model.Status == TrackStatus.Downloaded)
                .Select(vm => vm.Model)
                .ToList();
            
            if (albumTracks.Count == 0)
            {
                await _dialogService.ShowAlertAsync("No Tracks", $"No downloaded tracks found for album '{albumName}'");
                return;
            }
            
            // Queue for analysis
            var count = _analysisQueueService.QueueAlbumWithPriority(albumTracks);
            
            _notificationService.Show("Analysis Queued", $"Queued {count} tracks from '{albumName}' for analysis", SLSKDONET.Views.NotificationType.Success);
            _logger.LogInformation("Queued {Count} tracks from album '{Album}' for priority analysis", count, albumName);
        }
        catch (Exception)
        {
            await _dialogService.ShowAlertAsync("Error", "Failed to queue album for analysis");
        }
    }


    private async Task ExecuteExportPlaylistAsync(PlaylistJob? playlist)
    {
        if (playlist == null) return;

        try
        {
            var defaultName = $"{playlist.SourceTitle}_Rekordbox.xml";
            var savePath = await _dialogService.SaveFileAsync("Export Playlist to Rekordbox", defaultName, "xml");

            if (string.IsNullOrEmpty(savePath)) return;

            _logger.LogInformation("Exporting playlist {Playlist} to {Path}", playlist.SourceTitle, savePath);

            var count = await _rekordboxService.ExportPlaylistAsync(playlist, savePath);

            if (count > 0)
            {
                _notificationService.Show(
                    "Export Complete",
                    $"Successfully exported {count} tracks from '{playlist.SourceTitle}' to Rekordbox XML.",
                    NotificationType.Success);
            }
            else
            {
                 _notificationService.Show(
                    "Export Warning",
                    "No valid tracks found to export in this playlist.",
                    NotificationType.Warning);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export playlist {Playlist}", playlist.SourceTitle);
            _notificationService.Show(
                "Export Failed",
                $"Error exporting playlist: {ex.Message}",
                NotificationType.Error);
        }
    }
}
