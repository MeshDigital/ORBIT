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
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using System.Reactive.Linq;

namespace SLSKDONET.ViewModels;

public partial class SearchViewModel : ReactiveObject
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

    // Search input state
    private string _searchQuery = "";
    public string SearchQuery
    {
        get => _searchQuery;
        set 
        { 
            if (SetProperty(ref _searchQuery, value))
            {
                this.RaisePropertyChanged(nameof(CanSearch));
                // UnifiedSearchCommand: ReactiveCommand handles CanExecute auto-magically
            }
        }
    }
    
    public bool CanSearch => !string.IsNullOrWhiteSpace(SearchQuery);

    private bool _isAlbumSearch;
    public bool IsAlbumSearch
    {
        get => _isAlbumSearch;
        set
        {
            if (SetProperty(ref _isAlbumSearch, value))
            {
                _searchResults.Clear();
                AlbumResults.Clear();
            }
        }
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

    // Reactive State
    private readonly SourceList<SearchResult> _searchResults = new();
    private readonly ReadOnlyObservableCollection<SearchResult> _publicSearchResults;
    public ReadOnlyObservableCollection<SearchResult> SearchResults => _publicSearchResults;
    
    // Album Results (Legacy/Separate collection for now)
    public ObservableCollection<AlbumResultViewModel> AlbumResults { get; } = new();

    // Filter properties (Post-Search)
    private int _filterMinBitrate = 320;
    private bool _filterMp3 = true;
    private bool _filterFlac = true;
    private bool _filterWav = true;
    
    // Search Parameters (Pre-Search)
    private int _minBitrate = 320;
    public int MinBitrate 
    {
        get => _minBitrate;
        set => this.RaiseAndSetIfChanged(ref _minBitrate, value);
    }
    
    private int _maxBitrate = 3000;
    public int MaxBitrate 
    {
        get => _maxBitrate;
        set => this.RaiseAndSetIfChanged(ref _maxBitrate, value);
    }

    // Import Overlay State (Fix for ZIndex bug)
    private bool _isImportOverlayActive;
    public bool IsImportOverlayActive 
    {
        get => _isImportOverlayActive;
        set => SetProperty(ref _isImportOverlayActive, value);
    }
    
    // Dynamic Filters
    public int FilterMinBitrate 
    {
        get => _filterMinBitrate;
        set { SetProperty(ref _filterMinBitrate, value); } 
    }

    public bool FilterMp3 
    {
        get => _filterMp3;
        set { SetProperty(ref _filterMp3, value); }
    }

    public bool FilterFlac 
    {
        get => _filterFlac;
        set { SetProperty(ref _filterFlac, value); }
    }

    public bool FilterWav 
    {
        get => _filterWav;
        set { SetProperty(ref _filterWav, value); }
    }

    // Commands
    public ICommand UnifiedSearchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand BrowseCsvCommand { get; }
    public ICommand PasteTracklistCommand { get; }
    public ICommand CancelSearchCommand { get; }
    public ICommand AddToDownloadsCommand { get; }

    public SearchViewModel(
        ILogger<SearchViewModel> logger,
        SoulseekAdapter soulseek,
        ImportOrchestrator importOrchestrator,
        IEnumerable<IImportProvider> importProviders,
        ImportPreviewViewModel importPreviewViewModel,
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
        _downloadManager = downloadManager;
        _navigationService = navigationService;
        _fileInteractionService = fileInteractionService;
        _clipboardService = clipboardService;
        _searchOrchestration = searchOrchestration;
        _isImportOverlayActive = false; // Initialize explicitly

        // --- Reactive Pipeline Setup ---
        // Connect SourceList -> Filter -> Sort -> Bind -> Public Collection
        
        var filterPredicate = this.WhenAnyValue(
            x => x.FilterMinBitrate,
            x => x.FilterMp3,
            x => x.FilterFlac,
            x => x.FilterWav)
            .Select(BuildFilter);

        _searchResults.Connect()
            .Filter(filterPredicate)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _publicSearchResults)
            .Subscribe();

        // Commands
        var canSearch = this.WhenAnyValue(x => x.SearchQuery, query => !string.IsNullOrWhiteSpace(query));
        
        UnifiedSearchCommand = ReactiveCommand.CreateFromTask(ExecuteUnifiedSearchAsync, canSearch);
        ClearSearchCommand = ReactiveCommand.Create(() => { SearchQuery = ""; _searchResults.Clear(); });
        BrowseCsvCommand = ReactiveCommand.CreateFromTask(ExecuteBrowseCsvAsync);
        PasteTracklistCommand = ReactiveCommand.CreateFromTask(ExecutePasteTracklistAsync);
        CancelSearchCommand = ReactiveCommand.Create(ExecuteCancelSearch);
        AddToDownloadsCommand = ReactiveCommand.CreateFromTask(ExecuteAddToDownloadsAsync);
        
        // Listen for Import Preview completion to close overlay
        importPreviewViewModel.AddedToLibrary += (s, e) => IsImportOverlayActive = false;
        importPreviewViewModel.Cancelled += (s, e) => IsImportOverlayActive = false;
    }

    private Func<SearchResult, bool> BuildFilter((int minKbps, bool mp3, bool flac, bool wav) tuple)
    {
        return result =>
        {
            if (result.Model == null) return true;
            
            // 1. Bitrate Check
            if (result.Model.Bitrate < tuple.minKbps) return false;

            // 2. Format Check
            var ext = System.IO.Path.GetExtension(result.Model.Filename)?.ToLowerInvariant().TrimStart('.');
            if (string.IsNullOrEmpty(ext)) return true; // Keep unknown?

            if (ext == "mp3" && !tuple.mp3) return false;
            if (ext == "flac" && !tuple.flac) return false;
            if (ext == "wav" && !tuple.wav) return false;

            return true;
        };
    }

    private async Task ExecuteUnifiedSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;

        IsSearching = true;
        StatusText = "Processing...";
        _searchResults.Clear(); // Clear reactive list
        AlbumResults.Clear();

        try
        {
            // 1. Check Import Providers
            var provider = _importProviders.FirstOrDefault(p => p.CanHandle(SearchQuery));
            if (provider != null)
            {
                StatusText = $"Importing via {provider.Name}...";
                IsImportOverlayActive = true; // Show overlay
                await _importOrchestrator.StartImportWithPreviewAsync(provider, SearchQuery);
                IsSearching = false;
                StatusText = "Import ready";
                return;
            }

            // 2. Soulseek Streaming Search
            StatusText = $"Searching: {SearchQuery}...";
            
            var cts = new CancellationTokenSource();
            
            try 
            {
                // Consume the IAsyncEnumerable stream
                await foreach (var track in _searchOrchestration.SearchAsync(
                    SearchQuery,
                    string.Join(",", PreferredFormats),
                    MinBitrate, 
                    MaxBitrate,
                    IsAlbumSearch,
                    cts.Token))
                {
                    // Add directly to SourceList - it handles thread safety and notifications!
                    _searchResults.Add(new SearchResult(track));
                    
                    // Optional: throttle status updates
                    if (_searchResults.Count % 10 == 0)
                        StatusText = $"Found {_searchResults.Count} tracks...";
                }
            }
            catch (OperationCanceledException)
            {
                StatusText = "Cancelled";
            }
            finally
            {
                IsSearching = false;
                if (_searchResults.Count > 0) StatusText = $"Found {_searchResults.Count} items";
                else StatusText = "No results found";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed");
            StatusText = $"Error: {ex.Message}";
            IsSearching = false;
        }
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

    public void ResetState()
    {
        SearchQuery = "";
        IsSearching = false;
        IsImportOverlayActive = false;
        _searchResults.Clear();
        AlbumResults.Clear();
        StatusText = "Ready";
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        this.RaiseAndSetIfChanged(ref field, value, propertyName);
        return true;
    }
}
