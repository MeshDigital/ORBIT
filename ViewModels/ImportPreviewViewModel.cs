using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
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
    
    private string _sourceTitle = "Import Preview";
    private string _sourceType = "";
    private ObservableCollection<Track> _importedTracks = new();
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

    public ObservableCollection<Track> ImportedTracks
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
        ILibraryService? libraryService = null)
    {
        _logger = logger;
        _downloadManager = downloadManager;
        _libraryService = libraryService;

        AddToLibraryCommand = new AsyncRelayCommand(AddToLibraryAsync, () => CanAddToLibrary);
        SelectAllCommand = new RelayCommand(SelectAll);
        DeselectAllCommand = new RelayCommand(DeselectAll);
        CancelCommand = new RelayCommand(Cancel);
    }

    /// <summary>
    /// Initialize preview with imported tracks from Spotify/CSV/etc
    /// </summary>
    public void InitializePreview(string sourceTitle, string sourceType, IEnumerable<SearchQuery> queries)
    {
        SourceTitle = sourceTitle;
        SourceType = sourceType;
        ImportedTracks.Clear();
        AlbumGroups.Clear();

        int trackNum = 1;
        foreach (var query in queries ?? Enumerable.Empty<SearchQuery>())
        {
            var track = new Track
            {
                Title = query.Title,
                Artist = query.Artist,
                Album = query.Album,
                Length = query.Length
            };
            ImportedTracks.Add(track);
            trackNum++;
        }

        // Group by album for display
        GroupByAlbum();
        StatusMessage = $"Loaded {ImportedTracks.Count} tracks";
        _logger.LogInformation("Import preview initialized with {Count} tracks from {Source}", 
            ImportedTracks.Count, sourceTitle);
    }

    /// <summary>
    /// Group tracks by album for display in grid
    /// </summary>
    private void GroupByAlbum()
    {
        AlbumGroups.Clear();

        var groupedByAlbum = ImportedTracks
            .GroupBy(t => t.Album ?? "[Unknown Album]")
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var group in groupedByAlbum)
        {
            var albumGroup = new AlbumGroupViewModel
            {
                Album = group.Key,
                Tracks = new ObservableCollection<Track>(group.ToList())
            };
            AlbumGroups.Add(albumGroup);
        }
    }

    /// <summary>
    /// Add selected tracks to library as a new PlaylistJob
    /// </summary>
    private async Task AddToLibraryAsync()
    {
        var selectedTracks = ImportedTracks.Where(t => t.IsSelected).ToList();

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
            AddedToLibrary?.Invoke(this, job);

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

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Represents a group of tracks from the same album for display
/// </summary>
public class AlbumGroupViewModel
{
    public string Album { get; set; } = "[Unknown]";
    public ObservableCollection<Track> Tracks { get; set; } = new();
}
