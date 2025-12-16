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

    public ProjectListViewModel(
        ILogger<ProjectListViewModel> logger,
        ILibraryService libraryService,
        DownloadManager downloadManager,
        ImportOrchestrator importOrchestrator,
        Services.ImportProviders.SpotifyLikedSongsImportProvider spotifyLikedSongsProvider)
    {
        _logger = logger;
        _libraryService = libraryService;
        _downloadManager = downloadManager;
        _importOrchestrator = importOrchestrator;
        _spotifyLikedSongsProvider = spotifyLikedSongsProvider;

        // Initialize commands
        OpenProjectCommand = new RelayCommand<PlaylistJob>(project => SelectedProject = project);
        DeleteProjectCommand = new AsyncRelayCommand<PlaylistJob>(ExecuteDeleteProjectAsync);
        AddPlaylistCommand = new AsyncRelayCommand(ExecuteAddPlaylistAsync);
        RefreshLibraryCommand = new AsyncRelayCommand(ExecuteRefreshAsync);
        LoadAllTracksCommand = new RelayCommand(() => SelectedProject = _allTracksJob);
        ImportLikedSongsCommand = new AsyncRelayCommand(ExecuteImportLikedSongsAsync);

        // Subscribe to events
        _libraryService.PlaylistAdded += OnPlaylistAdded;
        _downloadManager.ProjectUpdated += OnProjectUpdated;
        _libraryService.ProjectDeleted += OnProjectDeleted;
    }

    private async Task ExecuteImportLikedSongsAsync()
    {
        _logger.LogInformation("Starting 'Liked Songs' import from Spotify...");
        
        // 1. Check if "Liked Songs" project already exists
        var existingJob = await _libraryService.FindPlaylistJobBySourceTypeAsync("Spotify Liked");

        if (existingJob != null)
        {
            _logger.LogInformation("Existing 'Liked Songs' project found ({Id}). Syncing...", existingJob.Id);
            await SyncLikedSongsAsync(existingJob);
        }
        else
        {
             _logger.LogInformation("No existing 'Liked Songs' project. Creating new one...");
            // Use "User Library" as input string (ignored by provider but useful for logs)
            await _importOrchestrator.ImportAllDirectlyAsync(_spotifyLikedSongsProvider, "User Library");
        }
    }

    private async Task SyncLikedSongsAsync(PlaylistJob existingJob)
    {
        try
        {
            // 1. Fetch current Liked Songs from Spotify
            var result = await _spotifyLikedSongsProvider.ImportAsync("User Library");
            
            if (!result.Success || result.Tracks == null)
            {
                _logger.LogError("Failed to fetch liked songs: {Error}", result.ErrorMessage);
                return; // TODO: Show notification
            }

            // 2. Identify New Tracks
            // We use SpotifyId if available, otherwise fallback to Artist+Title hash?
            // Existing tracks are in existingJob.PlaylistTracks? 
            // NOTE: PlaylistTracks may not be fully loaded if we only fetched the job header.
            // Better to fetch all tracks for this job from DB first.
            var existingTracks = await _libraryService.LoadPlaylistTracksAsync(existingJob.Id);
            var existingSpotifyIds = new HashSet<string>(existingTracks
                .Where(t => !string.IsNullOrEmpty(t.TrackUniqueHash)) // TrackUniqueHash is usually Spotify ID for these?
                // Wait, ImportProvider sets SpotifyId on SelectableTrack. Where does it go?
                // DownloadManager.QueueProject maps SelectableTrack to PlaylistTrack.
                // It maps: SpotifyTrackId = track.SpotifyTrackId.
                .Select(t => t.TrackUniqueHash) // Just using Hash for now as proxy?
                // Actually, let's assume we want to match by Title/Artist if Spotify ID logic is complex.
                // BUT, since we have Spotify IDs, we should use them.
                // Q: Does PlaylistTrack have SpotifyTrackId? 
                // A: Yes, added in Phase 0.
            );
            
            // Wait, existingTracks needs to load SpotifyTrackId.
            // Checking PlaylistTrack entity... yes it has it.
            // But checking equality:
            var existingSpotifyIdSet = existingTracks
                .Where(t => !string.IsNullOrEmpty(t.SpotifyTrackId))
                .Select(t => t.SpotifyTrackId)
                .ToHashSet();

            var newTracks = new List<SearchQuery>();
            foreach (var track in result.Tracks)
            {
                if (!string.IsNullOrEmpty(track.SpotifyTrackId) && !existingSpotifyIdSet.Contains(track.SpotifyTrackId))
                {
                    newTracks.Add(track);
                }
                else if (string.IsNullOrEmpty(track.SpotifyTrackId))
                {
                     // Fallback check?
                }
            }

            if (newTracks.Count == 0)
            {
                _logger.LogInformation("No new liked songs to sync.");
                 // Optionally notify user "All up to date"
                return;
            }

            _logger.LogInformation("Found {Count} new liked songs. Adding to project...", newTracks.Count);

            // 3. Convert SelectableTracks to PlaylistTracks
            var playlistTracksToAdd = new List<PlaylistTrack>();
            int maxTrackNum = existingTracks.Count > 0 ? existingTracks.Max(t => t.TrackNumber) : 0;

            foreach (var nt in newTracks)
            {
                playlistTracksToAdd.Add(new PlaylistTrack
                {
                    Id = Guid.NewGuid(),
                    PlaylistId = existingJob.Id,
                    Artist = nt.Artist,
                    Title = nt.Title,
                    Album = nt.Album,
                    TrackUniqueHash = nt.SpotifyTrackId ?? Guid.NewGuid().ToString(), // Use Spotify ID as hash preference
                    Status = TrackStatus.Missing,
                    TrackNumber = ++maxTrackNum,
                    SpotifyTrackId = nt.SpotifyTrackId,
                    // Use metadata if available (it was cached by provider)
                    AddedAt = DateTime.UtcNow
                });
            }

            // 4. Save new tracks to DB
            await _libraryService.SavePlaylistTracksAsync(playlistTracksToAdd);

            // 5. Update Job Totals
            existingJob.TotalTracks += playlistTracksToAdd.Count;
            // TODO: Update job in DB (SavePlaylistJobAsync updates header)
            await _libraryService.SavePlaylistJobAsync(existingJob);

            // 6. Queue for Download (Incremental)
            _downloadManager.QueueTracks(playlistTracksToAdd);

            _logger.LogInformation("Sync complete. queued {Count} new tracks.", playlistTracksToAdd.Count);

            // 7. Refresh UI logic
            // If the current view is showing this project, we might need to refresh local list?
            if (SelectedProject?.Id == existingJob.Id)
            {
                // Trigger reload of tracks
                 // This is tricky without coupled logic. 
                 // But LibraryViewModel handles "ProjectUpdated" maybe?
                 // Or we just re-select it?
                 // Simple hack:
                 // ProjectSelected?.Invoke(this, existingJob); // Might re-load?
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync liked songs");
        }
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
