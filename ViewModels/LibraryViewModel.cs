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
        TrackInspectorViewModel trackInspector) // Refactor: Inject Singleton Inspector
    {
        _logger = logger;
        _navigationService = navigationService;
        _importHistoryViewModel = importHistoryViewModel;
        _libraryService = libraryService;
        _eventBus = eventBus;
        
        // Assign child ViewModels
        Projects = projects;
        Tracks = tracks;
        Operations = operations;
        SmartPlaylists = smartPlaylists;
        
        // Initialize commands
        ViewHistoryCommand = new AsyncRelayCommand(ExecuteViewHistoryAsync);
        ToggleEditModeCommand = new RelayCommand<object>(_ => IsEditMode = !IsEditMode);
        ToggleActiveDownloadsCommand = new RelayCommand<object>(_ => IsActiveDownloadsVisible = !IsActiveDownloadsVisible);
        ToggleActiveDownloadsCommand = new RelayCommand<object>(_ => IsActiveDownloadsVisible = !IsActiveDownloadsVisible);
        
        // Session 1: Critical bug fixes
        PlayTrackCommand = new AsyncRelayCommand<PlaylistTrackViewModel>(ExecutePlayTrackAsync);
        RefreshLibraryCommand = new AsyncRelayCommand(ExecuteRefreshLibraryAsync);
        DeleteProjectCommand = new AsyncRelayCommand<PlaylistJob>(ExecuteDeleteProjectAsync);
        PlayAlbumCommand = new AsyncRelayCommand<PlaylistJob>(ExecutePlayAlbumAsync);
        DownloadAlbumCommand = new AsyncRelayCommand<PlaylistJob>(ExecuteDownloadAlbumAsync);
        
        PlayerViewModel = playerViewModel;
        UpgradeScout = upgradeScout;
        
        // Wire up events between child ViewModels
        Projects.ProjectSelected += OnProjectSelected;
        SmartPlaylists.SmartPlaylistSelected += OnSmartPlaylistSelected;
        
        _logger.LogInformation("LibraryViewModel initialized with child ViewModels");

        TrackInspector = trackInspector;

        // Subscribe to selection changes in Tracks.Hierarchical.Source.Selection
        if (Tracks.Hierarchical.Source.Selection is ITreeDataGridSelectionInteraction selectionInteraction)
        {
            selectionInteraction.SelectionChanged += OnTrackSelectionChanged;
        }
        
        // Subscribe to UpgradeScout close event
        
        // Phase 3: Post-Import Navigation - Auto-navigate to Library and select imported album
        _eventBus.GetEvent<ProjectAddedEvent>().Subscribe(OnProjectAdded);
    }
    
    private async void OnProjectAdded(ProjectAddedEvent evt)
    {
        try
        {
            _logger.LogInformation("ProjectAddedEvent received for job {JobId}, navigating to library", evt.ProjectId);
            
            // Navigate to Library page
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                _navigationService.NavigateTo("Library");
            });
            
            // Give the UI time to update
            await Task.Delay(300);
            
            // Load projects to ensure the new one is in the list
            await LoadProjectsAsync();
            
            // Select the newly added project
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

    private void OnTrackSelectionChanged(object? sender, EventArgs e)
    {
        var selection = Tracks.Hierarchical.Source.Selection as ITreeDataGridRowSelectionModel<PlaylistTrackViewModel>;
        var selectedItem = selection?.SelectedItem;
        if (selectedItem is PlaylistTrackViewModel trackVm)
        {
            TrackInspector.Track = trackVm.Model;
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
        if (project == null)
        {
            _logger.LogWarning("DeleteProject called with null project");
            return;
        }
        
        try
        {
            _logger.LogInformation("Deleting project: {Title}", project.SourceTitle);
            
            // TODO: Add confirmation dialog in Phase 6 redesign
            // For now, delete directly
            await _libraryService.DeletePlaylistJobAsync(project.Id);
            
            // Reload projects list
            await Projects.LoadProjectsAsync();
            
            // Clear selected project if it was deleted
            if (SelectedProject?.Id == project.Id)
            {
                SelectedProject = null;
                Tracks.CurrentProjectTracks.Clear();
            }
            
            _logger.LogInformation("Project deleted successfully: {Title}", project.SourceTitle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete project: {Title}", project.SourceTitle);
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
            // For now, just play the first track. 
            // TODO: Add all to queue properly via EventBus
            var vm = new PlaylistTrackViewModel(firstValid, _eventBus);
            _eventBus.Publish(new PlayTrackRequestEvent(vm));
        }
    }

    private async Task ExecuteDownloadAlbumAsync(PlaylistJob? job)
    {
        if (job == null) return;
        _logger.LogInformation("Downloading album: {Title}", job.SourceTitle);
        // Business logic for downloading album already exists in DownloadManager?
        // Actually, we usually queue the whole project.
        _eventBus.Publish(new DownloadAlbumRequestEvent(job));
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
    }
}
