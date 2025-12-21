using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.ImportProviders;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

public class SearchViewModel : INotifyPropertyChanged
{
    private readonly ILogger<SearchViewModel> _logger;
    private readonly SoulseekAdapter _soulseek;
    private readonly ImportOrchestrator _importOrchestrator;
    private readonly IEnumerable<IImportProvider> _importProviders;
    private readonly DownloadManager _downloadManager;
    private readonly INavigationService _navigationService;
    private readonly IFileInteractionService _fileInteractionService;
    private readonly IClipboardService _clipboardService;
    private readonly SearchOrchestrationService _searchOrchestration;

    public IEnumerable<string> PreferredFormats => new[] { "mp3", "flac", "m4a", "wav" }; // TODO: Load from config

    // Child ViewModels
    public ImportPreviewViewModel ImportPreviewViewModel { get; }
    public SpotifyImportViewModel SpotifyImportViewModel { get; }

    // Search input state
    private string _searchQuery = "";
    public string SearchQuery
    {
        get => _searchQuery;
        set 
        { 
            if (SetProperty(ref _searchQuery, value))
            {
                OnPropertyChanged(nameof(CanSearch));
                ((AsyncRelayCommand)UnifiedSearchCommand).RaiseCanExecuteChanged();
            }
        }
    }

    private bool _isAlbumSearch;
    public bool IsAlbumSearch
    {
        get => _isAlbumSearch;
        set
        {
            if (SetProperty(ref _isAlbumSearch, value))
            {
                SearchResults.Clear();
                AlbumResults.Clear();
            }
        }
    }
    
    private bool _isSpotifyBrowseMode;
    public bool IsSpotifyBrowseMode
    {
        get => _isSpotifyBrowseMode;
        set => SetProperty(ref _isSpotifyBrowseMode, value);
    }

    private bool _isSearching;
    public bool IsSearching
    {
        get => _isSearching;
        set => SetProperty(ref _isSearching, value);
    }
    
    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    // Results
    public ObservableCollection<SearchResult> SearchResults { get; } = new();
    public ObservableCollection<AlbumResultViewModel> AlbumResults { get; } = new();

    // Selection
    public int SelectedTrackCount => SearchResults.Count(t => t.IsSelected);

    // Filter/Ranking State
    public RankingPreset RankingPreset { get; set; } = RankingPreset.Balanced;
    public int MinBitrate { get; set; } = 320;
    public int MaxBitrate { get; set; } = 3000;

    // UI State
    public bool IsImportPreviewVisible => _navigationService.CurrentPage?.GetType().Name.Contains("ImportPreview") == true;

    public bool CanSearch => !string.IsNullOrWhiteSpace(SearchQuery);

    // Commands
    public ICommand UnifiedSearchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand BrowseCsvCommand { get; }
    public ICommand PasteTracklistCommand { get; }
    public ICommand BrowseSpotifyCommand { get; }
    public ICommand CancelSearchCommand { get; }
    public ICommand AddToDownloadsCommand { get; }
    public ICommand DownloadAlbumCommand { get; } 

    public event PropertyChangedEventHandler? PropertyChanged;

    public SearchViewModel(
        ILogger<SearchViewModel> logger,
        SoulseekAdapter soulseek,
        ImportOrchestrator importOrchestrator,
        IEnumerable<IImportProvider> importProviders,
        ImportPreviewViewModel importPreviewViewModel,
        SpotifyImportViewModel spotifyImportViewModel,
        DownloadManager downloadManager,
        INavigationService navigationService,
        IFileInteractionService fileInteractionService,
        IClipboardService clipboardService,
        SearchOrchestrationService searchOrchestration)
    {
        _logger = logger;
        _soulseek = soulseek;
        _importOrchestrator = importOrchestrator;
        _importProviders = importProviders;
        ImportPreviewViewModel = importPreviewViewModel;
        SpotifyImportViewModel = spotifyImportViewModel;
        _downloadManager = downloadManager;
        _navigationService = navigationService;
        _fileInteractionService = fileInteractionService;
        _clipboardService = clipboardService;
        _searchOrchestration = searchOrchestration;

        UnifiedSearchCommand = new AsyncRelayCommand(ExecuteUnifiedSearchAsync, () => CanSearch);
        ClearSearchCommand = new RelayCommand(() => SearchQuery = "");
        BrowseCsvCommand = new AsyncRelayCommand(ExecuteBrowseCsvAsync);
        PasteTracklistCommand = new AsyncRelayCommand(ExecutePasteTracklistAsync);
        BrowseSpotifyCommand = new RelayCommand(() => IsSpotifyBrowseMode = !IsSpotifyBrowseMode);
        CancelSearchCommand = new RelayCommand(ExecuteCancelSearch);
        AddToDownloadsCommand = new AsyncRelayCommand(ExecuteAddToDownloadsAsync);
        DownloadAlbumCommand = new RelayCommand<object>(param => { /* TODO: Implement single album download */ });
    }

