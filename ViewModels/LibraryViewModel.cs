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
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

public class LibraryViewModel : INotifyPropertyChanged
{
    private readonly ILogger<LibraryViewModel> _logger;
    private readonly DownloadManager _downloadManager;
    private readonly ILibraryService _libraryService;

    // Master/Detail pattern properties
    private ObservableCollection<PlaylistJob> _allProjects = new();
    private PlaylistJob? _selectedProject;
    private ObservableCollection<PlaylistTrackViewModel> _currentProjectTracks = new();
    private string _noProjectSelectedMessage = "Select an import job to view its tracks";

    public ICommand HardRetryCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenProjectCommand { get; }
    public ICommand DeleteProjectCommand { get; }

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

    // Detail List: Tracks for selected project (Project Manifest)
    public ObservableCollection<PlaylistTrackViewModel> CurrentProjectTracks
    {
        get => _currentProjectTracks;
        set { _currentProjectTracks = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Message to display when no project is selected.
    /// </summary>
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

    private bool _initialLoadCompleted = false;

    public LibraryViewModel(ILogger<LibraryViewModel> logger, DownloadManager downloadManager, ILibraryService libraryService)
    {
        _logger = logger;
        _downloadManager = downloadManager;
        _libraryService = libraryService;

        // Commands
        HardRetryCommand = new RelayCommand<PlaylistTrackViewModel>(ExecuteHardRetry);
        PauseCommand = new RelayCommand<PlaylistTrackViewModel>(ExecutePause);
        ResumeCommand = new RelayCommand<PlaylistTrackViewModel>(ExecuteResume);
        CancelCommand = new RelayCommand<PlaylistTrackViewModel>(ExecuteCancel);
        OpenProjectCommand = new RelayCommand<PlaylistJob>(project => SelectedProject = project);
        DeleteProjectCommand = new AsyncRelayCommand<PlaylistJob>(ExecuteDeleteProjectAsync);

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
            // Add the new project to the observable collection
            AllProjects.Add(e.Job);

            // Auto-select the newly added project so it shows immediately
            SelectedProject = e.Job;

            _logger.LogInformation("Project '{Title}' added to Library view.", e.Job.SourceTitle);
        });
        _logger.LogInformation("OnProjectAdded EXIT for job {JobId}. New project count: {ProjectCount}", e.Job.Id, AllProjects.Count);
    }

    public void ReorderTrack(PlaylistTrackViewModel source, PlaylistTrackViewModel target)
    {
        if (source == null || target == null || source == target) return;

        // Simple implementation: Swap SortOrder
        // Better implementation: Insert
        // Renumbering everything is safest for consistency

        // Find current indices in the underlying collection? 
        // We really want to change SortOrder values.

        // Let's adopt a "dense rank" approach.
        // First, ensure everyone has a SortOrder. if 0, assign based on current index.

        var allTracks = _downloadManager.AllGlobalTracks; // This is the source
        // But we are only reordering within "Warehouse" view ideally. 
        // Mixing active/warehouse reordering is tricky.
        // Assuming we drag pending items.

        int oldIndex = source.SortOrder;
        int newIndex = target.SortOrder;

        if (oldIndex == newIndex) return;

        // Shift items
        foreach (var track in allTracks)
        {
            if (oldIndex < newIndex)
            {
                // Moving down: shift items between old and new UP (-1)
                if (track.SortOrder > oldIndex && track.SortOrder <= newIndex)
                {
                    track.SortOrder--;
                }
            }
            else
            {
                // Moving up: shift items between new and old DOWN (+1)
                if (track.SortOrder >= newIndex && track.SortOrder < oldIndex)
                {
                    track.SortOrder++;
                }
            }
        }

        source.SortOrder = newIndex;
        // Verify uniqueness? If we started with unique 0..N, we end with unique 0..N
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
    }

    private async Task ExecuteDeleteProjectAsync(PlaylistJob? job)
    {
        if (job == null) return;

        _logger.LogInformation("Soft-deleting project: {Title} ({Id})", job.SourceTitle, job.Id);

        try
        {
            // Soft-delete via database service
            await _libraryService.DeletePlaylistJobAsync(job.Id);
            // The UI update will now be handled by the OnProjectDeleted event handler.
            _logger.LogInformation("Deletion request for project {Title} processed. Event will trigger UI update.", job.SourceTitle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete project {Id}", job.Id);
        }
    }

    private async Task LoadProjectTracksAsync(PlaylistJob job)
    {
        try
        {
            _logger.LogInformation("Loading tracks for project: {Name}", job.SourceTitle);
            var tracks = new ObservableCollection<PlaylistTrackViewModel>();

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
            _logger.LogInformation("Loading all playlist jobs from database...");

            var jobs = await _libraryService.LoadAllPlaylistJobsAsync();

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
                            AllProjects.Add(job);
                        }
                    }

                    // Remove jobs from collection that are no longer in the database
                    var jobsToRemove = AllProjects.Where(j => !loadedJobIds.Contains(j.Id)).ToList();
                    foreach (var job in jobsToRemove)
                    {
                        AllProjects.Remove(job);
                    }
                }
                else
                {
                    // Initial load: clear and add all
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
