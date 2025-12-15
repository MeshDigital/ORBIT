using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services;

using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

/// <summary>
/// ViewModel for previewing imported tracks before adding to library.
/// Displays tracks in a grid view with selection and album grouping.
/// </summary>
public class ImportPreviewViewModel : INotifyPropertyChanged
{
    private readonly ILogger<ImportPreviewViewModel> _logger;
    private readonly DownloadManager _downloadManager;
    private readonly ILibraryService? _libraryService;
    private readonly INavigationService _navigationService;
    
    private string _sourceTitle = "Import Preview";
    private string _sourceType = "";
    private ObservableCollection<SelectableTrack> _importedTracks = new();
    private ObservableCollection<AlbumGroupViewModel> _albumGroups = new();
    private bool _isLoading;
    private string _statusMessage = "Ready to import";
    private int _selectedCount;

    public string SourceTitle
    {
        get => _sourceTitle;
        set { _sourceTitle = value; OnPropertyChanged(); }
    }

    public string SourceType
    {
        get => _sourceType;
        set { _sourceType = value; OnPropertyChanged(); }
    }

    public ObservableCollection<SelectableTrack> ImportedTracks
    {
        get => _importedTracks;
        set { _importedTracks = value; OnPropertyChanged(); }
    }

    public ObservableCollection<AlbumGroupViewModel> AlbumGroups
    {
        get => _albumGroups;
        set { _albumGroups = value; OnPropertyChanged(); }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (_isLoading != value)
            {
                _isLoading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanAddToLibrary));
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public int SelectedCount
    {
        get => _selectedCount;
        set { _selectedCount = value; OnPropertyChanged(); }
    }

    public int TrackCount => ImportedTracks.Count;
    public bool CanAddToLibrary => !IsLoading && SelectedCount > 0;

    public ICommand AddToLibraryCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand DeselectAllCommand { get; }
    public ICommand CancelCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<PlaylistJob>? AddedToLibrary;
    public event EventHandler? Cancelled;

    public ImportPreviewViewModel(
        ILogger<ImportPreviewViewModel> logger,
        DownloadManager downloadManager,
        INavigationService navigationService,
        ILibraryService? libraryService = null)
    {
        _logger = logger;
        _downloadManager = downloadManager;
        _libraryService = libraryService;
        _navigationService = navigationService;

        AddToLibraryCommand = new AsyncRelayCommand(AddToLibraryAsync, () => CanAddToLibrary);
        SelectAllCommand = new RelayCommand(SelectAll);
        DeselectAllCommand = new RelayCommand(DeselectAll);
        CancelCommand = new RelayCommand(Cancel);
    }

    /// <summary>
    /// Initialize preview with imported tracks from Spotify/CSV/etc
    /// </summary>
    public async Task InitializePreviewAsync(string sourceTitle, string sourceType, IEnumerable<SearchQuery> queries)
    {
        try
        {
            _logger.LogInformation("InitializePreviewAsync ENTRY: {SourceTitle}, {SourceType}, Query count: {Count}",
                sourceTitle, sourceType, queries?.Count() ?? 0);

            SourceTitle = sourceTitle;
            SourceType = sourceType;

            int trackNum = 1;
            var tempTracks = new List<Track>();
            foreach (var query in queries ?? Enumerable.Empty<SearchQuery>())
            {
                var track = new Track
                {
                    Title = query.Title,
                    Artist = query.Artist,
                    Album = query.Album,
                    Length = query.Length
                };
                tempTracks.Add(track);
                trackNum++;
            }

            _logger.LogInformation("Created {Count} Track objects from queries", tempTracks.Count);

            // Check for duplicates asynchronously
            if (_libraryService != null)
            {
                try
                {
                    foreach (var track in tempTracks)
                    {
                        var entry = await _libraryService.FindLibraryEntryAsync(track.UniqueHash);
                        track.IsInLibrary = entry != null;
                    }
                }
                catch (Exception ex)
                {
                    // Database might not be initialized yet on first run - this is non-critical
                    _logger.LogDebug(ex, "Could not check library for duplicates (database may not be initialized)");
                }
            }

            // Create SelectableTrack wrappers
            var selectableTracks = new List<SelectableTrack>();
            foreach (var track in tempTracks)
            {
                var selectableTrack = new SelectableTrack(track);
                
                // Wire up notification so the ViewModel knows when selection changes
                selectableTrack.OnSelectionChanged = () => 
                {
                     UpdateSelectedCount();
                     // Re-evaluate command can-execute
                     ((AsyncRelayCommand)AddToLibraryCommand).RaiseCanExecuteChanged();
                };
                
                selectableTracks.Add(selectableTrack);
            }

            _logger.LogInformation("Created {Count} SelectableTrack wrappers", selectableTracks.Count);

            // CRITICAL FIX: Replace entire collection instead of modifying it
            // This triggers OnPropertyChanged and forces UI to rebind
            // Pattern from main branch, adapted for SelectableTrack
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                ImportedTracks = new ObservableCollection<SelectableTrack>(selectableTracks);
                AlbumGroups.Clear(); // Clear album groups before regrouping
            });

            _logger.LogInformation("Replaced ImportedTracks collection with {Count} items", ImportedTracks.Count);

            // Group by album for display
            GroupByAlbum();
            StatusMessage = $"Loaded {ImportedTracks.Count} tracks";
            