    private async Task ExecuteUnifiedSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;

        IsSearching = true;
        StatusText = "Processing...";
        SearchResults.Clear();
        AlbumResults.Clear();

        try
        {
            // 1. Check if any registered Import Provider can handle this input
            var provider = _importProviders.FirstOrDefault(p => p.CanHandle(SearchQuery));
            if (provider != null)
            {
                StatusText = $"Importing via {provider.Name}...";
                await _importOrchestrator.StartImportWithPreviewAsync(provider, SearchQuery);
                IsSearching = false;
                StatusText = "Import ready";
                return;
            }

            // 2. Default: Soulseek Search
            StatusText = $"Searching Soulseek for '{SearchQuery}'...";
            
            // Pass the callback to handle results as they stream in
            // 2. Default: Soulseek Search via Orchestrator
            StatusText = $"Searching Soulseek for '{SearchQuery}'...";
            
            // Pass the callback to handle results as they stream in
            var result = await _searchOrchestration.SearchAsync(
                SearchQuery,
                string.Join(",", PreferredFormats),
                MinBitrate, 
                MaxBitrate,
                IsAlbumSearch,
                OnTracksFound,
                CancellationToken.None
            );
            
            // Auto-hide spinner after 5 seconds if results found
            _ = Task.Delay(5000).ContinueWith(_ => 
            {
                Dispatcher.UIThread.Post(() => 
                {
                    if (IsSearching && SearchResults.Any()) 
                        IsSearching = false;
                });
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed");
            StatusText = $"Error: {ex.Message}";
            IsSearching = false;
        }
    }

    private void OnTracksFound(IEnumerable<Track> tracks)
    {
        // QUICK WIN: Batch UI updates to prevent freeze
        const int BATCH_SIZE = 50;
        var trackList = tracks.ToList();
        
        Dispatcher.UIThread.Post(() =>
        {
            // Add in batches with small delays
            for (int i = 0; i < trackList.Count; i += BATCH_SIZE)
            {
                var batch = trackList.Skip(i).Take(BATCH_SIZE);
                
                foreach (var track in batch)
                {
                    // Wrap Track in SearchResult ViewModel
                    var result = new SearchResult(track);
                    SearchResults.Add(result);
                }
                
                // Update status after each batch
                StatusText = $"Found {SearchResults.Count} tracks...";
                
                // Small yield to keep UI responsive
                if (i + BATCH_SIZE < trackList.Count)
                {
                    Task.Delay(1).Wait(); // Minimal delay to yield to UI thread
                }
            }
        });
    }

    private async Task ExecuteBrowseCsvAsync()
    {
        try
        {
            var path = await _fileInteractionService.OpenFileDialogAsync("Select CSV File", new[] 
            { 
                new FileDialogFilter("CSV Files", new List<string> { "csv" }),
                new FileDialogFilter("All Files", new List<string> { "*" })
            });

            if (!string.IsNullOrEmpty(path))
            {
                SearchQuery = path; 
                await ExecuteUnifiedSearchAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error browsing for CSV");
            StatusText = "Error selecting file";
        }
    }

    private async Task ExecutePasteTracklistAsync()
    {
        try 
        {
            var text = await _clipboardService.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text)) 
            {
                StatusText = "Clipboard is empty";
                return;
            }

            // Check if any provider can handle this text
            var provider = _importProviders.FirstOrDefault(p => p.CanHandle(text));
            if (provider != null)
            {
                 StatusText = $"Importing from Clipboard ({provider.Name})...";
                 await _importOrchestrator.StartImportWithPreviewAsync(provider, text);
            }
            else
            {
                StatusText = "Clipboard content recognition failed.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pasting from clipboard");
            StatusText = "Clipboard error";
        }
    }

    private void ExecuteCancelSearch()
    {
        IsSearching = false;
        StatusText = "Cancelled";
        // _soulseek.CancelSearch(); // If/when supported by adapter
    }

    private async Task ExecuteAddToDownloadsAsync()
    {
        var selected = SearchResults.Where(t => t.IsSelected).ToList();
        if (!selected.Any()) return;

        foreach (var track in selected)
        {
             _downloadManager.EnqueueTrack(track.Model);
        }
        StatusText = $"Queued {selected.Count} downloads";
        await Task.CompletedTask;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
