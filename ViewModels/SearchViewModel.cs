using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Media;
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
using SLSKDONET.Configuration;
using System.Reactive.Disposables;

namespace SLSKDONET.ViewModels;

public partial class SearchViewModel : ReactiveObject, IDisposable
{
    private readonly ILogger<SearchViewModel> _logger;
    private readonly SoulseekAdapter _soulseek;
    private readonly AppConfig _config;
    private readonly ConfigManager _configManager;
    private readonly ImportOrchestrator _importOrchestrator;
    private readonly IEnumerable<IImportProvider> _importProviders;
    private readonly DownloadManager _downloadManager;
    private readonly INavigationService _navigationService;
    private readonly IFileInteractionService _fileInteractionService;
    private readonly IClipboardService _clipboardService;
    private readonly SearchOrchestrationService _searchOrchestration;

    private readonly FileNameFormatter _fileNameFormatter;
    
    // Cleanup
    private readonly CompositeDisposable _disposables = new();
    private bool _isDisposed;

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
    public ObservableCollection<AnalyzedSearchResultViewModel> SelectedResults { get; } = new();
    private string _searchQuery = "";
    public string SearchQuery
    {
        get => _searchQuery;
        set 
        { 
            if (SetProperty(ref _searchQuery, value))
            {
                this.RaisePropertyChanged(nameof(CanSearch));
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
    private readonly SourceList<AnalyzedSearchResultViewModel> _searchResults = new();
    private readonly ReadOnlyObservableCollection<AnalyzedSearchResultViewModel> _publicSearchResults;
    public ReadOnlyObservableCollection<AnalyzedSearchResultViewModel> SearchResults => _publicSearchResults;
    
    // Album Results (Legacy/Separate collection for now)
    public ObservableCollection<AlbumResultViewModel> AlbumResults { get; } = new();

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

    // Phase 5: Ranking Weights (Control Surface)
    public double BitrateWeight
    {
        get => _config.CustomWeights.QualityWeight;
        set 
        { 
            _config.CustomWeights.QualityWeight = value;
            this.RaisePropertyChanged();
            OnRankingWeightsChanged();
            _configManager.Save(_config);
        }
    }

    public double ReliabilityWeight
    {
        get => _config.CustomWeights.AvailabilityWeight;
        set 
        { 
            _config.CustomWeights.AvailabilityWeight = value;
            this.RaisePropertyChanged(); // Notify UI
            OnRankingWeightsChanged();
            _configManager.Save(_config);
        }
    }

    public double MatchWeight
    {
        get => _config.CustomWeights.MusicalWeight;
        set 
        { 
            _config.CustomWeights.MusicalWeight = value;
            _config.CustomWeights.MetadataWeight = value;
            _config.CustomWeights.StringWeight = value;
            
            this.RaisePropertyChanged();
            OnRankingWeightsChanged();
            _configManager.Save(_config);
        }
    }

    // Format Toggles (Zone C)
    public bool IsFlacEnabled
    {
        get => FilterViewModel.FilterFlac;
        set { FilterViewModel.FilterFlac = value; this.RaisePropertyChanged(); OnRankingWeightsChanged(); }
    }
    
    public bool IsMp3Enabled
    {
        get => FilterViewModel.FilterMp3;
        set { FilterViewModel.FilterMp3 = value; this.RaisePropertyChanged(); OnRankingWeightsChanged(); }
    }

    public bool IsWavEnabled
    {
        get => FilterViewModel.FilterWav;
        set { FilterViewModel.FilterWav = value; this.RaisePropertyChanged(); OnRankingWeightsChanged(); }
    }


    // Commands
    public ICommand UnifiedSearchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand BrowseCsvCommand { get; }
    public ICommand PasteTracklistCommand { get; }
    public ICommand CancelSearchCommand { get; }
    public ICommand AddToDownloadsCommand { get; }
    public ReactiveCommand<object?, System.Reactive.Unit> DownloadSelectedCommand { get; }
    public ICommand ApplyPresetCommand { get; } // Phase 5: Search Presets

    public SearchViewModel(
        ILogger<SearchViewModel> logger,
        SoulseekAdapter soulseek,
        AppConfig config,
        ConfigManager configManager,
        ImportOrchestrator importOrchestrator,
        IEnumerable<IImportProvider> importProviders,
        ImportPreviewViewModel importPreviewViewModel,
        DownloadManager downloadManager,
        INavigationService navigationService,
        IFileInteractionService fileInteractionService,
        IClipboardService clipboardService,
        SearchOrchestrationService searchOrchestration,
        FileNameFormatter fileNameFormatter,
        IEventBus eventBus)
    {
        _logger = logger;
        _soulseek = soulseek;
        _config = config;
        _configManager = configManager;
        _importOrchestrator = importOrchestrator;
        _importProviders = importProviders;
        ImportPreviewViewModel = importPreviewViewModel;
        _downloadManager = downloadManager;
        _navigationService = navigationService;
        _fileInteractionService = fileInteractionService;
        _clipboardService = clipboardService;
        _searchOrchestration = searchOrchestration;
        _fileNameFormatter = fileNameFormatter;

        // Reactive Status Updates
        eventBus.GetEvent<TrackStateChangedEvent>()
            .Subscribe(OnTrackStateChanged)
            .DisposeWith(_disposables);
            
        eventBus.GetEvent<TrackAddedEvent>()
            .Subscribe(OnTrackAdded)
            .DisposeWith(_disposables);
            
        // Phase 6: Hybrid Search
        eventBus.GetEvent<FindSimilarRequestEvent>()
            .Subscribe(OnFindSimilarRequest)
            .DisposeWith(_disposables);

        // --- Reactive Pipeline Setup ---
        var filterPredicate = FilterViewModel.FilterChanged;

        _searchResults.Connect()
            .Filter(FilterViewModel.FilterChanged.Select(f => new Func<AnalyzedSearchResultViewModel, bool>(vm => f(vm.RawResult)))) 
            .Sort(SortExpressionComparer<AnalyzedSearchResultViewModel>.Descending(t => t.TrustScore))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _publicSearchResults)
            .DisposeMany() 
            .Subscribe(_ => 
            {
                HiddenResultsCount = _searchResults.Count - _publicSearchResults.Count;
                this.RaisePropertyChanged(nameof(SearchResults)); 
            })
            .DisposeWith(_disposables);


        // Commands
        var canSearch = this.WhenAnyValue(x => x.SearchQuery, query => !string.IsNullOrWhiteSpace(query));
        
        UnifiedSearchCommand = ReactiveCommand.CreateFromTask(ExecuteUnifiedSearchAsync, canSearch);
        ClearSearchCommand = ReactiveCommand.Create(() => { SearchQuery = ""; _searchResults.Clear(); });
        BrowseCsvCommand = ReactiveCommand.CreateFromTask(ExecuteBrowseCsvAsync);
        PasteTracklistCommand = ReactiveCommand.CreateFromTask(ExecutePasteTracklistAsync);
        CancelSearchCommand = ReactiveCommand.Create(ExecuteCancelSearch);
        AddToDownloadsCommand = ReactiveCommand.CreateFromTask(ExecuteAddToDownloadsAsync);
        DownloadSelectedCommand = ReactiveCommand.CreateFromTask<object?>(ExecuteDownloadSelectedAsync);
        ApplyPresetCommand = ReactiveCommand.Create<string>(ExecuteApplyPreset);
        
        FilterViewModel.OnTokenSyncRequested = HandleTokenSync;
    }

    private void HandleTokenSync(string token, bool shouldAdd)
    {
        if (shouldAdd)
            InjectToken(token);
        else
            RemoveToken(token);
    }

    private void InjectToken(string token)
    {
        if (token.EndsWith("+") || token.StartsWith(">"))
        {
            RemoveBitrateTokens();
        }
        
        var pattern = $@"\b{System.Text.RegularExpressions.Regex.Escape(token)}\b";
        if (System.Text.RegularExpressions.Regex.IsMatch(SearchQuery ?? "", pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return;
        
        SearchQuery = $"{SearchQuery} {token}".Trim();
    }

    private void RemoveToken(string token)
    {
        var pattern = $@"\b{System.Text.RegularExpressions.Regex.Escape(token)}\b";
        var clean = System.Text.RegularExpressions.Regex.Replace(SearchQuery ?? "", pattern, "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        SearchQuery = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim();
    }

    private void RemoveBitrateTokens()
    {
        var pattern = @"\b(\d{2,4}\+?|>\d{2,4})\b";
        var clean = System.Text.RegularExpressions.Regex.Replace(SearchQuery ?? "", pattern, "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        SearchQuery = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim();
    }

    private async Task ExecuteUnifiedSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;

        var processedQuery = new List<string>();
        bool filtersModified = false;
        
        FilterViewModel.SetFromQueryParsing(() =>
        {
            FilterViewModel.Reset();

            var tokens = SearchQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

            foreach (var token in tokens)
            {
                var lower = token.ToLowerInvariant();
                
                if (lower == "flac") { FilterViewModel.FilterFlac = true; FilterViewModel.FilterMp3 = false; FilterViewModel.FilterWav = false; filtersModified = true; continue; }
                if (lower == "wav") { FilterViewModel.FilterWav = true; FilterViewModel.FilterMp3 = false; FilterViewModel.FilterFlac = false; filtersModified = true; continue; }
                if (lower == "mp3") { FilterViewModel.FilterMp3 = true; FilterViewModel.FilterFlac = false; FilterViewModel.FilterWav = false; filtersModified = true; continue; }
                
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

                processedQuery.Add(token);
            }
        });

        string effectiveQuery = filtersModified ? string.Join(" ", processedQuery) : SearchQuery;
        if (string.IsNullOrWhiteSpace(effectiveQuery)) effectiveQuery = SearchQuery; 

        IsSearching = true;
        StatusText = "Searching...";
        _searchResults.Clear(); 
        AlbumResults.Clear();
        HiddenResultsCount = 0; 

        try
        {
            var provider = _importProviders.FirstOrDefault(p => p.CanHandle(SearchQuery));
            if (provider != null)
            {
                StatusText = $"Importing via {provider.Name}...";
                IsSearching = false;
                await _importOrchestrator.StartImportWithPreviewAsync(provider, SearchQuery);
                StatusText = "Ready";
                return;
            }

            StatusText = $"Listening for vibes: {effectiveQuery}...";
            
            var cts = new System.Threading.CancellationTokenSource();
            
            var buffer = new List<AnalyzedSearchResultViewModel>();
            var lastUpdate = DateTime.UtcNow;
            int totalFound = 0;

            try 
            {
                await foreach (var track in _searchOrchestration.SearchAsync(
                    SearchQuery,
                    string.Join(",", PreferredFormats),
                    MinBitrate, 
                    MaxBitrate,
                    IsAlbumSearch,
                    cts.Token))
                {
                    var result = new SearchResult(track);
                    
                    var existing = _downloadManager.ActiveDownloads.FirstOrDefault(d => d.GlobalId == track.UniqueHash);
                    if (existing != null)
                    {
                        result.Status = (existing.State == PlaylistTrackState.Completed) ? TrackStatus.Downloaded :
                                        (existing.State == PlaylistTrackState.Failed) ? TrackStatus.Failed : 
                                        TrackStatus.Missing;
                    }
                    
                    // WRAP IN VIEWMODEL
                    var vm = new AnalyzedSearchResultViewModel(result);
                    
                    buffer.Add(vm);
                    totalFound++;

                    if ((DateTime.UtcNow - lastUpdate).TotalMilliseconds > 250 || buffer.Count >= 50)
                    {
                        _searchResults.AddRange(buffer);
                        buffer.Clear();
                        lastUpdate = DateTime.UtcNow;
                        StatusText = $"Found {totalFound} tracks...";
                    }
                }

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
                    ApplyPercentileScoring(); // Updated for AnalyzedSearchResultViewModel
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

    private void ApplyPercentileScoring()
    {
        var results = _publicSearchResults.ToList();
        if (!results.Any()) return;
        
        var sorted = results.OrderByDescending(r => r.RawResult.CurrentRank).ToList();
        
        for (int i = 0; i < sorted.Count; i++)
        {
            var percentile = (double)i / sorted.Count;
            sorted[i].RawResult.Percentile = percentile;
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
    }

    private async Task ExecuteAddToDownloadsAsync()
    {
         await ExecuteDownloadSelectedAsync(null);
    }

    private async Task ExecuteDownloadSelectedAsync(object? parameter)
    {
        var toDownload = new List<AnalyzedSearchResultViewModel>();
        
        if (parameter is AnalyzedSearchResultViewModel single)
        {
            toDownload.Add(single);
        }
        else
        {
            toDownload.AddRange(SelectedResults);
        }

        if (!toDownload.Any()) return;

        if (toDownload.Count > 20)
        {
            _logger.LogWarning("Batch download > 20 items requested.");
        }

        foreach (var vm in toDownload)
        {
             vm.RawResult.Status = TrackStatus.Pending;
             _downloadManager.EnqueueTrack(vm.RawResult.Model);
        }
        StatusText = $"Queued {toDownload.Count} downloads";
    }

    public void ResetState()
    {
        SearchQuery = "";
        IsSearching = false;
        _searchResults.Clear();
        AlbumResults.Clear();
        StatusText = "Ready";
    }

    private void ExecuteApplyPreset(string presetName)
    {
        switch (presetName)
        {
            case "Deep Dive":
                BitrateWeight = 0.5;
                ReliabilityWeight = 0.5;
                MatchWeight = 2.0;
                IsFlacEnabled = false; 
                break;
            case "Quick Grab":
                BitrateWeight = 1.5;
                ReliabilityWeight = 2.0;
                MatchWeight = 1.0;
                break;
            case "High Fidelity":
                BitrateWeight = 2.0;
                ReliabilityWeight = 1.0;
                MatchWeight = 1.0;
                IsFlacEnabled = true;
                IsMp3Enabled = false;
                break;
             case "Balanced":
             default:
                BitrateWeight = 1.0;
                ReliabilityWeight = 1.0;
                MatchWeight = 1.0;
                IsFlacEnabled = false;
                IsMp3Enabled = false;
                break;
        }
    }

    private void OnTrackStateChanged(TrackStateChangedEvent evt)
    {
        if (_searchResults.Count == 0) return;

        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var status = evt.State switch
            {
                PlaylistTrackState.Completed => TrackStatus.Downloaded,
                PlaylistTrackState.Failed => TrackStatus.Failed,
                PlaylistTrackState.Downloading => TrackStatus.Pending,
                PlaylistTrackState.Searching => TrackStatus.Pending,
                PlaylistTrackState.Pending => TrackStatus.Pending,
                _ => TrackStatus.Missing
            };

            foreach (var result in _publicSearchResults)
            {
                if (result.RawResult.Model.UniqueHash == evt.TrackGlobalId)
                {
                    result.RawResult.Status = status;
                }
            }
        });
    }

    private void OnTrackAdded(TrackAddedEvent evt)
    {
         Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Placeholder for future use
        });
    }

    private void OnFindSimilarRequest(FindSimilarRequestEvent evt)
    {
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            // 1. Navigate to Search Page (if not already there)
            _navigationService.NavigateTo("Search");
            
            // 2. Prepare UI
            SearchQuery = $"sonic: {evt.SeedTrack.Title}"; // Display indicator
            StatusText = $"Finding sonic twins for '{evt.SeedTrack.Title}'...";
            IsSearching = true;
            _searchResults.Clear();
            AlbumResults.Clear();
            HiddenResultsCount = 0;
            
            try 
            {
                // 3. Execute Vector Search
                var results = await _searchOrchestration.SearchSimilarAsync(evt.SeedTrack.TrackUniqueHash);
                
                // 4. Update Results
                if (results.Any())
                {
                    var viewModels = results.Select(t => 
                    {
                        // Use a specific SearchResult wrapper
                        var sr = new SearchResult(t); 
                        sr.Status = TrackStatus.Downloaded; // They are library tracks
                        
                        // Rank is already synced with Model in SearchResult constructor/wrapper
                        // sr.CurrentRank = t.CurrentRank; 
                        
                        return new AnalyzedSearchResultViewModel(sr);
                    }).ToList();
                    
                    _searchResults.AddRange(viewModels);
                    StatusText = $"Found {results.Count} sonic twins";
                }
                else
                {
                    StatusText = "No similar tracks found (check if analysis is complete)";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sonic search failed");
                StatusText = "Sonic search failed";
            }
            finally
            {
                IsSearching = false;
            }
        });
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            _disposables.Dispose();
            _searchResults.Dispose();
            // Cancel any active search
            if (IsSearching)
            {
                ExecuteCancelSearch();
            }
        }

        _isDisposed = true;
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        this.RaiseAndSetIfChanged(ref field, value, propertyName);
        return true;
    }

    private void OnRankingWeightsChanged()
    {
        var weights = _config.CustomWeights;
        ResultSorter.SetWeights(weights);
        RecalculateScores();
    }

    private void RecalculateScores()
    {
        if (_searchResults.Count == 0 || string.IsNullOrWhiteSpace(SearchQuery)) return;

        var artist = "";
        var title = SearchQuery;
        
        if (SearchQuery.Contains(" - "))
        {
            var parts = SearchQuery.Split(new[] { " - " }, 2, StringSplitOptions.RemoveEmptyEntries);
            artist = parts[0].Trim();
            title = parts[1].Trim();
        }
        else if (SearchQuery.Contains(" – ")) 
        {
            var parts = SearchQuery.Split(new[] { " – " }, 2, StringSplitOptions.RemoveEmptyEntries);
            artist = parts[0].Trim();
            title = parts[1].Trim();
        }

        var searchTrack = new Models.Track { Artist = artist, Title = title }; 

        var evaluator = new Models.FileConditionEvaluator();
        
        evaluator.AddPreferred(new Models.BitrateCondition 
        { 
            MinBitrate = MinBitrate, 
            MaxBitrate = MaxBitrate 
        });

        evaluator.AddPreferred(new Models.FormatCondition 
        { 
            AllowedFormats = GetActiveFormats() 
        });

        var items = _searchResults.Items.ToList();
        foreach (var item in items)
        {
             ResultSorter.CalculateRank(item.RawResult.Model, searchTrack, evaluator);
             item.RawResult.RefreshRank(); 
        }
    }
    
    private List<string> GetActiveFormats()
    {
        var list = new List<string>();
        if (IsFlacEnabled) list.Add("flac");
        if (IsMp3Enabled) list.Add("mp3");
        if (IsWavEnabled) list.Add("wav");
        if (list.Count == 0) list.AddRange(new[] { "mp3", "flac", "wav" }); 
        return list;
    }
}
