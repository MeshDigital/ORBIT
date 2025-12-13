using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services;
using System.Windows.Data;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

public class LibraryViewModel : INotifyPropertyChanged
{
    private readonly ILogger<LibraryViewModel> _logger;
    private readonly DownloadManager _downloadManager;

    private readonly ILibraryService _libraryService;
    private readonly PlayerViewModel _playerViewModel;
    private readonly ImportHistoryViewModel _importHistoryViewModel;
    private readonly INavigationService _navigationService;
    private readonly IUserInputService _userInputService;


    private bool FilterTracks(object obj)
    {
        if (obj is not PlaylistTrackViewModel track) return false;
        
        // Apply state filter first (only if not showing all)
        if (!IsFilterAll)
        {
            if (IsFilterDownloaded && track.State != PlaylistTrackState.Completed)
                return false;
            
            if (IsFilterPending && track.State == PlaylistTrackState.Completed)
                return false;
        }
        
        // Then apply search filter
        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        var search = SearchText.Trim();
        return (track.Artist?.Contains(search, StringComparison.OrdinalIgnoreCase) == true) ||
               (track.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
    }

    // Master/Detail pattern properties
    private ObservableCollection<PlaylistJob> _allProjects = new();
    private PlaylistJob? _selectedProject;
    private ObservableCollection<PlaylistTrackViewModel> _currentProjectTracks = new();
    private string _noProjectSelectedMessage = "Select an import job to view its tracks";
    private readonly PlaylistJob _allTracksJob = new() { Id = Guid.Empty, SourceTitle = "All Tracks", SourceType = "Global Library" };

    public ICommand HardRetryCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenProjectCommand { get; }
    public ICommand DeleteProjectCommand { get; }
    public ICommand RefreshLibraryCommand { get; }
    public ICommand PauseProjectCommand { get; }
    public ICommand ResumeProjectCommand { get; }
    public ICommand LoadAllTracksCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand RemoveTrackCommand { get; }
    public ICommand AddPlaylistCommand { get; }
    public ICommand PlayTrackCommand { get; }

    public ICommand ViewHistoryCommand { get; }

    // Master List: All import jobs/projects
    public ObservableCollection<PlaylistJob> AllProjects
    {
        get => _allProjects;
        set { _allProjects = value; OnPropertyChanged(); }
    }

    // Selected project
    public PlaylistJob? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (_selectedProject != value)
            {
                _selectedProject = value;
                OnPropertyChanged();
                if (value != null)
                    _ = LoadProjectTracksAsync(value);
            }
        }
    }

    public ObservableCollection<PlaylistTrackViewModel> CurrentProjectTracks
    {
        get => _currentProjectTracks;
        set 
        { 
            _currentProjectTracks = value; 
            OnPropertyChanged();
            
            // Re-create Filtered View when collection changes
            var filteredView = new ListCollectionView(_currentProjectTracks);
            filteredView.Filter = FilterTracks;
            filteredView.IsLiveFiltering = true;
            filteredView.LiveFilteringProperties.Add(nameof(PlaylistTrackViewModel.State));
            filteredView.LiveFilteringProperties.Add(nameof(PlaylistTrackViewModel.Artist));
            filteredView.LiveFilteringProperties.Add(nameof(PlaylistTrackViewModel.Title));
            
            FilteredTracks = filteredView;
            OnPropertyChanged(nameof(FilteredTracks));
        }
    }

    public ICollectionView? FilteredTracks { get; private set; }
    public ICollectionView ActiveDownloadsView { get; private set; } // For HUD

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText != value)
            {
                _searchText = value;
                OnPropertyChanged();
                FilteredTracks?.Refresh();
            }
        }
    }
    
    // Filter state properties
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
                if (value) FilteredTracks?.Refresh();
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
                if (value) FilteredTracks?.Refresh();
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
                if (value) FilteredTracks?.Refresh();
            }
        }
    }
    
    // Active Downloads HUD controls
    private bool _isActiveDownloadsVisible = true;
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
    
    public ICommand ToggleActiveDownloadsCommand { get; }

    public int MaxDownloads 
    {
        get => _downloadManager.MaxActiveDownloads;
        set
        {
            if (_downloadManager.MaxActiveDownloads != value)
            {
                _downloadManager.MaxActiveDownloads = value;
                OnPropertyChanged();
            }
        }
    }

    public string NoProjectSelectedMessage
    {
        get => _noProjectSelectedMessage;
        set { if (_noProjectSelectedMessage != value) { _noProjectSelectedMessage = value; OnPropertyChanged(); } }
    }

    private bool _isGridView;
    public bool IsGridView
    {
        get => _isGridView;
        set
        {
            if (_isGridView != value)
            {
                _isGridView = value;
                OnPropertyChanged();
            }
        }
    }

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
                OnPropertyChanged(nameof(IsDataGridReadOnly));
            }
        }
    }

    public bool IsDataGridReadOnly => !IsEditMode;

    public ICommand ToggleEditModeCommand { get; }

    private bool _initialLoadCompleted = false;

    public LibraryViewModel(
        ILogger<LibraryViewModel> logger, 
        DownloadManager downloadManager, 
        ILibraryService libraryService, 
        PlayerViewModel playerViewModel,
        ImportHistoryViewModel importHistoryViewModel,
        INavigationService navigationService,
        IUserInputService userInputService)
    {
        _logger = logger;
        _downloadManager = downloadManager;
        _libraryService = libraryService;
        _playerViewModel = playerViewModel;
        _importHistoryViewModel = importHistoryViewModel;
        _navigationService = navigationService;
        _userInputService = userInputService;

        // Commands
        HardRetryCommand = new RelayCommand<PlaylistTrackViewModel>(ExecuteHardRetry);
        PauseCommand = new RelayCommand<PlaylistTrackViewModel>(ExecutePause);
        ResumeCommand = new RelayCommand<PlaylistTrackViewModel>(ExecuteResume);
        CancelCommand = new RelayCommand<PlaylistTrackViewModel>(ExecuteCancel);
        OpenProjectCommand = new RelayCommand<PlaylistJob>(project => SelectedProject = project);
        DeleteProjectCommand = new AsyncRelayCommand<PlaylistJob>(ExecuteDeleteProjectAsync);
        RefreshLibraryCommand = new AsyncRelayCommand(async () => await LoadProjectsAsync());
        PauseProjectCommand = new RelayCommand<PlaylistJob>(ExecutePauseProject);
        ResumeProjectCommand = new RelayCommand<PlaylistJob>(ExecuteResumeProject);
        LoadAllTracksCommand = new RelayCommand(() => SelectedProject = _allTracksJob);
        ToggleActiveDownloadsCommand = new RelayCommand<object>(_ => IsActiveDownloadsVisible = !IsActiveDownloadsVisible);
        AddPlaylistCommand = new AsyncRelayCommand(ExecuteAddPlaylistAsync);
        PlayTrackCommand = new RelayCommand<PlaylistTrackViewModel>(ExecutePlayTrack);
        ViewHistoryCommand = new AsyncRelayCommand(ExecuteViewHistoryAsync);
        ToggleEditModeCommand = new RelayCommand<object>(_ => IsEditMode = !IsEditMode);
        
        // Basic impl for missing commands
        OpenFolderCommand = new RelayCommand<object>(_ => { /* TODO: Implement Open Folder */ });
        RemoveTrackCommand = new RelayCommand<PlaylistTrackViewModel>(ExecuteRemoveTrack); // Replaced TODO

        // Initialize Views
        var activeView = new ListCollectionView(_downloadManager.AllGlobalTracks);
        activeView.Filter = o => (o as PlaylistTrackViewModel)?.IsActive == true;
        activeView.IsLiveFiltering = true;
        activeView.LiveFilteringProperties.Add(nameof(PlaylistTrackViewModel.IsActive));
        ActiveDownloadsView = activeView;

        // Subscribe to global track updates for live project track status
        _downloadManager.TrackUpdated += OnGlobalTrackUpdated;

        // Subscribe to project added events
        _downloadManager.ProjectAdded += OnProjectAdded;
        
        // NEW: Subscribe to updates
        _downloadManager.ProjectUpdated += OnProjectUpdated;

        // Subscribe to project deletion events for real-time Library updates
        _libraryService.ProjectDeleted += OnProjectDeleted;

        // Load projects asynchronously
        _ = LoadProjectsAsync();
    }

    private async void OnProjectUpdated(object? sender, Guid jobId)
    {
        // Fetch the freshest data from DB
        var updatedJob = await _libraryService.FindPlaylistJobAsync(jobId);
        if (updatedJob == null) return;

        if (System.Windows.Application.Current is null) return;
        
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            // Update the existing object in the list so the UI binding triggers
            var existingJob = AllProjects.FirstOrDefault(j => j.Id == jobId);
            if (existingJob != null)
            {
                existingJob.SuccessfulCount = updatedJob.SuccessfulCount;
                existingJob.FailedCount = updatedJob.FailedCount;
                existingJob.MissingCount = updatedJob.MissingCount;
                
                // Force UI refresh if needed (ProgressPercentage relies on these)
                _logger.LogDebug("Refreshed UI counts for project {Title}: {Succ}/{Total}", existingJob.SourceTitle, existingJob.SuccessfulCount, existingJob.TotalTracks);
            }
        });
    }

    private async void OnProjectDeleted(object? sender, Guid projectId)
    {
        _logger.LogInformation("OnProjectDeleted event received for job {JobId}", projectId);
        if (System.Windows.Application.Current is null) return;

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var jobToRemove = AllProjects.FirstOrDefault(p => p.Id == projectId);
            if (jobToRemove != null)
            {
                AllProjects.Remove(jobToRemove);

                // Auto-select next project if the deleted one was selected
                if (SelectedProject == jobToRemove)
                    SelectedProject = AllProjects.FirstOrDefault();
            }
        });
    }
    private async void OnProjectAdded(object? sender, ProjectEventArgs e)
    {
        _logger.LogInformation("OnProjectAdded ENTRY for job {JobId}. Current project count: {ProjectCount}, Global track count: {TrackCount}", e.Job.Id, AllProjects.Count, _downloadManager.AllGlobalTracks.Count);
        if (System.Windows.Application.Current is null) return;
        
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            // Check if the job already exists in the collection (race condition safety)
            if (AllProjects.Any(j => j.Id == e.Job.Id))
            {
                _logger.LogWarning("Project {JobId} already exists in AllProjects, skipping add.", e.Job.Id);
                return;
            }

            // Add the new project to the observable collection
            AllProjects.Add(e.Job);

            // Auto-select the newly added project so it shows immediately
            SelectedProject = e.Job;

            _logger.LogInformation("Project '{Title}' added to Library view.", e.Job.SourceTitle);
        });
        _logger.LogInformation("OnProjectAdded EXIT for job {JobId}. New project count: {ProjectCount}", e.Job.Id, AllProjects.Count);
    }

    public async void AddToPlaylist(PlaylistJob targetPlaylist, PlaylistTrackViewModel sourceTrack)
    {
        if (targetPlaylist == null || sourceTrack == null || targetPlaylist.Id == Guid.Empty) return; // Prevent invalid drops

        _logger.LogInformation("Adding track {Track} to playlist {Playlist}", sourceTrack.Title, targetPlaylist.SourceTitle);

        try
        {
            // 1. Create new PlaylistTrack model linked to the target playlist
            var newTrack = new PlaylistTrack
            {
                Id = Guid.NewGuid(),
                PlaylistId = targetPlaylist.Id,
                Artist = sourceTrack.Artist,
                Title = sourceTrack.Title,
                Album = sourceTrack.Album,
                TrackUniqueHash = sourceTrack.GlobalId,
                Status = TrackStatus.Downloaded, // Assuming we dragged a downloaded/existing track
                SortOrder = targetPlaylist.TotalTracks + 1 // Default sort order
            };

            // 2. Persist to Database
            await _libraryService.SavePlaylistTrackAsync(newTrack);

            // 3. Log Activity
            await _libraryService.LogPlaylistActivityAsync(targetPlaylist.Id, "Add", $"Added track '{sourceTrack.Artist} - {sourceTrack.Title}'");

            // 4. Update Target Playlist Counts/Metadata if needed
            // The service might do this, or we rely on events.
            // Let's manually refresh the target playlist's in-memory list if it's currently selected?
            // If it's NOT selected, we just need to update the TotalTracks count on the job object.
            
            targetPlaylist.TotalTracks++;
            targetPlaylist.SuccessfulCount++; // Validation: check actual status? 
            
            _logger.LogInformation("Successfully added track to playlist {Playlist}", targetPlaylist.SourceTitle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add track to playlist");
        }
    }

    public async void ReorderTrack(PlaylistTrackViewModel source, PlaylistTrackViewModel target)
    {
        if (source == null || target == null || source == target) return;
        if (CurrentProjectTracks == null) return;

        // Ensure we are operating on the same collection
        int oldIndex = CurrentProjectTracks.IndexOf(source);
        int newIndex = CurrentProjectTracks.IndexOf(target);

        if (oldIndex < 0 || newIndex < 0) return;

        // 1. Move in ObservableCollection (updates UI immediately)
        CurrentProjectTracks.Move(oldIndex, newIndex);

        // 2. Recalculate SortOrder for the entire collection (Dense Rank)
        // This ensures a clean 0..N sequence regardless of previous state
        for (int i = 0; i < CurrentProjectTracks.Count; i++)
        {
            var track = CurrentProjectTracks[i];
            track.SortOrder = i; 
            // Note: Setter updates Model.SortOrder automatically
        }

        // 3. Persist changes asynchronously
        if (SelectedProject != null)
        {
            try 
            {
                // Extract models to save
                var tracksToSave = CurrentProjectTracks.Select(vm => vm.Model).ToList();
                await _libraryService.SaveTrackOrderAsync(SelectedProject.Id, tracksToSave);
                _logger.LogInformation("Persisted new track order for playlist {Id}", SelectedProject.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist track order");
            }
        }
    }

    private void ExecuteHardRetry(PlaylistTrackViewModel? vm)
    {
        if (vm == null) return;

        _logger.LogInformation("Hard Retry requested for {Artist} - {Title}", vm.Artist, vm.Title);
        _downloadManager.HardRetryTrack(vm.GlobalId);
    }

    private void ExecutePause(PlaylistTrackViewModel? vm)
    {
        if (vm == null) return;

        _logger.LogInformation("Pause requested for {Artist} - {Title}", vm.Artist, vm.Title);
        _downloadManager.PauseTrack(vm.GlobalId);
    }

    private void ExecuteResume(PlaylistTrackViewModel? vm)
    {
        if (vm == null) return;

        _logger.LogInformation("Resume requested for {Artist} - {Title}", vm.Artist, vm.Title);
        _downloadManager.ResumeTrack(vm.GlobalId);
    }

    private void ExecuteCancel(PlaylistTrackViewModel? vm)
    {
        if (vm == null) return;

        _logger.LogInformation("Cancel requested for {Artist} - {Title}", vm.Artist, vm.Title);
        _downloadManager.CancelTrack(vm.GlobalId);
        _logger.LogInformation("Cancel requested for {Artist} - {Title}", vm.Artist, vm.Title);
        _downloadManager.CancelTrack(vm.GlobalId);
    }

    private void ExecutePlayTrack(PlaylistTrackViewModel? vm)
    {
        if (vm == null || string.IsNullOrEmpty(vm.Model?.ResolvedFilePath)) return;
        
        // Ensure file exists? libVLC will fail or log err if not.
        _logger.LogInformation("Playing track: {Title} by {Artist}", vm.Title, vm.Artist);
        _playerViewModel.PlayTrack(vm.Model!.ResolvedFilePath, vm.Title ?? "Unknown", vm.Artist ?? "Unknown Artist");
        
        // Also ensure sidebar is visible?
        // _mainViewModel.TogglePlayerCommand.Execute(null); // No reference to MainViewModel here.
        // We'll trust user to open it, or maybe use an event aggregator later if we want auto-open.
    }

    private void ExecutePauseProject(PlaylistJob? job)
    {
        // Operate on currently visible tracks
        var tracks = CurrentProjectTracks.ToList();
        _logger.LogInformation("Pausing all {Count} tracks in current view", tracks.Count);
        foreach (var t in tracks)
        {
            if (t.CanPause) _downloadManager.PauseTrack(t.GlobalId);
        }
    }

    private void ExecuteResumeProject(PlaylistJob? job)
    {
        // Use local snapshot to avoid collection modified exception
        var tracks = CurrentProjectTracks?.ToList();
        if (tracks == null || !tracks.Any()) return;

        _logger.LogInformation("Start All: Processing {Count} tracks in current view", tracks.Count);
        
        int resumed = 0, requeued = 0, alreadyActive = 0;
        
        foreach (var t in tracks)
        {
            // 1. Check if track is actually known to the DownloadManager
            var liveTrack = _downloadManager.AllGlobalTracks.FirstOrDefault(x => x.GlobalId == t.GlobalId);
            
            if (liveTrack == null)
            {
                // ORPHAN DETECTED: Track exists in UI but not in Manager
                // Re-queue it unless it's genuinely completed/downloading (unlikely for orphan)
                if (t.State != PlaylistTrackState.Completed && t.State != PlaylistTrackState.Downloading)
                {
                    _downloadManager.QueueTracks(new List<PlaylistTrack> { t.Model });
                    requeued++;
                }
                continue;
            }
            
            // 2. Existing Track Logic
            if (liveTrack.State == PlaylistTrackState.Paused || liveTrack.State == PlaylistTrackState.Failed)
            {
                _downloadManager.ResumeTrack(t.GlobalId);
                resumed++;
            }
            else if (liveTrack.State == PlaylistTrackState.Pending)
            {
                // Already pending in manager. 
                // We'll leave it to the loop, but we won't skip logic if we needed to do something else.
            }
            else if (liveTrack.IsActive)
            {
                alreadyActive++;
            }
        }
        
        _logger.LogInformation("Start All Summary: Resumed {Resumed}, Re-Queued {Requeued}, AlreadyActive {Active}", resumed, requeued, alreadyActive);
    }
        


    private async Task ExecuteAddPlaylistAsync()
    {
        try
        {
            // Simple auto-naming strategy as default
            int count = AllProjects.Count(p => p.SourceTitle.StartsWith("New Playlist"));
            string defaultTitle = count == 0 ? "New Playlist" : $"New Playlist {count + 1}";

            string? title = _userInputService.GetInput("Enter a name for the new playlist:", "New Playlist", defaultTitle);

            if (string.IsNullOrWhiteSpace(title)) return; // User cancelled

            _logger.LogInformation("Creating new playlist: {Title}", title);
            var job = await _libraryService.CreateEmptyPlaylistAsync(title);
            
            // UI Update
            AllProjects.Add(job);
            SelectedProject = job;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create new playlist");
        }
    }

    private async Task ExecuteDeleteProjectAsync(PlaylistJob? job)
    {
        // Fallback to selected project if null (e.g. called from header button without parameter)
        var targetJob = job ?? SelectedProject;
        
        if (targetJob == null || targetJob == _allTracksJob) return;

        _logger.LogInformation("Soft-deleting project: {Title} ({Id})", targetJob.SourceTitle, targetJob.Id);

        try
        {
            // Soft-delete via database service
            await _libraryService.DeletePlaylistJobAsync(targetJob.Id);
            // The UI update will now be handled by the OnProjectDeleted event handler.
            _logger.LogInformation("Deletion request for project {Title} processed. Event will trigger UI update.", targetJob.SourceTitle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete project {Id}", targetJob.Id);
        }
    }

    private async void ExecuteRemoveTrack(PlaylistTrackViewModel? track)
    {
        if (track == null || SelectedProject == null || SelectedProject.Id == Guid.Empty) return;

        // User didn't strictly say it's restricted to Edit Mode, but it's consistent.
        if (!IsEditMode) 
        {
             // Optional: Show message "Enable Edit Mode to remove tracks"
             return; 
        }

        var result = System.Windows.MessageBox.Show(
            $"Remove '{track.Title}' from playlist?", 
            "Confirm Remove", 
            System.Windows.MessageBoxButton.YesNo, 
            System.Windows.MessageBoxImage.Question);

        if (result != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            _logger.LogInformation("Removing track {Track} from playlist {Playlist}", track.Title, SelectedProject.SourceTitle);
            
            // 1. Remove from DB
            await _libraryService.DeletePlaylistTrackAsync(track.Id);
            
            // 2. Log Activity
            await _libraryService.LogPlaylistActivityAsync(SelectedProject.Id, "Remove", $"Removed track '{track.Artist} - {track.Title}'");

            // 3. Update In-Memory list
            if (CurrentProjectTracks.Contains(track))
            {
                CurrentProjectTracks.Remove(track);
            }
            
            // 4. Update counts
            SelectedProject.TotalTracks--;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove track");
        }
    }

    private async Task ExecuteViewHistoryAsync()
    {
        if (SelectedProject == null || SelectedProject.Id == Guid.Empty) return;

        _logger.LogInformation("Navigating to history for job: {Title}", SelectedProject.SourceTitle);
        
        // 1. Pre-select in target VM
        await _importHistoryViewModel.SelectJob(SelectedProject.Id);
        
        // 2. Navigate
        _navigationService.NavigateTo("ImportHistory");
    }

    private async Task LoadProjectTracksAsync(PlaylistJob job)
    {
        try
        {
            _logger.LogInformation("Loading tracks for project: {Name}", job.SourceTitle);
            var tracks = new ObservableCollection<PlaylistTrackViewModel>();

            if (job.Id == Guid.Empty || job == _allTracksJob) // All Tracks
            {
                 var globalCount = _downloadManager.AllGlobalTracks.Count;
                 _logger.LogInformation("All Tracks mode: GlobalTracks has {Count} items.", globalCount);
                 
                 // Load ALL global tracks, Sorted by Active first
                 var all = _downloadManager.AllGlobalTracks
                     .OrderByDescending(t => t.IsActive)
                     .ThenBy(t => t.Artist)
                     .ToList();
                 
                 _logger.LogInformation("All Tracks mode: Adding {Count} tracks to view.", all.Count);
                 foreach (var t in all) tracks.Add(t);
            }
            else
            {
                // N+1 Query Fix: Use the eagerly loaded tracks from the job object itself.
                foreach (var track in job.PlaylistTracks.OrderBy(t => t.TrackNumber))
                {
                    var vm = new PlaylistTrackViewModel(track);

                    // Sync with live DownloadManager state for real-time progress
                    var liveTrack = _downloadManager.AllGlobalTracks
                        .FirstOrDefault(t => t.GlobalId == track.TrackUniqueHash);

                    if (liveTrack != null)
                    {
                        vm.State = liveTrack.State;
                        vm.Progress = liveTrack.Progress;
                        vm.CurrentSpeed = liveTrack.CurrentSpeed;
                        vm.ErrorMessage = liveTrack.ErrorMessage;
                    }

                    tracks.Add(vm);
                }
            }

            if (System.Windows.Application.Current is null) return;
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                CurrentProjectTracks = tracks;
            });
            _logger.LogInformation("Loaded {Count} tracks for project {Title}", tracks.Count, job.SourceTitle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load tracks for project {Id}", job.Id);
        }
    }

    private async Task LoadProjectsAsync()
    {
        try
        {
            _logger.LogInformation("LoadProjectsAsync ENTRY. Current AllProjects.Count: {Count}", AllProjects.Count);
            _logger.LogInformation("Loading all playlist jobs from database...");

            var jobs = await _libraryService.LoadAllPlaylistJobsAsync();
            
            _logger.LogInformation("LoadProjectsAsync: Loaded {Count} jobs from database", jobs.Count);
            foreach (var job in jobs)
            {
                _logger.LogInformation("  - Job {JobId}: '{Title}' with {TrackCount} tracks, Created: {Created}", 
                    job.Id, job.SourceTitle, job.TotalTracks, job.CreatedAt);
            }

            if (System.Windows.Application.Current is null) return;
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (_initialLoadCompleted)
                {
                    _logger.LogWarning("LoadProjectsAsync called after initial load, performing a safe sync.");
                    // Safe sync: add missing, remove deleted, then re-sort
                    var loadedJobIds = new HashSet<Guid>(jobs.Select(j => j.Id));
                    var currentJobIds = new HashSet<Guid>(AllProjects.Select(j => j.Id));

                    // Add new jobs not in the current collection
                    foreach (var job in jobs)
                    {
                        if (!currentJobIds.Contains(job.Id))
                        {
                            _logger.LogInformation("Adding missing job {JobId} to AllProjects", job.Id);
                            AllProjects.Add(job);
                        }
                    }

                    // Remove jobs from collection that are no longer in the database
                    var jobsToRemove = AllProjects.Where(j => !loadedJobIds.Contains(j.Id)).ToList();
                    foreach (var job in jobsToRemove)
                    {
                        _logger.LogInformation("Removing deleted job {JobId} from AllProjects", job.Id);
                        AllProjects.Remove(job);
                    }
                }
                else
                {
                    // Initial load: clear and add all
                    _logger.LogInformation("Initial load: clearing AllProjects and adding {Count} jobs", jobs.Count);
                    AllProjects.Clear();
                    foreach (var job in jobs)
                    {
                        AllProjects.Add(job);
                    }
                }

                // Re-sort the entire collection to ensure order is correct
                var sorted = AllProjects.OrderByDescending(j => j.CreatedAt).ToList();
                for (int i = 0; i < sorted.Count; i++)
                {
                    var job = sorted[i];
                    int currentIndex = AllProjects.IndexOf(job);
                    if (currentIndex != i)
                    {
                        AllProjects.Move(currentIndex, i);
                    }
                }

                if (SelectedProject == null && AllProjects.Any())
                {
                    SelectedProject = AllProjects.First();
                }

                if (!_initialLoadCompleted)
                {
                    _initialLoadCompleted = true;
                    _logger.LogInformation("Initial load of {count} projects completed.", AllProjects.Count);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load playlist jobs");
        }
    }
    private void OnGlobalTrackUpdated(object? sender, PlaylistTrackViewModel? updatedTrack)
    {
        if (updatedTrack == null || CurrentProjectTracks == null) return;

        if (System.Windows.Application.Current is null) return;
        // Use Dispatcher for UI thread safety
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var localTrack = CurrentProjectTracks
                .FirstOrDefault(t => t.GlobalId == updatedTrack.GlobalId);

            if (localTrack != null)
            {
                localTrack.State = updatedTrack.State;
                localTrack.Progress = updatedTrack.Progress;
                localTrack.CurrentSpeed = updatedTrack.CurrentSpeed;
                localTrack.ErrorMessage = updatedTrack.ErrorMessage;
            }
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
