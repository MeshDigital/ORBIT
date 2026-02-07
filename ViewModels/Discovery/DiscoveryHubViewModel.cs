using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData;

namespace SLSKDONET.ViewModels.Discovery;

/// <summary>
/// ViewModel for the Discovery Hub - the "Command Center" for finding new music.
/// Integrates Spotify library access with advanced Soulseek search orchestration.
/// </summary>
public class DiscoveryHubViewModel : ReactiveObject, IDisposable
{
    private readonly ILogger<DiscoveryHubViewModel> _logger;
    private readonly SpotifyEnrichmentService _spotifyEnrichment;
    private readonly SearchOrchestrationService _searchOrchestration;
    private readonly IDialogService _dialogService;
    private readonly INavigationService _navigationService;
    private readonly CompositeDisposable _disposables = new();

    private bool _isLoadingStore;
    public bool IsLoadingStore
    {
        get => _isLoadingStore;
        set => this.RaiseAndSetIfChanged(ref _isLoadingStore, value);
    }

    private string _searchQuery = string.Empty;
    public string SearchQuery
    {
        get => _searchQuery;
        set => this.RaiseAndSetIfChanged(ref _searchQuery, value);
    }

    private bool _isSearching;
    public bool IsSearching
    {
        get => _isSearching;
        set => this.RaiseAndSetIfChanged(ref _isSearching, value);
    }

    // Spotify Library Collections
    public ObservableCollection<object> UserPlaylists { get; } = new();
    public ObservableCollection<object> SavedTracks { get; } = new();

    // Search Results (Analyzed for the Hub)
    public ObservableCollection<AnalyzedSearchResultViewModel> SearchResults { get; } = new();

    // Commands
    public ICommand RefreshSpotifyCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand LoadPlaylistTracksCommand { get; }

    public DiscoveryHubViewModel(
        ILogger<DiscoveryHubViewModel> logger,
        SpotifyEnrichmentService spotifyEnrichment,
        SearchOrchestrationService searchOrchestration,
        IDialogService dialogService,
        INavigationService navigationService)
    {
        _logger = logger;
        _spotifyEnrichment = spotifyEnrichment;
        _searchOrchestration = searchOrchestration;
        _dialogService = dialogService;
        _navigationService = navigationService;

        RefreshSpotifyCommand = ReactiveCommand.CreateFromTask(RefreshSpotifyLibraryAsync);
        SearchCommand = ReactiveCommand.CreateFromTask(ExecuteCascadeSearchAsync);
        LoadPlaylistTracksCommand = ReactiveCommand.CreateFromTask<string>(LoadSpotifyPlaylistAsync);

        // Auto-refresh on load
        Task.Run(RefreshSpotifyLibraryAsync);
    }

    private async Task RefreshSpotifyLibraryAsync()
    {
        IsLoadingStore = true;
        try
        {
            _logger.LogInformation("Refreshing Spotify Library for Discovery Hub...");
            
            var playlists = await _spotifyEnrichment.GetCurrentUserPlaylistsAsync();
            var tracks = await _spotifyEnrichment.GetCurrentUserSavedTracksAsync(30); // Last 30 liked

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                UserPlaylists.Clear();
                foreach (var p in playlists) UserPlaylists.Add(p);

                SavedTracks.Clear();
                foreach (var t in tracks) SavedTracks.Add(t);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh Spotify library in Hub");
        }
        finally
        {
            IsLoadingStore = false;
        }
    }

    private async Task ExecuteCascadeSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;

        IsSearching = true;
        SearchResults.Clear();
        using var cts = new CancellationTokenSource();

        try
        {
            _logger.LogInformation("Starting Cascade Search in Discovery Hub for: {Query}", SearchQuery);
            
            // Note: In a real implementation, we'd wrap SearchAsync results into AnalyzedSearchResultViewModel
            // with heat-map scoring and metadata enrichment.
            await foreach (var track in _searchOrchestration.SearchAsync(
                SearchQuery, 
                "mp3,flac", // Preferred formats
                192, 320,  // Bitrate range
                false,     // IsAlbum
                cts.Token))
            {
                // UI update
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // This is a placeholder for the actual AnalyzedSearchResultViewModel mapping
                    _logger.LogDebug("Found result in Hub: {Artist} - {Title}", track.Artist, track.Title);
                });
            }
        }
        catch (OperationCanceledException) { /* Normal search stop */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cascade Search failed in Discovery Hub");
        }
        finally
        {
            IsSearching = false;
        }
    }

    private async Task LoadSpotifyPlaylistAsync(string playlistId)
    {
        // Placeholder for loading playlist tracks and queuing them for search/enrichment
        _logger.LogInformation("Loading tracks from Spotify playlist: {Id}", playlistId);
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}