            _logger.LogInformation("Import preview initialized with {Count} tracks from {Source}. First track: {Artist} - {Title}", 
                ImportedTracks.Count, sourceTitle, 
                ImportedTracks.FirstOrDefault()?.Model.Artist ?? "None", 
                ImportedTracks.FirstOrDefault()?.Model.Title ?? "None");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InitializePreviewAsync FAILED for {SourceTitle}", sourceTitle);
            StatusMessage = $"Error loading preview: {ex.Message}";
            throw;
        }
    }

    /// <summary>
    /// Group tracks by album for display in grid
    /// </summary>
    private void GroupByAlbum()
    {
        AlbumGroups.Clear();

        var groupedByAlbum = ImportedTracks
            .GroupBy(t => t.Model.Album ?? "[Unknown Album]")
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var group in groupedByAlbum)
        {
            var albumGroup = new AlbumGroupViewModel
            {
                Album = group.Key,
                Tracks = new ObservableCollection<SelectableTrack>(group.ToList())
            };
            AlbumGroups.Add(albumGroup);
        }
    }

    /// <summary>
    /// Add selected tracks to library as a new PlaylistJob
    /// </summary>
    private async Task AddToLibraryAsync()
    {
        var selectedTracks = ImportedTracks.Where(t => t.IsSelected).Select(t => t.Model).ToList();

        if (!selectedTracks.Any())
        {
            StatusMessage = "No tracks selected";
            return;
        }

        IsLoading = true;
        StatusMessage = "Adding to library...";

        // Create PlaylistJob to group all tracks
        var job = new PlaylistJob
        {
            Id = Guid.NewGuid(),
            SourceTitle = SourceTitle,
            SourceType = SourceType,
            CreatedAt = DateTime.UtcNow,
            DestinationFolder = "" // Use default
        };

        try
        {
            await Task.Delay(100); // Simulate async work
            _logger.LogInformation(
                "Adding {Count} tracks to library. JobId: {JobId}, Thread: {ThreadId}",
                selectedTracks.Count,
                job.Id,
                Thread.CurrentThread.ManagedThreadId);

            // Convert tracks to PlaylistTracks
            foreach (var track in selectedTracks)
            {
                // Note: PlaylistJob.OriginalTracks is ObservableCollection<Track>, not PlaylistTrack
                job.OriginalTracks.Add(track);
            }

            // Notify that tracks have been added
            _logger.LogInformation("Firing AddedToLibrary event for job {JobId}", job.Id);
            AddedToLibrary?.Invoke(this, job);

            _logger.LogInformation("AddedToLibrary event fired. Updating status message.");
            StatusMessage = $"✓ Added {selectedTracks.Count} tracks to library";
            _logger.LogInformation(
                "Successfully added {Count} tracks to library. JobId: {JobId}, Thread: {ThreadId}",
                selectedTracks.Count,
                job.Id,
                Thread.CurrentThread.ManagedThreadId);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            _logger.LogError(ex, "Failed to add tracks to library for JobId: {JobId}", job.Id);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void SelectAll()
    {
        foreach (var track in ImportedTracks)
        {
            track.IsSelected = true;
        }
        UpdateSelectedCount();
    }

    private void DeselectAll()
    {
        foreach (var track in ImportedTracks)
        {
            track.IsSelected = false;
        }
        UpdateSelectedCount();
    }

    private void Cancel()
    {
        _logger.LogInformation("Import preview cancelled");
        StatusMessage = "Preview cancelled";
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateSelectedCount()
    {
        var newCount = ImportedTracks.Count(t => t.IsSelected);
        if (SelectedCount != newCount)
        {
            SelectedCount = newCount;
            OnPropertyChanged(nameof(CanAddToLibrary));
        }
    }

    /// <summary>
    /// Handles the logic when a playlist job is confirmed and added from the preview.
    /// This was moved from MainViewModel to make this component more self-contained.
    /// </summary>
    public async Task HandlePlaylistJobAddedAsync(PlaylistJob job)
    {
        try
        {
            _logger.LogInformation("HandlePlaylistJobAddedAsync ENTRY: {Title} with {Count} tracks",
                job.SourceTitle, job.OriginalTracks.Count);

            // Queue project through DownloadManager to persist and add to Library.
            await _downloadManager.QueueProject(job);
            
            _logger.LogInformation("QueueProject completed for {JobId}. Job saved to database.", job.Id);

            // Small delay to ensure ProjectAdded event handler completes
            // This allows LibraryViewModel.OnProjectAdded to finish adding to AllProjects
            await Task.Delay(200);
            
            _logger.LogInformation("Navigating to Library page to show job {JobId}...", job.Id);

            // Navigate to Library to see the new job.
            _navigationService.NavigateTo("Library");

            _logger.LogInformation("Navigation to Library completed.");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error adding to library: {ex.Message}";
            _logger.LogError(ex, "Failed to handle PlaylistJob addition");
        }
    }

    /// <summary>
    /// Handles the logic when the import is cancelled.
    /// This was moved from MainViewModel.
    /// </summary>
    public void HandleCancellation()
    {
        // Navigate back to the search page on cancellation.
        _navigationService.NavigateTo("Search");
        _logger.LogInformation("Import cancelled, navigating back to Search page.");
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class AlbumGroupViewModel
{
    public string Album { get; set; } = "[Unknown]";
    public ObservableCollection<SelectableTrack> Tracks { get; set; } = new();
}
