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
/// Manages the list of projects/playlists in the library.
/// Handles project selection, creation, deletion, and refresh.
/// </summary>
public class ProjectListViewModel : INotifyPropertyChanged
{
    private readonly ILogger<ProjectListViewModel> _logger;
    private readonly ILibraryService _libraryService;
    private readonly DownloadManager _downloadManager;

    // Master List: All import jobs/projects
    private ObservableCollection<PlaylistJob> _allProjects = new();
    public ObservableCollection<PlaylistJob> AllProjects
    {
        get => _allProjects;
        set
        {
            _allProjects = value;
            OnPropertyChanged();
        }
    }

    // Selected project
    private PlaylistJob? _selectedProject;
    public PlaylistJob? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (_selectedProject != value)
            {
                _logger.LogInformation("SelectedProject changing to {Id} - {Title}", value?.Id, value?.SourceTitle);
                _selectedProject = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedProject));
                OnPropertyChanged(nameof(CanDeleteProject));

                // Raise event for parent ViewModel to handle
                ProjectSelected?.Invoke(this, value);
            }
        }
    }

    public bool HasSelectedProject => SelectedProject != null;
    public bool CanDeleteProject => SelectedProject != null && !IsEditMode;

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
                OnPropertyChanged(nameof(CanDeleteProject));
            }
        }
    }

    // Special "All Tracks" pseudo-project
    private readonly PlaylistJob _allTracksJob = new()
    {
        Id = Guid.Empty,
        SourceTitle = "All Tracks",
        SourceType = "Global Library"
    };

    // Events
    public event EventHandler<PlaylistJob?>? ProjectSelected;
    public event PropertyChangedEventHandler? PropertyChanged;

    // Commands
    // Commands
    public System.Windows.Input.ICommand OpenProjectCommand { get; }
    public System.Windows.Input.ICommand DeleteProjectCommand { get; }
    public System.Windows.Input.ICommand AddPlaylistCommand { get; }
    public System.Windows.Input.ICommand RefreshLibraryCommand { get; }
    public System.Windows.Input.ICommand LoadAllTracksCommand { get; }
    public System.Windows.Input.ICommand ImportLikedSongsCommand { get; }

    // Services
    private readonly ImportOrchestrator _importOrchestrator;
    private readonly Services.ImportProviders.SpotifyLikedSongsImportProvider _spotifyLikedSongsProvider;
    private readonly IDialogService _dialogService;
    private readonly SpotifyAuthService _spotifyAuthService;

    private bool _isSpotifyAuthenticated;
    public bool IsSpotifyAuthenticated
    {
        get => _isSpotifyAuthenticated;
        set
        {
            if (_isSpotifyAuthenticated != value)
            {
                _isSpotifyAuthenticated = value;
                OnPropertyChanged();
            }
        }
    }

    public ProjectListViewModel(
        ILogger<ProjectListViewModel> logger,
        ILibraryService libraryService,
        DownloadManager downloadManager,
        ImportOrchestrator importOrchestrator,
        Services.ImportProviders.SpotifyLikedSongsImportProvider spotifyLikedSongsProvider,
        SpotifyAuthService spotifyAuthService,
        IEventBus eventBus,
        IDialogService dialogService)
    {
        _logger = logger;
        _libraryService = libraryService;
        _downloadManager = downloadManager;
        _importOrchestrator = importOrchestrator;
        _spotifyLikedSongsProvider = spotifyLikedSongsProvider;
        _spotifyAuthService = spotifyAuthService;
        _dialogService = dialogService;

        // Initialize commands
        OpenProjectCommand = new RelayCommand<PlaylistJob>(project => SelectedProject = project);
        DeleteProjectCommand = new AsyncRelayCommand<PlaylistJob>(ExecuteDeleteProjectAsync);
        AddPlaylistCommand = new AsyncRelayCommand(ExecuteAddPlaylistAsync);
        RefreshLibraryCommand = new AsyncRelayCommand(ExecuteRefreshAsync);
        LoadAllTracksCommand = new RelayCommand(() => SelectedProject = _allTracksJob);
        ImportLikedSongsCommand = new AsyncRelayCommand(ExecuteImportLikedSongsAsync, () => IsSpotifyAuthenticated);

        // Subscribe to auth changes
        _spotifyAuthService.AuthenticationChanged += (s, authenticated) => 
        {
             IsSpotifyAuthenticated = authenticated;
             ((AsyncRelayCommand)ImportLikedSongsCommand).RaiseCanExecuteChanged();
        };

        // Initial auth check
        _ = Task.Run(async () => 
        {
            IsSpotifyAuthenticated = await _spotifyAuthService.IsAuthenticatedAsync();
        });

        // Subscribe to events
        // Subscribe to events
        eventBus.GetEvent<ProjectAddedEvent>().Subscribe(async evt => 
        {
            var job = await _libraryService.FindPlaylistJobAsync(evt.ProjectId);
            if (job != null) OnPlaylistAdded(this, job);
        });
        eventBus.GetEvent<ProjectUpdatedEvent>().Subscribe(evt => OnProjectUpdated(this, evt.ProjectId));
        eventBus.GetEvent<ProjectDeletedEvent>().Subscribe(evt => OnProjectDeleted(this, evt.ProjectId));
        
        // Subscribe to track state changes to update active download counts in real-time
        eventBus.GetEvent<Events.TrackStateChangedEvent>().Subscribe(OnTrackStateChanged);
    }
    
    private void OnTrackStateChanged(Events.TrackStateChangedEvent evt)
    {
        // PERFORMANCE FIX: Target specific project instead of looping through all
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Find the specific project that changed
            var project = AllProjects.FirstOrDefault(p => p.Id == evt.ProjectId);
            if (project != null)
            {
                // Refresh ONLY the affected project's stats
                project.RefreshStatusCounts();
                
                // TODO: Implement real active downloads tracking via DownloadManager
                // For now, placeholder values
                project.ActiveDownloadsCount = 0;
                project.CurrentDownloadingTrack = null;
            }
        });
    }

    private async Task ExecuteImportLikedSongsAsync()
    {
        _logger.LogInformation("Starting 'Liked Songs' import from Spotify...");
        
        // Use the unified orchestrator path. 
        // The orchestrator handles finding existing jobs and showing the preview.
        await _importOrchestrator.StartImportWithPreviewAsync(_spotifyLikedSongsProvider, "spotify:liked");
    }

    /// <summary>
    /// Loads all projects from the database.
    /// </summary>
    public async Task LoadProjectsAsync()
    {
        try
        {
            _logger.LogInformation("Loading projects from database...");
            var jobs = await _libraryService.LoadAllPlaylistJobsAsync();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                AllProjects.Clear();
                foreach (var job in jobs.OrderByDescending(j => j.CreatedAt))
                {
                    AllProjects.Add(job);
                }

                _logger.LogInformation("Loaded {Count} projects", AllProjects.Count);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load projects");
        }
    }
    // ... existing methods ...

    // ... existing event handlers ...

    private async Task ExecuteRefreshAsync()
    {
        _logger.LogInformation("Manual refresh requested - reloading projects");
        var selectedProjectId = SelectedProject?.Id;

        await LoadProjectsAsync();

        // Restore selection
        if (selectedProjectId.HasValue)
        {
            if (selectedProjectId == Guid.Empty)
            {
                SelectedProject = _allTracksJob;
            }
            else
            {
                var project = AllProjects.FirstOrDefault(p => p.Id == selectedProjectId.Value);
                if (project != null)
                {
                    SelectedProject = project;
                }
            }
        }

        _logger.LogInformation("Manual refresh completed");
    }

    private async Task ExecuteAddPlaylistAsync()
    {
        // TODO: Implement add playlist dialog
        _logger.LogInformation("Add playlist command executed");
        await Task.CompletedTask;
    }

    private async Task ExecuteDeleteProjectAsync(PlaylistJob? job)
    {
        if (job == null) return;

        try
        {
            var confirmed = await _dialogService.ConfirmAsync(
                "Delete Project", 
                $"Are you sure you want to delete '{job.SourceTitle}'? This cannot be undone.");
            
            if (!confirmed) return;

            _logger.LogInformation("Deleting project: {Title}", job.SourceTitle);
            await _libraryService.DeletePlaylistJobAsync(job.Id);
            _logger.LogInformation("Project deleted successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete project");
        }
    }

    private async void OnPlaylistAdded(object? sender, PlaylistJob job)
    {
        _logger.LogInformation("OnPlaylistAdded event received for job {JobId}", job.Id);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (AllProjects.Any(j => j.Id == job.Id))
            {
                _logger.LogWarning("Project {JobId} already exists, skipping add", job.Id);
                return;
            }

            AllProjects.Add(job);
            SelectedProject = job; // Auto-select new project

            _logger.LogInformation("Project '{Title}' added to list", job.SourceTitle);
        });
    }

    private async void OnProjectUpdated(object? sender, Guid jobId)
    {
        var updatedJob = await _libraryService.FindPlaylistJobAsync(jobId);
        if (updatedJob == null) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var existingJob = AllProjects.FirstOrDefault(j => j.Id == jobId);
            if (existingJob != null)
            {
                existingJob.SuccessfulCount = updatedJob.SuccessfulCount;
                existingJob.FailedCount = updatedJob.FailedCount;
                existingJob.MissingCount = updatedJob.MissingCount;

                _logger.LogDebug("Updated project {Title}: {Succ}/{Total}",
                    existingJob.SourceTitle, existingJob.SuccessfulCount, existingJob.TotalTracks);
            }
        });
    }

    private async void OnProjectDeleted(object? sender, Guid projectId)
    {
        _logger.LogInformation("OnProjectDeleted event received for job {JobId}", projectId);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var jobToRemove = AllProjects.FirstOrDefault(p => p.Id == projectId);
            if (jobToRemove != null)
            {
                AllProjects.Remove(jobToRemove);

                // Auto-select next project if deleted one was selected
                if (SelectedProject == jobToRemove)
                {
                    SelectedProject = AllProjects.FirstOrDefault();
                }
            }
        });
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
