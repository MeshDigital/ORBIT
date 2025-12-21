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
    private readonly SpotifyAuthService _authService;
    
    private readonly DownloadManager _downloadManager;
    private readonly ImportOrchestrator _importOrchestrator;
    private readonly Services.ImportProviders.SpotifyImportProvider _spotifyProvider;
    private readonly Services.ImportProviders.SpotifyLikedSongsImportProvider _likedSongsProvider;
    private readonly Services.ImportProviders.CsvImportProvider _csvProvider;
    private readonly INavigationService _navigationService;
    private readonly IFileInteractionService _fileInteractionService;
    
    // Properties
    public ObservableCollection<SelectableTrack> Tracks { get; } = new();
    
    private string _playlistUrl = "";
    public string PlaylistUrl
    {
        get => _playlistUrl;
        set { _playlistUrl = value; OnPropertyChanged(); }
    }

    private string _playlistTitle = "Spotify Playlist";
    public string PlaylistTitle
    {
        get => _playlistTitle;
        set { _playlistTitle = value; OnPropertyChanged(); }
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    private string _statusMessage = "";
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public int TrackCount => Tracks.Count;
    public int SelectedCount => Tracks.Count(t => t.IsSelected);

    // User Playlists
    public ObservableCollection<SpotifyPlaylistViewModel> UserPlaylists { get; } = new();
    
    private bool _isAuthenticated;
    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        set 
        { 
            _isAuthenticated = value; 
            OnPropertyChanged(); 
            OnPropertyChanged(nameof(ShowLoginButton));
            OnPropertyChanged(nameof(ShowPlaylists));
        }
    }
    
    public bool ShowLoginButton => !IsAuthenticated;
    public bool ShowPlaylists => IsAuthenticated;

    public ICommand ConnectCommand { get; }
    public ICommand RefreshPlaylistsCommand { get; }
    public ICommand ImportPlaylistCommand { get; }
    public ICommand LoadPlaylistCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand DeselectAllCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ImportLikedSongsCommand { get; }
    public ICommand SelectCsvFileCommand { get; }

    public SpotifyImportViewModel(
        ILogger<SpotifyImportViewModel> logger,
        DownloadManager downloadManager,
        SpotifyAuthService authService,
        ImportOrchestrator importOrchestrator,
        Services.ImportProviders.SpotifyImportProvider spotifyProvider,
        Services.ImportProviders.SpotifyLikedSongsImportProvider likedSongsProvider,
        Services.ImportProviders.CsvImportProvider csvProvider,
        INavigationService navigationService,
        IFileInteractionService fileInteractionService)
    {
        _logger = logger;
        _downloadManager = downloadManager;
        _authService = authService;
        _importOrchestrator = importOrchestrator;
        _spotifyProvider = spotifyProvider;
        _likedSongsProvider = likedSongsProvider;
        _csvProvider = csvProvider;
        _navigationService = navigationService;
        _fileInteractionService = fileInteractionService;
        
        // Subscribe to auth changes
        _authService.AuthenticationChanged += (s, e) => 
        {
            IsAuthenticated = e;
            if (e) _ = RefreshPlaylistsAsync();
        };

        // Initial check
        Task.Run(async () => IsAuthenticated = await _authService.IsAuthenticatedAsync());

        LoadPlaylistCommand = new AsyncRelayCommand(LoadPlaylistAsync);
        SelectAllCommand = new RelayCommand(SelectAll);
        DeselectAllCommand = new RelayCommand(DeselectAll);
        CancelCommand = new RelayCommand(Cancel);
        
        ConnectCommand = new AsyncRelayCommand(ConnectSpotifyAsync);
        RefreshPlaylistsCommand = new AsyncRelayCommand(RefreshPlaylistsAsync);
        ImportPlaylistCommand = new AsyncRelayCommand<SpotifyPlaylistViewModel>(ImportUserPlaylistAsync);
        
        ImportLikedSongsCommand = new AsyncRelayCommand(ExecuteImportLikedSongsAsync, () => IsAuthenticated);
        SelectCsvFileCommand = new AsyncRelayCommand(ExecuteSelectCsvFileAsync);

        // Disable unused commands
        DownloadCommand = new RelayCommand(() => {}, () => false);
    }

    private async Task ExecuteImportLikedSongsAsync()
    {
        _logger.LogInformation("Starting 'Liked Songs' import from Spotify...");
        await _importOrchestrator.StartImportWithPreviewAsync(_likedSongsProvider, "spotify:liked");
    }

    private async Task ExecuteSelectCsvFileAsync()
    {
        try
        {
            var filters = new System.Collections.Generic.List<Services.FileDialogFilter>
            {
                new Services.FileDialogFilter("CSV Files", new System.Collections.Generic.List<string> { "csv" }),
                new Services.FileDialogFilter("All Files", new System.Collections.Generic.List<string> { "*" })
            };

            var filePath = await _fileInteractionService.OpenFileDialogAsync("Select CSV File", filters);

            if (!string.IsNullOrWhiteSpace(filePath))
            {
                await _importOrchestrator.StartImportWithPreviewAsync(_csvProvider, filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to select CSV file");
            StatusMessage = $"Error selecting file: {ex.Message}";
        }
    }

    public async Task LoadPlaylistAsync()
    {
        if (string.IsNullOrWhiteSpace(PlaylistUrl))
        {
            StatusMessage = "Please enter a Spotify playlist URL";
            return;
        }

        _logger.LogInformation("Starting unified Spotify import: {Url}", PlaylistUrl);
        await _importOrchestrator.StartImportWithPreviewAsync(_spotifyProvider, PlaylistUrl);
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
        _navigationService.GoBack();
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

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private async Task ConnectSpotifyAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Connecting to Spotify...";
            await _authService.StartAuthorizationAsync();
            // AuthenticationChanged event will handle the rest
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Spotify");
            StatusMessage = "Connection failed: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task RefreshPlaylistsAsync()
    {
        if (!IsAuthenticated) return;

        try
        {
            IsLoading = true;
            StatusMessage = "Fetching your playlists...";
            UserPlaylists.Clear();

            var client = await _authService.GetAuthenticatedClientAsync();
            var page = await client.Playlists.CurrentUsers();
            
            await foreach (var playlist in client.Paginate(page))
            {
                UserPlaylists.Add(new SpotifyPlaylistViewModel
                {
                    Id = playlist.Id ?? "",
                    Name = playlist.Name ?? "Unnamed Playlist",
                    ImageUrl = playlist.Images?.FirstOrDefault()?.Url ?? "",
                    TrackCount = playlist.Tracks?.Total ?? 0,
                    Owner = playlist.Owner?.DisplayName ?? "Unknown",
                    Url = (playlist.ExternalUrls != null && playlist.ExternalUrls.ContainsKey("spotify")) ? playlist.ExternalUrls["spotify"] : ""
                });
            }

            // Also try to get "My Library" (Liked Songs)
            // Note: Liked songs is a separate endpoint "Library.GetTracks", not a playlist.
            // We can add a fake "Liked Songs" entry if we want, handled by specific provider.
            UserPlaylists.Insert(0, new SpotifyPlaylistViewModel
            {
                Id = "me/tracks",
                Name = "Liked Songs",
                ImageUrl = "https://t.scdn.co/images/3099b3803ad9496896c43f22fe9be8c4.png", // Generic heart icon
                TrackCount = 0, // Hard to get total without a call
                Owner = "You",
                Url = "spotify:user:me:collection" // Special case handled by provider?
            });

            StatusMessage = $"Found {UserPlaylists.Count} playlists";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch playlists");
            StatusMessage = "Failed to load playlists";
            
            // If it's an auth error, we might want to reset auth
            if (ex.Message.Contains("Unauthorized"))
            {
                await _authService.SignOutAsync();
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ImportUserPlaylistAsync(SpotifyPlaylistViewModel? playlist)
    {
        if (playlist == null) return;

        _logger.LogInformation("Importing user playlist: {Name} ({Id})", playlist.Name, playlist.Id);
        
        if (playlist.Id == "me/tracks")
        {
             await _importOrchestrator.StartImportWithPreviewAsync(_likedSongsProvider, "spotify:liked");
        }
        else
        {
             await _importOrchestrator.StartImportWithPreviewAsync(_spotifyProvider, playlist.Url);
        }
    }
}

public class SpotifyPlaylistViewModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public int TrackCount { get; set; }
    public string Owner { get; set; } = "";
    public string Url { get; set; } = "";
    public string Description => $"{TrackCount} tracks • by {Owner}";
}
