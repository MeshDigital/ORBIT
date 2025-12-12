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
using SLSKDONET.Services.InputParsers;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

public class SpotifyImportViewModel : INotifyPropertyChanged
{
    private readonly ILogger<SpotifyImportViewModel> _logger;
    private readonly SpotifyInputSource? _spotifyApiService;
    private readonly SpotifyScraperInputSource _spotifyScraperService;
    private readonly DownloadManager _downloadManager;
    
    private string _playlistUrl = "";
    private string _playlistTitle = "Spotify Playlist";
    private string _playlistCoverUrl = "";
    private bool _isLoading;
    private string _statusMessage = "";
    private ObservableCollection<SelectableTrack> _tracks = new();

    public string PlaylistUrl
    {
        get => _playlistUrl;
        set { _playlistUrl = value; OnPropertyChanged(); }
    }

    public string PlaylistTitle
    {
        get => _playlistTitle;
        set { _playlistTitle = value; OnPropertyChanged(); }
    }

    public string PlaylistCoverUrl
    {
        get => _playlistCoverUrl;
        set { _playlistCoverUrl = value; OnPropertyChanged(); }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            _isLoading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanInteract));
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public ObservableCollection<SelectableTrack> Tracks
    {
        get => _tracks;
        set
        {
            _tracks = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TrackCount));
            OnPropertyChanged(nameof(SelectedCount));
        }
    }

    public int TrackCount => Tracks.Count;
    public int SelectedCount => Tracks.Count(t => t.IsSelected);
    public bool CanInteract => !IsLoading;

    public ICommand LoadPlaylistCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand DeselectAllCommand { get; }
    public ICommand CancelCommand { get; }

    public SpotifyImportViewModel(
        ILogger<SpotifyImportViewModel> logger,
        SpotifyInputSource? spotifyApiService,
        SpotifyScraperInputSource spotifyScraperService,
        DownloadManager downloadManager)
    {
        _logger = logger;
        _spotifyApiService = spotifyApiService;
        _spotifyScraperService = spotifyScraperService;
        _downloadManager = downloadManager;

        LoadPlaylistCommand = new AsyncRelayCommand(LoadPlaylistAsync);
        DownloadCommand = new AsyncRelayCommand(DownloadSelectedAsync, () => SelectedCount > 0);
        SelectAllCommand = new RelayCommand(SelectAll);
        DeselectAllCommand = new RelayCommand(DeselectAll);
        CancelCommand = new RelayCommand(Cancel);
    }

    public async Task LoadPlaylistAsync()
    {
        if (string.IsNullOrWhiteSpace(PlaylistUrl))
        {
            StatusMessage = "Please enter a Spotify playlist URL";
            return;
        }

        IsLoading = true;
        StatusMessage = "Loading playlist...";
        Tracks.Clear();

        try
        {
            _logger.LogInformation("Loading Spotify playlist: {Url}", PlaylistUrl);

            List<SearchQuery> queries;

            // Try API first if configured
            if (_spotifyApiService?.IsConfigured == true)
            {
                _logger.LogInformation("Using Spotify API");
                StatusMessage = "Fetching from Spotify API...";
                queries = await _spotifyApiService.ParseAsync(PlaylistUrl);
            }
            else
            {
                _logger.LogInformation("Using Spotify web scraper");
                StatusMessage = "Scraping Spotify web page...";
                queries = await _spotifyScraperService.ParseAsync(PlaylistUrl);
            }

            if (!queries.Any())
            {
                StatusMessage = "No tracks found. Check the URL or playlist privacy settings.";
                return;
            }

            // Set playlist metadata
            PlaylistTitle = queries.FirstOrDefault()?.SourceTitle ?? "Spotify Playlist";
            StatusMessage = $"Loaded {queries.Count} tracks";

            // Convert to SelectableTrack
            int trackNum = 1;
            foreach (var query in queries)
            {
                var track = new Track
                {
                    Title = query.Title,
                    Artist = query.Artist,
                    Album = query.Album,
                    Length = 0 // Will be populated during search
                };

                Tracks.Add(new SelectableTrack(track, trackNum++));
            }

            _logger.LogInformation("Successfully loaded {Count} tracks", Tracks.Count);
            OnPropertyChanged(nameof(TrackCount));
            OnPropertyChanged(nameof(SelectedCount));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Spotify playlist");
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task DownloadSelectedAsync()
    {
        var selectedTracks = Tracks.Where(t => t.IsSelected).ToList();

        if (!selectedTracks.Any())
        {
            StatusMessage = "No tracks selected";
            return;
        }

        _logger.LogInformation("Downloading {Count} selected tracks from Spotify playlist", selectedTracks.Count);

        // Create PlaylistJob
        var job = new PlaylistJob
        {
            Id = Guid.NewGuid(),
            SourceTitle = PlaylistTitle,
            SourceType = "Spotify (UI)",
            CreatedAt = DateTime.Now,
            DestinationFolder = "" // Will use default from config
        };

        // Convert SelectableTracks to PlaylistTracks and attach the PlaylistId
        foreach (var selectable in selectedTracks)
        {
            var playlistTrack = new PlaylistTrack
            {
                Id = Guid.NewGuid(),
                PlaylistId = job.Id,
                Title = selectable.Title,
                Artist = selectable.Artist,
                Album = selectable.Album,
                TrackUniqueHash = $"{selectable.Artist}|{selectable.Title}".ToLowerInvariant(),
                Status = TrackStatus.Missing,
                AddedAt = DateTime.Now
            };

            job.PlaylistTracks.Add(playlistTrack);
        }

        // Use the new DownloadManager overload which persists the PlaylistJob and queues the tracks
        await _downloadManager.QueueProject(job);

        StatusMessage = $"Queued {selectedTracks.Count} tracks for download";
        
        // TODO: Navigate back to Library and show new project
        _logger.LogInformation("Spotify import complete: {Count} tracks queued", selectedTracks.Count);
    }

    private void SelectAll()
    {
        foreach (var track in Tracks)
        {
            track.IsSelected = true;
        }
        OnPropertyChanged(nameof(SelectedCount));
    }

    private void DeselectAll()
    {
        foreach (var track in Tracks)
        {
            track.IsSelected = false;
        }
        OnPropertyChanged(nameof(SelectedCount));
    }

    private void Cancel()
    {
        // TODO: Navigate back or close window
        _logger.LogInformation("Spotify import cancelled");
    }

    public void ReorderTrack(SelectableTrack source, SelectableTrack target)
    {
        if (source == null || target == null || source == target)
            return;

        int oldIndex = Tracks.IndexOf(source);
        int newIndex = Tracks.IndexOf(target);

        if (oldIndex == -1 || newIndex == -1)
            return;

        Tracks.Move(oldIndex, newIndex);

        // Renumber all tracks
        for (int i = 0; i < Tracks.Count; i++)
        {
            Tracks[i].TrackNumber = i + 1;
        }

        _logger.LogDebug("Reordered track from position {Old} to {New}", oldIndex + 1, newIndex + 1);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
