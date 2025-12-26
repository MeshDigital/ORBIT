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
    public SearchFilterViewModel FilterViewModel { get; } = new();

    // Hidden Results Counter
    private int _hiddenResultsCount;
    public int HiddenResultsCount
    {
        get => _hiddenResultsCount;
        set => this.RaiseAndSetIfChanged(ref _hiddenResultsCount, value);
    }
    
    // Selected items for Batch Actions
    public ObservableCollection<SearchResult> SelectedResults { get; } = new();
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

    // Filter properties (Post-Search) - Managed by FilterViewModel now
    
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


    // Commands
    public ICommand UnifiedSearchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand BrowseCsvCommand { get; }
    public ICommand PasteTracklistCommand { get; }
    public ICommand CancelSearchCommand { get; }
    public ICommand AddToDownloadsCommand { get; }
    public ICommand DownloadSelectedCommand { get; }

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
        SearchOrchestrationService searchOrchestration,
        IEventBus eventBus)
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

        // Reactive Status Updates
        eventBus.GetEvent<Events.TrackStateChangedEvent>().Subscribe(OnTrackStateChanged);
        eventBus.GetEvent<TrackAddedEvent>().Subscribe(OnTrackAdded);

        // --- Reactive Pipeline Setup ---
        // Connect SourceList -> Filter -> Sort -> Bind -> Public Collection
        
        // Observe filter changes from Child ViewModel
        var filterPredicate = FilterViewModel.FilterChanged;

        _searchResults.Connect()
            .Filter(filterPredicate)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _publicSearchResults)
            .DisposeMany() // Ensure items are disposed if needed
            .Subscribe(set => 
            {
                // Update Hidden Count
                // Total in source - Total in public view
                // Note: Count retrieval needs to be thread safe, SourceList.Count is generally safe
                HiddenResultsCount = _searchResults.Count - _publicSearchResults.Count;
            });

        // Commands
        var canSearch = this.WhenAnyValue(x => x.SearchQuery, query => !string.IsNullOrWhiteSpace(query));
        
        UnifiedSearchCommand = ReactiveCommand.CreateFromTask(ExecuteUnifiedSearchAsync, canSearch);
        ClearSearchCommand = ReactiveCommand.Create(() => { SearchQuery = ""; _searchResults.Clear(); });
        BrowseCsvCommand = ReactiveCommand.CreateFromTask(ExecuteBrowseCsvAsync);
        PasteTracklistCommand = ReactiveCommand.CreateFromTask(ExecutePasteTracklistAsync);
        CancelSearchCommand = ReactiveCommand.Create(ExecuteCancelSearch);
        AddToDownloadsCommand = ReactiveCommand.CreateFromTask(ExecuteAddToDownloadsAsync);
        DownloadSelectedCommand = ReactiveCommand.CreateFromTask(ExecuteDownloadSelectedAsync);
        
        // Phase 12.6: Bi-directional filter sync - wire callback
        FilterViewModel.OnTokenSyncRequested = HandleTokenSync;
        
        // Note: Import overlay visibility is now handled by ImportOrchestrator via navigation
        // Event handlers for AddedToLibrary and Cancelled are set up in ImportOrchestrator.SetupPreviewCallbacks()
    }

    // Phase 12.6: Token sync methods for bi-directional filter
    private void HandleTokenSync(string token, bool shouldAdd)
    {
        if (shouldAdd)
            InjectToken(token);
        else
            RemoveToken(token);
    }

    private void InjectToken(string token)
    {
        // If it's a bitrate token (ends with +), remove any existing bitrate tokens first
        if (token.EndsWith("+") || token.StartsWith(">"))
        {
            RemoveBitrateTokens();
        }
        
        // Don't duplicate - check with word boundary
        var pattern = $@"\b{System.Text.RegularExpressions.Regex.Escape(token)}\b";
        if (System.Text.RegularExpressions.Regex.IsMatch(SearchQuery ?? "", pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return;
        
        SearchQuery = $"{SearchQuery} {token}".Trim();
    }

    private void RemoveToken(string token)
    {
        // Word-boundary removal
        var pattern = $@"\b{System.Text.RegularExpressions.Regex.Escape(token)}\b";
        var clean = System.Text.RegularExpressions.Regex.Replace(SearchQuery ?? "", pattern, "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Collapse multiple spaces and trim
        SearchQuery = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim();
    }

    private void RemoveBitrateTokens()
    {
        // Remove existing bitrate tokens like "320+", ">320", "256+"
        var pattern = @"\b(\d{2,4}\+?|>\d{2,4})\b";
        var clean = System.Text.RegularExpressions.Regex.Replace(SearchQuery ?? "", pattern, "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        SearchQuery = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim();
    }

    // Removed BuildFilter - logic moved to SearchFilterViewModel

    private async Task ExecuteUnifiedSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;

        // Phase 12.6: Parse tokens without triggering reverse sync
        var processedQuery = new List<string>();
        bool filtersModified = false;
        
        FilterViewModel.SetFromQueryParsing(() =>
        {
            // Reset Filters (Polish: Ensure clean state unless locked - naive reset for now)
            FilterViewModel.Reset();

            // --- VIBE SEARCH: Natural Language Parsing ---
            // Parse tokens like "flac", "wav", ">320", "kbps:320"
            var tokens = SearchQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

            foreach (var token in tokens)
            {
                var lower = token.ToLowerInvariant();
                
                // Format Tokens
                if (lower == "flac") { FilterViewModel.FilterFlac = true; FilterViewModel.FilterMp3 = false; FilterViewModel.FilterWav = false; filtersModified = true; continue; }
                if (lower == "wav") { FilterViewModel.FilterWav = true; FilterViewModel.FilterMp3 = false; FilterViewModel.FilterFlac = false; filtersModified = true; continue; }
                if (lower == "mp3") { FilterViewModel.FilterMp3 = true; FilterViewModel.FilterFlac = false; FilterViewModel.FilterWav = false; filtersModified = true; continue; }
                
                // Quality Tokens (>320 or 320+)
                if (lower.StartsWith(">") && int.TryParse(lower.TrimStart('>'), out int minQ))
                {
                    FilterViewModel.MinBitrate = minQ; 
                    filtersModified = true;
                    continue;
                }
                if (lower.EndsWith("+") && int.TryParse(lower.TrimEnd('+'), out int minQ2))
                {
                    FilterViewModel.MinBitrate = minQ2;
                    filtersModified = true;
                    continue;
                }

                // Normal keyword
                processedQuery.Add(token);
            }
        });

        // Use parsed query if we extracted tokens, otherwise original
        string effectiveQuery = filtersModified ? string.Join(" ", processedQuery) : SearchQuery;
        if (string.IsNullOrWhiteSpace(effectiveQuery)) effectiveQuery = SearchQuery; // Fallback if only tokens provided

        IsSearching = true;
        StatusText = "Searching...";
        _searchResults.Clear(); // Clear reactive list
        AlbumResults.Clear();

        try
        {
            // 1. Check Import Providers (using original query for URL detection)
            var provider = _importProviders.FirstOrDefault(p => p.CanHandle(SearchQuery));
            if (provider != null)
            {
                StatusText = $"Importing via {provider.Name}...";
                IsSearching = false;
                await _importOrchestrator.StartImportWithPreviewAsync(provider, SearchQuery);
                StatusText = "Ready";
                return;
            }

            // 2. Soulseek Streaming Search (Use Cleaned Query)
            StatusText = $"Listening for vibes: {effectiveQuery}...";
            
            var cts = new CancellationTokenSource();
            
            // "Active HUD" - Search Progress Visualization
            var buffer = new List<SearchResult>();
            var lastUpdate = DateTime.UtcNow;
            int totalFound = 0;

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
                    var result = new SearchResult(track);
                    
                    // Check initial status
                    var existing = _downloadManager.ActiveDownloads.FirstOrDefault(d => d.GlobalId == track.UniqueHash);
                    if (existing != null)
                    {
                        result.Status = (existing.State == PlaylistTrackState.Completed) ? TrackStatus.Downloaded :
                                        (existing.State == PlaylistTrackState.Failed) ? TrackStatus.Failed : 
                                        TrackStatus.Missing;
                        // Deferred/Pending/Searching/Downloading all map to Missing in this simple 3-state enum
                    }
                    
                    buffer.Add(result);
                    totalFound++;

                    // Throttled Buffering (User Trick)
                    // Batch UI updates every 250ms or 50 items to prevent stutter
                    if ((DateTime.UtcNow - lastUpdate).TotalMilliseconds > 250 || buffer.Count >= 50)
                    {
                        _searchResults.AddRange(buffer);
                        buffer.Clear();
                        lastUpdate = DateTime.UtcNow;
                        StatusText = $"Found {totalFound} tracks...";
                    }
                }

                // Flush remaining
                if (buffer.Any())
                {
                    _searchResults.AddRange(buffer);
                }
            }
            catch (OperationCanceledException)
            {
                StatusText = "Cancelled";
            }
            finally
            {
                IsSearching = false;
                if (totalFound > 0)
                {
                    // Phase 12.6: Apply percentile-based scoring for visual hierarchy
                    ApplyPercentileScoring();
                    StatusText = $"Found {totalFound} items";
                }
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

    /// <summary>
    /// Phase 12.6: Calculate relative percentile scores for visual hierarchy.
    /// Top results get golden highlighting regardless of absolute score.
    /// </summary>
    private void ApplyPercentileScoring()
    {
        var results = _publicSearchResults.ToList();
        if (!results.Any()) return;
        
        // Sort by rank (already calculated by ResultSorter)
        var sorted = results.OrderByDescending(r => r.CurrentRank).ToList();
        
        for (int i = 0; i < sorted.Count; i++)
        {
            var percentile = (double)i / sorted.Count;
            sorted[i].Percentile = percentile;
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
         // Legacy "Add All Visible/Selected" fallback? 
         // Prefer DownloadSelectedCommand for explicit batch
         await ExecuteDownloadSelectedAsync();
    }

    private async Task ExecuteDownloadSelectedAsync()
    {
        var selected = SelectedResults.ToList();
        if (!selected.Any()) return;

        // Safety Gate
        if (selected.Count > 20)
        {
            // TODO: In a real app we'd show a Dialog here. 
            // For now, we'll log a warning and proceed, or we could just clamp it?
            _logger.LogWarning("Batch download > 20 items requested.");
        }

        foreach (var track in selected)
        {
             // Immediate Feedback (Polish)
             track.Status = TrackStatus.Pending;
             _downloadManager.EnqueueTrack(track.Model);
        }
        StatusText = $"Queued {selected.Count} downloads";
    }

    public void ResetState()
    {
        SearchQuery = "";
        IsSearching = false;
        // Note: IsImportOverlayActive was removed - overlay handled by navigation
        _searchResults.Clear();
        AlbumResults.Clear();
        StatusText = "Ready";
    }

    private void OnTrackStateChanged(Events.TrackStateChangedEvent evt)
    {
        // Update any matching search results via UI thread
        if (_searchResults.Count == 0) return;

        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var status = (evt.NewState == PlaylistTrackState.Completed) ? TrackStatus.Downloaded : 
                         (evt.NewState == PlaylistTrackState.Failed) ? TrackStatus.Failed : 
                         TrackStatus.Missing;

            // Note: We use global ID (hash) to match.
            // SourceList access is thread-safe for reading but we need to modify the ViewModel which is bound.
            // Since SearchResult is an object, we can iterate and update property.
            
            // We need to efficiently find the item.
            // _searchResults is a SourceList. We can just iterate the items we exposed.
            foreach (var result in _publicSearchResults)
            {
                if (result.Model.UniqueHash == evt.TrackGlobalId)
                {
                    result.Status = status;
                }
            }
        });
    }

    private void OnTrackAdded(TrackAddedEvent evt)
    {
         Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var result in _publicSearchResults)
            {
                if (result.Model.UniqueHash == evt.TrackModel.TrackUniqueHash)
                {
                    // It was just added to queue
                    // We might want a "Queued" status in SearchResult, but TrackStatus only has Missing/Downloaded/Failed
                    // We can map Missing to "Queued" visually if we add a property, but for now let's leave it.
                    // Actually, if it's added, it's effectively "Queued".
                    // Let's assume TrackStateChanged checks will handle the granular states (Downloading etc)
                }
            }
        });
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        this.RaiseAndSetIfChanged(ref field, value, propertyName);
        return true;
    }
}
