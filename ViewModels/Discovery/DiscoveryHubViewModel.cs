using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Models.Discovery;
using SLSKDONET.Services;
using SLSKDONET.Utils;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using DynamicData;

namespace SLSKDONET.ViewModels.Discovery;

/// <summary>
/// ViewModel for the Discovery Hub - the "Command Center" for finding new music.
/// Integrates Spotify library access with advanced Soulseek search orchestration.
/// Supports multiline input for YouTube tracklist parsing (Workbench mode).
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
        set
        {
            this.RaiseAndSetIfChanged(ref _searchQuery, value);
            DetectInputMode(value);
        }
    }

    private bool _isSearching;
    public bool IsSearching
    {
        get => _isSearching;
        set => this.RaiseAndSetIfChanged(ref _isSearching, value);
    }

    private DiscoveryViewMode _currentViewMode = DiscoveryViewMode.Search;
    public DiscoveryViewMode CurrentViewMode
    {
        get => _currentViewMode;
        set => this.RaiseAndSetIfChanged(ref _currentViewMode, value);
    }

    // Type-safe Spotify Library Collections
    public ObservableCollection<SpotifyPlaylistDto> UserPlaylists { get; } = new();
    public ObservableCollection<SpotifySavedTrackDto> SavedTracks { get; } = new();

    // Search Results (Analyzed for the Hub)
    public ObservableCollection<DiscoverySearchResultDto> SearchResults { get; } = new();

    // Workbench for batch tracklist processing
    public ObservableCollection<BatchTrackItem> WorkbenchTracks { get; } = new();

    // Commands
    public ICommand RefreshSpotifyCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand LoadPlaylistTracksCommand { get; }
    public ICommand SearchWorkbenchItemCommand { get; }
    public ICommand SearchAllWorkbenchCommand { get; }
    public ICommand ClearWorkbenchCommand { get; }

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
        SearchCommand = ReactiveCommand.CreateFromTask(ExecuteSmartSearchAsync);
        LoadPlaylistTracksCommand = ReactiveCommand.CreateFromTask<string>(LoadSpotifyPlaylistAsync);
        SearchWorkbenchItemCommand = ReactiveCommand.CreateFromTask<BatchTrackItem>(SearchSingleWorkbenchItemAsync);
        SearchAllWorkbenchCommand = ReactiveCommand.CreateFromTask(SearchAllWorkbenchItemsAsync);
        ClearWorkbenchCommand = ReactiveCommand.Create(ClearWorkbench);

        // Auto-refresh on load
        Task.Run(RefreshSpotifyLibraryAsync);
    }

    /// <summary>
    /// Detect if input is multiline (YouTube tracklist) or single-line (standard search).
    /// Automatically switches to Workbench mode when multiline is detected.
    /// </summary>
    private void DetectInputMode(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            CurrentViewMode = DiscoveryViewMode.Search;
            return;
        }

        // Check for multiline input (YouTube tracklist indicator)
        if (input.Contains('\n') || input.Contains('\r'))
        {
            _logger.LogInformation("Multiline input detected. Switching to Workbench mode.");
            ParseAndPopulateWorkbench(input);
            CurrentViewMode = DiscoveryViewMode.Workbench;
        }
        else
        {
            // Single line - standard search mode
            if (CurrentViewMode == DiscoveryViewMode.Workbench && WorkbenchTracks.Count == 0)
            {
                CurrentViewMode = DiscoveryViewMode.Search;
            }
        }
    }

    /// <summary>
    /// Parse multiline input using CommentTracklistParser and populate the Workbench.
    /// </summary>
    private void ParseAndPopulateWorkbench(string rawText)
    {
        var parsedTracks = CommentTracklistParser.Parse(rawText);
        
        WorkbenchTracks.Clear();
        foreach (var track in parsedTracks)
        {
            WorkbenchTracks.Add(new BatchTrackItem
            {
                Artist = track.Artist,
                Title = track.Title,
                OriginalLine = $"{track.Artist} - {track.Title}",
                IsSearched = false,
                ResultCount = 0
            });
        }

        _logger.LogInformation("Parsed {Count} tracks from multiline input into Workbench.", WorkbenchTracks.Count);
    }

    /// <summary>
    /// Smart search: If in Workbench mode, search selected items. Otherwise, cascade search.
    /// </summary>
    private async Task ExecuteSmartSearchAsync()
    {
        if (CurrentViewMode == DiscoveryViewMode.Workbench)
        {
            await SearchAllWorkbenchItemsAsync();
        }
        else
        {
            await ExecuteCascadeSearchAsync();
        }
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
                foreach (var p in playlists)
                {
                    // Dynamic projection to DTO (safe even if 'object')
                    try
                    {
                        dynamic playlist = p;
                        UserPlaylists.Add(new SpotifyPlaylistDto
                        {
                            Id = playlist.Id ?? string.Empty,
                            Name = playlist.Name ?? "Unknown Playlist",
                            ImageUrl = (playlist.Images?.Count > 0) ? playlist.Images[0].Url : null,
                            TrackCount = playlist.Tracks?.Total ?? 0,
                            OwnerName = playlist.Owner?.DisplayName
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to map playlist to DTO");
                    }
                }

                SavedTracks.Clear();
                foreach (var t in tracks)
                {
                    try
                    {
                        dynamic saved = t;
                        SavedTracks.Add(new SpotifySavedTrackDto
                        {
                            Id = saved.Track?.Id ?? string.Empty,
                            Name = saved.Track?.Name ?? "Unknown Track",
                            Artist = (saved.Track?.Artists?.Count > 0) ? saved.Track.Artists[0].Name : "Unknown Artist",
                            Album = saved.Track?.Album?.Name,
                            AddedAt = saved.AddedAt
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to map saved track to DTO");
                    }
                }
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
            
            var resultsList = new System.Collections.Generic.List<Track>();
            
            await foreach (var track in _searchOrchestration.SearchAsync(
                SearchQuery, 
                "mp3,flac", // Preferred formats
                192, 320,  // Bitrate range
                false,     // IsAlbum
                cts.Token))
            {
                resultsList.Add(track);
            }

            // Sort by bitrate descending (Quality Bias)
            var sortedResults = resultsList.OrderByDescending(t => t.Bitrate).ToList();
            
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var track in sortedResults.Take(100)) // Limit to top 100
                {
                    SearchResults.Add(TrackToDto(track));
                }
            });
            
            _logger.LogInformation("Cascade Search complete. Found {Count} results (showing top 100).", resultsList.Count);
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

    private async Task SearchSingleWorkbenchItemAsync(BatchTrackItem item)
    {
        if (item == null) return;

        item.IsSearched = true;
        IsSearching = true;

        try
        {
            var query = item.SearchQuery;
            _logger.LogInformation("Searching P2P for Workbench item: {Query}", query);
            
            int count = 0;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            
            await foreach (var track in _searchOrchestration.SearchAsync(
                query, "mp3,flac", 192, 320, false, cts.Token))
            {
                count++;
                // Add to SearchResults for display
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    SearchResults.Add(TrackToDto(track));
                });
            }

            item.ResultCount = count;
            _logger.LogInformation("Workbench search for '{Query}' found {Count} results.", query, count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search Workbench item: {Item}", item.SearchQuery);
        }
        finally
        {
            IsSearching = false;
        }
    }

    private async Task SearchAllWorkbenchItemsAsync()
    {
        IsSearching = true;
        SearchResults.Clear();

        try
        {
            foreach (var item in WorkbenchTracks.Where(t => !t.IsSearched))
            {
                await SearchSingleWorkbenchItemAsync(item);
                await Task.Delay(500); // Stagger to avoid rate limiting
            }
        }
        finally
        {
            IsSearching = false;
        }
    }

    private void ClearWorkbench()
    {
        WorkbenchTracks.Clear();
        SearchResults.Clear();
        CurrentViewMode = DiscoveryViewMode.Search;
        SearchQuery = string.Empty;
    }

    private async Task LoadSpotifyPlaylistAsync(string playlistId)
    {
        if (string.IsNullOrWhiteSpace(playlistId)) return;

        _logger.LogInformation("Loading tracks from Spotify playlist: {Id}", playlistId);
        IsLoadingStore = true;

        try
        {
            // Get tracks from the playlist and add to Workbench
            var playlistTracks = await _spotifyEnrichment.GetPlaylistTracksAsync(playlistId);
            
            WorkbenchTracks.Clear();
            foreach (var track in playlistTracks)
            {
                try
                {
                    dynamic t = track;
                    string artist = (t.Track?.Artists?.Count > 0) ? t.Track.Artists[0].Name : "Unknown";
                    string title = t.Track?.Name ?? "Unknown";
                    
                    WorkbenchTracks.Add(new BatchTrackItem
                    {
                        Artist = artist,
                        Title = title,
                        OriginalLine = $"{artist} - {title}",
                        IsSearched = false,
                        ResultCount = 0
                    });
                }
                catch { /* Skip malformed tracks */ }
            }

            CurrentViewMode = DiscoveryViewMode.Workbench;
            _logger.LogInformation("Loaded {Count} tracks from playlist into Workbench.", WorkbenchTracks.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Spotify playlist: {Id}", playlistId);
        }
        finally
        {
            IsLoadingStore = false;
        }
    }

    /// <summary>
    /// Convert a Track object to a DiscoverySearchResultDto for display.
    /// </summary>
    private static DiscoverySearchResultDto TrackToDto(Track track)
    {
        return new DiscoverySearchResultDto
        {
            DisplayTitle = track.Title ?? System.IO.Path.GetFileNameWithoutExtension(track.Filename ?? "Unknown"),
            DisplayArtist = track.Artist ?? "Unknown Artist",
            Bitrate = track.Bitrate,
            Format = System.IO.Path.GetExtension(track.Filename ?? "").TrimStart('.').ToUpperInvariant(),
            FileSize = track.Size ?? 0,
            Username = track.Username ?? string.Empty,
            FullPath = track.Filename ?? string.Empty,
            QualityScore = CalculateQualityScore(track),
            Track = track
        };
    }

    /// <summary>
    /// Calculate a quality score (0-100) based on bitrate and format.
    /// </summary>
    private static int CalculateQualityScore(Track track)
    {
        int score = 0;
        
        // Bitrate contribution (max 60 points)
        var bitrate = track.Bitrate;
        if (bitrate >= 320) score += 60;
        else if (bitrate >= 256) score += 50;
        else if (bitrate >= 192) score += 40;
        else if (bitrate >= 128) score += 25;
        else score += 10;
        
        // Format contribution (max 40 points)
        var ext = System.IO.Path.GetExtension(track.Filename ?? "").ToLowerInvariant();
        if (ext == ".flac") score += 40;
        else if (ext == ".wav" || ext == ".aiff") score += 35;
        else if (ext == ".mp3" || ext == ".m4a") score += 20;
        else if (ext == ".aac") score += 15;
        else score += 5;
        
        return Math.Min(100, score);
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}
