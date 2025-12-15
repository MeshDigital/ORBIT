using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Windows.Input;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services.InputParsers;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using SLSKDONET.Views;
using Avalonia.Threading;
using System.Collections.Concurrent;
using System.IO;

namespace SLSKDONET.Views;

/// <summary>
/// ViewModel for the main window.
/// </summary>
public class MainViewModel : INotifyPropertyChanged
{
    private readonly ILogger<MainViewModel> _logger;
    private readonly AppConfig _config;
    private readonly ConfigManager _configManager;
    private readonly SoulseekAdapter _soulseek;
    private readonly DownloadManager _downloadManager;
    private readonly ILibraryService _libraryService;
    private readonly IFileInteractionService _fileInteractionService;
    private readonly Services.ImportProviders.TracklistImportProvider _tracklistImportProvider;
    private string _username = "";
    private PlaylistJob? _currentPlaylistJob;
    private bool _isConnected = false;
    private bool _isSearching = false;
    private bool _isDiagnosticsRunning;
    private string _statusText = "Disconnected";
    private string _downloadPath = "";
    private string _sharedFolderPath = "";
    private int _maxConcurrentDownloads = 2;
    private string _fileNameFormat = "{artist} - {title}";
    private int _selectedTrackCount;
    private string _preferredFormats = "mp3,flac";

    private CancellationTokenSource _searchCts = new();



    private bool _isAlbumSearch;
    public bool IsAlbumSearch
    {
        get => _isAlbumSearch;
        set => SetProperty(ref _isAlbumSearch, value);
    }

    private string _applicationVersion = "Unknown";
    public string ApplicationVersion
    {
        get => _applicationVersion;
        set => SetProperty(ref _applicationVersion, value);
    }

    public ObservableCollection<Track> SearchResults { get; } = new();

    // Search Filters
    private string _selectedFormat = "All";
    public string SelectedFormat
    {
        get => _selectedFormat;
        set
        {
            if (_selectedFormat != value)
            {
                _selectedFormat = value;
                OnPropertyChanged();
            }
        }
    }

    private int _minBitrateFilter = 0;
    public int MinBitrateFilter
    {
        get => _minBitrateFilter;
        set
        {
            if (_minBitrateFilter != value)
            {
                _minBitrateFilter = value;
                OnPropertyChanged();
            }
        }
    }

    private int _minFileSizeFilter = 0;
    public int MinFileSizeFilter
    {
        get => _minFileSizeFilter;
        set
        {
            if (_minFileSizeFilter != value)
            {
                _minFileSizeFilter = value;
                OnPropertyChanged();
            }
        }
    }

    public ObservableCollection<AlbumResultViewModel> AlbumResults { get; } = new();
    public ObservableCollection<DownloadJob> Downloads { get; } = new();
    public ObservableCollection<SearchQuery> ImportedQueries { get; } = new();
    public ObservableCollection<LibraryEntry> LibraryEntries { get; } = new();
    public ObservableCollection<OrchestratedQueryProgress> OrchestratedQueries { get; } = new();
    
    // Import Preview ViewModel (for CSV/Spotify imports)
    public ImportPreviewViewModel? ImportPreviewViewModel { get; private set; }
    public PlayerViewModel PlayerViewModel { get; }
    
    // Navigation - Current page being displayed
    private object? _currentPage;
    public object? CurrentPage
    {
        get => _currentPage;
        set => SetProperty(ref _currentPage, value);
    }
    
    // Page instances (lazy-loaded)
    private object? _searchPage;
    private object? _libraryPage;
    private object? _downloadsPage;
    private object? _settingsPage;
    
    // Store LibraryViewModel for navigation
    private LibraryViewModel? _libraryViewModel;
    
    private bool _isImportPreviewVisible;
    public bool IsImportPreviewVisible
    {
        get => _isImportPreviewVisible;
        set => SetProperty(ref _isImportPreviewVisible, value);
    }

    // --- Unified Search Properties ---

    private SearchInputMode _currentSearchMode = SearchInputMode.AdHoc;
    public SearchInputMode CurrentSearchMode
    {
        get => _currentSearchMode;
        set
        {
            if (SetProperty(ref _currentSearchMode, value))
            {
                OnPropertyChanged(nameof(SearchInputPlaceholder));
            }
        }
    }

    public string SearchInputPlaceholder => CurrentSearchMode switch
    {
        SearchInputMode.AdHoc => "Search Soulseek (e.g. 'Artist - Title')...",
        SearchInputMode.SpotifyLink => "Paste Spotify Playlist or Album URL...",
        SearchInputMode.CsvFile => "Enter path to CSV file or Drag & Drop...",
        _ => "Search..."
    };

    private RankingPreset _rankingPreset = RankingPreset.Balanced;
    public RankingPreset RankingPreset
    {
        get => _rankingPreset;
        set
        {
            if (SetProperty(ref _rankingPreset, value))
            {
                _config.RankingPreset = value.ToString();
                _configManager.Save(_config);
            }
        }
    }

    public int MinBitrate
    {
        get => _config.PreferredMinBitrate;
        set
        {
            if (_config.PreferredMinBitrate != value)
            {
                _config.PreferredMinBitrate = value;
                OnPropertyChanged();
                _configManager.Save(_config);
            }
        }
    }

    public int MaxBitrate
    {
        get => _config.PreferredMaxBitrate;
        set
        {
            if (_config.PreferredMaxBitrate != value)
            {
                _config.PreferredMaxBitrate = value;
                OnPropertyChanged();
                _configManager.Save(_config);
            }
        }
    }


    private async Task ExecuteUnifiedSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
             _notificationService.Show("Search Error", "Please enter a search term, URL, or file path.", NotificationType.Warning);
            return;
        }

        // Auto-detect mode based on input
        var mode = DetermineInputMode(SearchQuery);
        
        // Update the mode property so the UI (placeholder etc) updates if needed
        CurrentSearchMode = mode;

        switch (mode)
        {
            case SearchInputMode.AdHoc:
                if (!string.IsNullOrWhiteSpace(SearchQuery))
                {
                    await SearchAsync(); 
                }
                break;

            case SearchInputMode.SpotifyLink:
                await ImportFromSpotifyAsync(SearchQuery);
                break;

            case SearchInputMode.CsvFile:
                await ImportCsvAsync(SearchQuery);
                break;
        }
    }

    private SearchInputMode DetermineInputMode(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return SearchInputMode.AdHoc; 
        }

        var q = query.Trim();

        // 1. Spotify Autodetection
        if (q.Contains("spotify.com", StringComparison.OrdinalIgnoreCase) || 
            q.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))
        {
            return SearchInputMode.SpotifyLink;
        }

        // 2. CSV Autodetection (check file extension or path chars)
        // We can be looser here for the detection, verifying existence in the import method
        if (q.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return SearchInputMode.CsvFile;
        }

        // 3. Ad Hoc (default)
        return SearchInputMode.AdHoc;
    }

    public ICommand BrowseCsvCommand { get; }
    public ICommand PasteTracklistCommand { get; }

    private async Task ExecuteBrowseCsvAsync()
    {
        var result = await _fileInteractionService.OpenFileDialogAsync(
            "Select CSV File", 
            new[] { new FileDialogFilter("CSV Files", new List<string> { "csv" }) });

        if (!string.IsNullOrEmpty(result))
        {
            SearchQuery = result;
            // Optionally trigger the search immediately
            // await ExecuteUnifiedSearchAsync();
        }
    }

    private async Task ExecutePasteTracklistAsync()
    {
        try
        {
            // Get multi-line input from user
            var rawText = _userInputService.GetInput(
                "Paste your tracklist here (timestamps will be removed automatically):",
                "Paste Tracklist");

            if (string.IsNullOrWhiteSpace(rawText))
                return;

            _logger.LogInformation("Processing pasted tracklist ({Length} chars)", rawText.Length);

            // Use the TracklistImportProvider to parse
            var result = await _tracklistImportProvider.ImportAsync(rawText);

            if (!result.Success)
            {
                StatusText = result.ErrorMessage ?? "Failed to parse tracklist";
                _logger.LogWarning("Tracklist parse failed: {Error}", result.ErrorMessage);
                return;
            }

            // Show preview page
            if (result.Tracks.Any())
            {
                _logger.LogInformation("Initializing ImportPreviewViewModel for {Count} pasted tracks", result.Tracks.Count);
                var importPreviewVm = CreateImportPreviewViewModel();
                await importPreviewVm.InitializePreviewAsync(
                    result.SourceTitle,
                    "Pasted Tracklist",
                    result.Tracks);

                ImportPreviewViewModel = importPreviewVm;
                OnPropertyChanged(nameof(ImportPreviewViewModel));

                // Show the preview in the search page
                IsImportPreviewVisible = true;
                OnPropertyChanged(nameof(IsImportPreviewVisible));

                StatusText = $"Preview: {result.Tracks.Count} tracks loaded from pasted text";
            }
            else
            {
                StatusText = "No tracks found in pasted text";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Paste failed: {ex.Message}";
            _logger.LogError(ex, "Failed to paste tracklist");
        }
    }



    private bool _isNavigationCollapsed;
    public bool IsNavigationCollapsed
    {
        get => _isNavigationCollapsed;
        set => SetProperty(ref _isNavigationCollapsed, value);
    }

    // Player location toggle (sidebar vs bottom)
    private bool _isPlayerAtBottom = false;
    public bool IsPlayerAtBottom
    {
        get => _isPlayerAtBottom;
        set
        {
            if (SetProperty(ref _isPlayerAtBottom, value))
            {
                OnPropertyChanged(nameof(IsPlayerInSidebar));
            }
        }
    }

    public bool IsPlayerInSidebar => !_isPlayerAtBottom;
    
    private bool _isPlayerSidebarVisible = true;
    public bool IsPlayerSidebarVisible
    {
        get => _isPlayerSidebarVisible;
        set => SetProperty(ref _isPlayerSidebarVisible, value);
    }

    // --- Downloads Page Search & Filtering ---
    private string _downloadsSearchText = "";
    public string DownloadsSearchText
    {
        get => _downloadsSearchText;
        set
        {
            SetProperty(ref _downloadsSearchText, value);
            // Filter logic handled by DownloadsPage or binding
        }
    }

    public ObservableCollection<PlaylistTrackViewModel> FilteredGlobalTracks { get; private set; } = new();
    public ICommand DeleteTrackCommand { get; }

    // Surface download manager counters compatibility
    public int SuccessfulCount => _downloadManager.AllGlobalTracks.Count(t => t.State == ViewModels.PlaylistTrackState.Completed);
    public int FailedCount => _downloadManager.AllGlobalTracks.Count(t => t.State == ViewModels.PlaylistTrackState.Failed);
    public int TodoCount => _downloadManager.AllGlobalTracks.Count(t => t.State == ViewModels.PlaylistTrackState.Pending || t.State == ViewModels.PlaylistTrackState.Downloading || t.State == ViewModels.PlaylistTrackState.Searching);

    /// <summary>
    /// Exposes the global tracks collection from DownloadManager for binding in DownloadsPage.
    /// </summary>
    public ObservableCollection<PlaylistTrackViewModel> AllGlobalTracks => _downloadManager.AllGlobalTracks;

    /// <summary>
    /// Calculate overall download progress percentage for the global progress bar.
    /// </summary>
    public double DownloadProgressPercentage
    {
        get
        {
            var total = SuccessfulCount + FailedCount + TodoCount;
            if (total == 0) return 0;
            return (SuccessfulCount / (double)total) * 100;
        }
    }

    private void OnTrackUpdated(object? sender, PlaylistTrackViewModel e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(SuccessfulCount));
            OnPropertyChanged(nameof(FailedCount));
            OnPropertyChanged(nameof(TodoCount));
            OnPropertyChanged(nameof(DownloadProgressPercentage));
        });
    }
    public ICommand LoginCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand AddToDownloadsCommand { get; }
    public ICommand ImportCsvCommand { get; }
    public ICommand StartDownloadsCommand { get; }
    public ICommand ImportFromSpotifyCommand { get; }
    // public ICommand RemoveFromLibraryCommand { get; } // LEGACY
    public ICommand RescanLibraryCommand { get; }
    public ICommand CancelDownloadsCommand { get; }
    public ICommand ToggleFiltersPanelCommand { get; }
    public ICommand QuickDownloadCommand { get; }
    public ICommand ClearSearchHistoryCommand { get; }
    public ICommand BrowseDownloadPathCommand { get; }
    public ICommand BrowseSharedFolderCommand { get; }
    public ICommand ShowPauseComingSoonCommand { get; }
    public ICommand SearchAllImportedCommand { get; }
    public ICommand RunDiagnosticsCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand CancelSearchCommand { get; }
    public ICommand NavigateSearchCommand { get; }
    public ICommand NavigateLibraryCommand { get; }
    public ICommand NavigateDownloadsCommand { get; }
    public ICommand NavigateSettingsCommand { get; }
    public ICommand ShowLoginCommand { get; }
    public ICommand DismissLoginCommand { get; }

    public ICommand DisconnectCommand { get; }
    public ICommand ToggleNavigationCommand { get; }
    public ICommand TogglePlayerCommand { get; }
    
    // Unified Search Commands
    public ICommand UnifiedSearchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand TogglePlayerLocationCommand { get; }

    private readonly INotificationService _notificationService;
    private readonly INavigationService _navigationService;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainViewModel(
        ILogger<MainViewModel> logger,
        AppConfig config,
        SoulseekAdapter soulseek,
        DownloadManager downloadManager,
        ILibraryService libraryService,        
        SpotifyScraperInputSource spotifyScraperInputSource,
        SpotifyInputSource spotifyInputSource,
        SearchQueryNormalizer searchQueryNormalizer,
        ConfigManager configManager,
        INavigationService navigationService,
        DownloadLogService downloadLogService,
        INotificationService notificationService,
        ProtectedDataService protectedDataService,
        IUserInputService userInputService,
        CsvInputSource csvInputSource, // Add CsvInputSource dependency
        IFileInteractionService fileInteractionService,
        Services.ImportProviders.TracklistImportProvider tracklistImportProvider,
        PlayerViewModel playerViewModel)
    {
        _logger = logger;
        _logger.LogInformation("=== MainViewModel Constructor Started ===");
        
        _config = config;
        _configManager = configManager;
        _soulseek = soulseek;
        _downloadLogService = downloadLogService;
        _navigationService = navigationService;
        _notificationService = notificationService;
        _downloadManager = downloadManager;
        _libraryService = libraryService;
        _csvInputSource = csvInputSource;
        _spotifyScraperInputSource = spotifyScraperInputSource;
        _spotifyInputSource = spotifyInputSource;
        _protectedDataService = protectedDataService;
        _userInputService = userInputService;
        _searchQueryNormalizer = searchQueryNormalizer; // Store it
        _fileInteractionService = fileInteractionService;
        _tracklistImportProvider = tracklistImportProvider;
        SpotifyClientId = _config.SpotifyClientId;
        SpotifyClientSecret = _config.SpotifyClientSecret;
        
        PlayerViewModel = playerViewModel;
        
        _logger.LogInformation("Dependencies injected successfully");

        // Load initial settings
        Username = _config.Username ?? "";
        DownloadPath = _config.DownloadDirectory ?? "";
        SharedFolderPath = _config.SharedFolderPath ?? "";
        MaxConcurrentDownloads = _config.MaxConcurrentDownloads;
        FileNameFormat = _config.NameFormat ?? "{artist} - {title}";
        PreferredFormats = string.Join(",", _config.PreferredFormats ?? new List<string> { "mp3", "flac" });
        CheckForDuplicates = _config.CheckForDuplicates;
        RememberPassword = _config.RememberPassword;
        UseSpotifyApi = !_config.SpotifyUsePublicOnly;
        MinBitrate = _config.PreferredMinBitrate;
        MaxBitrate = _config.PreferredMaxBitrate;

        // Initialize commands
        LoginCommand = new AsyncRelayCommand<string>(LoginAsync, (pwd) => !IsConnected && !string.IsNullOrEmpty(pwd));
        SearchCommand = new AsyncRelayCommand(SearchAsync, () =>
            !IsSearching && !string.IsNullOrEmpty(SearchQuery) && 
            (IsConnected || LooksLikeSpotifyUrl(SearchQuery) || LooksLikeCsvFilePath(SearchQuery)));
        AddToDownloadsCommand = new RelayCommand<IList<object>?>(AddToDownloads, items => items is { Count: > 0 });
        ImportCsvCommand = new AsyncRelayCommand<string>(ImportCsvAsync, filePath => !string.IsNullOrEmpty(filePath));
        ImportFromSpotifyCommand = new AsyncRelayCommand(() => ImportFromSpotifyAsync());
        // RemoveFromLibraryCommand = new RelayCommand<IList<object>?>(RemoveFromLibrary, items => items is { Count: > 0 }); // LEGACY

        
        // Downloads Page Commands
        DeleteTrackCommand = new AsyncRelayCommand<PlaylistTrackViewModel>(async (vm) => 
        {
             if (vm != null) await _downloadManager.DeleteTrackFromDiskAndHistoryAsync(vm);
        });
        
        // Bind filtered downloads directly to the observable collection (simple Avalonia approach)
        FilteredGlobalTracks = _downloadManager.AllGlobalTracks;

        RescanLibraryCommand = new AsyncRelayCommand(RescanLibraryAsync);
        StartDownloadsCommand = new AsyncRelayCommand(StartDownloadsAsync, () => Downloads.Any(j => j.State == DownloadState.Pending));
        CancelDownloadsCommand = new RelayCommand(CancelDownloads);
        ToggleFiltersPanelCommand = new RelayCommand(ToggleFiltersPanel);
        QuickDownloadCommand = new RelayCommand<Track?>(QuickDownload);
        BrowseDownloadPathCommand = new RelayCommand(BrowseDownloadPath);
        BrowseSharedFolderCommand = new RelayCommand(BrowseSharedFolder);
        SearchAllImportedCommand = new AsyncRelayCommand(SearchAllImportedAsync, () => ImportedQueries.Any() && !IsSearching);
        RunDiagnosticsCommand = new AsyncRelayCommand(RunDiagnosticsHarnessAsync);
        ShowPauseComingSoonCommand = new RelayCommand(() => StatusText = "Pause functionality is planned for a future update!");
        CancelSearchCommand = new RelayCommand(CancelSearch, () => IsSearching);
        ChangeViewModeCommand = new RelayCommand<string?>(mode => { if (!string.IsNullOrEmpty(mode)) CurrentViewMode = mode; });
        SelectImportCommand = new RelayCommand<SearchQuery?>(import => SelectedImport = import);
        ClearSearchHistoryCommand = new RelayCommand(ClearSearchHistory);
        SaveSettingsCommand = new RelayCommand(() =>
        {
            UpdateConfigFromViewModel();
            configManager.Save(_config);
            StatusText = "Settings saved successfully!";
        });


        // Initialize        // Navigation commands
        NavigateSearchCommand = new RelayCommand(NavigateToSearch);
        NavigateLibraryCommand = new RelayCommand(NavigateToLibrary);
        NavigateDownloadsCommand = new RelayCommand(NavigateToDownloads);
        NavigateSettingsCommand = new RelayCommand(NavigateToSettings);
        
        // Initialize login overlay commands
        ShowLoginCommand = new RelayCommand(() => 
        {
            _loginDismissed = false;
            OnPropertyChanged(nameof(IsLoginOverlayVisible));
        });
        
        DismissLoginCommand = new RelayCommand(() => 
        {
            _loginDismissed = true;
            OnPropertyChanged(nameof(IsLoginOverlayVisible));
        });
        
        DisconnectCommand = new RelayCommand(() =>
        {
            _soulseek.Disconnect();
            IsConnected = false;
            _loginDismissed = false; // Show login overlay again
            OnPropertyChanged(nameof(IsLoginOverlayVisible));
            StatusText = "Disconnected";
        });

        ToggleNavigationCommand = new RelayCommand(() => IsNavigationCollapsed = !IsNavigationCollapsed);
        TogglePlayerCommand = new RelayCommand(() => IsPlayerSidebarVisible = !IsPlayerSidebarVisible);
        
        // Subscribe to download events
        // REMOVED: DownloadManager events are deprecated in Bundle 1 refactor.
        // Subscribe to download events
        _downloadManager.TrackUpdated += OnTrackUpdated;
        _downloadManager.AllGlobalTracks.CollectionChanged += (s, e) => 
        {
             OnPropertyChanged(nameof(SuccessfulCount));
             OnPropertyChanged(nameof(FailedCount));
             OnPropertyChanged(nameof(TodoCount));
             OnPropertyChanged(nameof(DownloadProgressPercentage));
        };
        
        // Subscribe to Soulseek state changes
        _soulseek.EventBus.Subscribe(evt =>
        {
            if (evt.eventType == "state_changed")
            {
                try
                {
                    dynamic data = evt.data;
                    string state = data.state;
                    HandleStateChange(state);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to handle state change event");
                }
            }
        });
        
        
        // Set application version from assembly
        try
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            ApplicationVersion = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
            _logger.LogInformation($"Application Version: {ApplicationVersion}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get application version");
            ApplicationVersion = "1.0.0";
        }
        
        _logger.LogInformation($"MainViewModel initialized. IsConnected={_isConnected}, IsSearching={_isSearching}, StatusText={_statusText}");
        _logger.LogInformation("=== MainViewModel Constructor Completed ===");


        // Set default page to Search
        // Unified Search Commands
        // Unified Search Commands
        UnifiedSearchCommand = new AsyncRelayCommand(ExecuteUnifiedSearchAsync, () => !IsSearching);
        BrowseCsvCommand = new AsyncRelayCommand(ExecuteBrowseCsvAsync);
        PasteTracklistCommand = new AsyncRelayCommand(ExecutePasteTracklistAsync);
        ClearSearchCommand = new RelayCommand(() => SearchQuery = "", () => !string.IsNullOrEmpty(SearchQuery));
        TogglePlayerLocationCommand = new RelayCommand(() => IsPlayerAtBottom = !IsPlayerAtBottom);
        
        NavigateToSearch();
    }
    
    // Navigation Methods
    private void NavigateToSearch()
    {
        if (_searchPage == null)
        {
            _searchPage = new Views.Avalonia.SearchPage { DataContext = this };
        }
        CurrentPage = _searchPage;
    }
    
    private void NavigateToLibrary()
    {
        if (_libraryPage == null)
        {
            // Get LibraryViewModel from services if not already set
            if (_libraryViewModel == null && App.Current is App app && app.Services != null)
            {
                _libraryViewModel = app.Services.GetService(typeof(LibraryViewModel)) as LibraryViewModel;
            }
            _libraryPage = new Views.Avalonia.LibraryPage { DataContext = _libraryViewModel };
        }
        CurrentPage = _libraryPage;
    }
    
    private void NavigateToDownloads()
    {
        if (_downloadsPage == null)
        {
            _downloadsPage = new Views.Avalonia.DownloadsPage { DataContext = this };
        }
        CurrentPage = _downloadsPage;
    }
    
    private void NavigateToSettings()
    {
        if (_settingsPage == null)
        {
            _settingsPage = new Views.Avalonia.SettingsPage { DataContext = this };
        }
        CurrentPage = _settingsPage;
    }

    // The field '_isLibraryLoaded' is assigned but its value is never used. It has been removed.

    /// <summary>
    /// Called when the view is loaded to initialize library entries.
    /// </summary>
    public void OnViewLoaded()
    {
        _logger.LogInformation("OnViewLoaded called");
        
        // Log configuration state for debugging
        _logger.LogInformation($"Config - Username: {(_config.Username != null ? "<set>" : "<null>")}, " +
                              $"Password: {(_config.Password != null ? "<set>" : "<null>")}, " +
                              $"RememberPassword: {_config.RememberPassword}");
        
        // Attempt auto-login if credentials are saved
        // Auto-login is now optional and controlled by user preference via AutoConnectEnabled
        // Don't automatically attempt login on startup - let user decide via login screen
        _logger.LogInformation("App startup complete - showing login overlay");
        
        // Load library asynchronously to avoid blocking UI thread
        // _ = LoadLibraryAsync(); // LEGACY - Library now managed by LibraryViewModel
    }
    
    private async Task AutoLoginAsync()
    {
        // Only attempt auto-login if user has enabled it and has saved credentials
        if (!AutoConnectEnabled || string.IsNullOrEmpty(_config.Password))
        {
            _logger.LogInformation("Auto-login: disabled or no saved credentials");
            return;
        }

        try
        {
            // Give user 2 seconds to dismiss the overlay if they want
            _logger.LogInformation("Auto-login: waiting 2 seconds before attempting login...");
            await Task.Delay(2000);
            
            // Check if user manually dismissed the overlay during the delay
            if (_loginDismissed)
            {
                _logger.LogInformation("Auto-login cancelled - user dismissed overlay");
                return;
            }
            
            var decryptedPassword = _protectedDataService.Unprotect(_config.Password);
            Username = _config.Username ?? "";
            await LoginAsync(decryptedPassword);
            _logger.LogInformation("Auto-login successful");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-login failed, showing login screen");
            // Login overlay will remain visible since IsConnected is still false
        }
    }
    
    private void HandleStateChange(string? state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _logger.LogInformation("Handling state change: {State}", state);
            
            // Map Soulseek states to our IsConnected property
            if (state == "Connected" || state == "LoggedIn")
            {
                IsConnected = true;
                StatusText = "";
            }
            else if (state == "Disconnected")
            {
                IsConnected = false;
                StatusText = "Disconnected";
            }
            else if (state == "Connecting")
            {
                StatusText = "Connecting...";
            }
        });
    }



    public string Username
    {
        get => _username;
        set { SetProperty(ref _username, value); }
    }

    public PlaylistJob? CurrentPlaylistJob
    {
        get => _currentPlaylistJob;
        set => SetProperty(ref _currentPlaylistJob, value);
    }

    private string _searchQuery = "";
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                // Notify SearchCommand to re-evaluate CanExecute
                (SearchCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    private bool LooksLikeSpotifyUrl(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;

        var q = query.Trim();
        return q.StartsWith("spotify:playlist:", StringComparison.OrdinalIgnoreCase)
            || q.StartsWith("spotify:album:", StringComparison.OrdinalIgnoreCase)
            || q.Contains("open.spotify.com/playlist/", StringComparison.OrdinalIgnoreCase)
            || q.Contains("open.spotify.com/album/", StringComparison.OrdinalIgnoreCase);
    }

    private bool LooksLikeCsvFilePath(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;

        var q = query.Trim();
        
        // Check if it looks like a file path (Windows or Unix style)
        // Windows: C:\path\to\file.csv or \\network\path\file.csv
        // Unix: /path/to/file.csv or ~/path/to/file.csv
        var hasPathCharacters = q.Contains('\\') || q.Contains('/');
        var endsWithCsv = q.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
        
        // Additional check: if it contains path characters and .csv, try to see if file exists
        if (hasPathCharacters && endsWithCsv)
        {
            try
            {
                return System.IO.File.Exists(q);
            }
            catch
            {
                return false;
            }
        }
        
        return false;
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(IsLoginOverlayVisible));
                OnPropertyChanged(nameof(ConnectionStatus));
                // Notify commands that depend on IsConnected
                (LoginCommand as AsyncRelayCommand<string>)?.RaiseCanExecuteChanged();
                (SearchCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    private bool _loginDismissed = false;
    private bool _autoConnectEnabled = false;
    
    /// <summary>
    /// Whether user has enabled automatic connection on app startup.
    /// </summary>
    public bool AutoConnectEnabled
    {
        get => _autoConnectEnabled;
        set { SetProperty(ref _autoConnectEnabled, value); }
    }
    
    /// <summary>
    /// Login overlay is visible when not connected AND user hasn't dismissed it.
    /// </summary>
    public bool IsLoginOverlayVisible => !IsConnected && !_loginDismissed;

    /// <summary>
    /// Connection status string for display (e.g., "Connected", "Disconnected").
    /// Used by the status indicator in MainWindow.
    /// </summary>
    public string ConnectionStatus => _isConnected ? "Connected" : "Disconnected";

    public bool IsSearching
    {
        get => _isSearching;
        set
        {
            if (SetProperty(ref _isSearching, value))
            {
                _logger.LogInformation($"*** IsSearching changed to: {value} ***");
                // Notify commands that depend on IsSearching
                (SearchCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (CancelSearchCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set { SetProperty(ref _statusText, value); }
    }

    public string DownloadPath
    {
        get => _downloadPath;
        set { SetProperty(ref _downloadPath, value); }
    }

    public string SharedFolderPath
    {
        get => _sharedFolderPath;
        set { SetProperty(ref _sharedFolderPath, value); }
    }

    public int MaxConcurrentDownloads
    {
        get => _maxConcurrentDownloads;
        set { SetProperty(ref _maxConcurrentDownloads, value); }
    }

    public string FileNameFormat
    {
        get => _fileNameFormat;
        set { SetProperty(ref _fileNameFormat, value); }
    }

    public string PreferredFormats
    {
        get => _preferredFormats;
        set
        {
            if (SetProperty(ref _preferredFormats, value)) UpdateActiveFiltersSummary();
        }
    }

    public int SelectedTrackCount
    {
        get => _selectedTrackCount;
        set => SetProperty(ref _selectedTrackCount, value);
    }

    private bool _rememberPassword;
    public bool RememberPassword
    {
        get => _rememberPassword;
        set => SetProperty(ref _rememberPassword, value);
    }

    private bool _isFiltersPanelVisible;
    public bool IsFiltersPanelVisible
    {
        get => _isFiltersPanelVisible;
        set => SetProperty(ref _isFiltersPanelVisible, value);
    }

    private string _currentViewMode = "Albums";
    public string CurrentViewMode
    {
        get => _currentViewMode;
        set => SetProperty(ref _currentViewMode, value);
    }

    private SearchQuery? _selectedImport;
    public SearchQuery? SelectedImport
    {
        get => _selectedImport;
        set
        {
            if (SetProperty(ref _selectedImport, value))
            {
                UpdateFilteredTracks();
                UpdateImportDownloadStats();
            }
        }
    }

    public ObservableCollection<Track> FilteredLibraryEntries { get; } = new();
    public ObservableCollection<SearchQuery> UniqueImports { get; } = new();

    private ImportDownloadStats _importDownloadStats = new();
    public ImportDownloadStats ImportDownloadStats
    {
        get => _importDownloadStats;
        set => SetProperty(ref _importDownloadStats, value);
    }

    public ICommand ChangeViewModeCommand { get; }
    public ICommand SelectImportCommand { get; }

    /// <summary>
    /// Input source type selector (Spotify URL, Plain Text, CSV, Auto-Detect)
    /// </summary>
    public List<string> InputSourceTypes { get; } = new() 
    { 
        "Auto-Detect", 
        "Spotify URL", 
        "Plain Text", 
        "CSV File" 
    };

    private string _selectedInputSourceType = "Auto-Detect";
    public string SelectedInputSourceType
    {
        get => _selectedInputSourceType;
        set => SetProperty(ref _selectedInputSourceType, value);
    }

    public string QueryInputPlaceholder => "Search tracks, import Spotify/CSV - Auto-detects format";

    private string? _spotifyClientId;
    public string? SpotifyClientId
    {
        get => _spotifyClientId;
        set => SetProperty(ref _spotifyClientId, value);
    }

    private string? _spotifyClientSecret;
    public string? SpotifyClientSecret
    {
        get => _spotifyClientSecret;
        set => SetProperty(ref _spotifyClientSecret, value);
    }

    private bool _useSpotifyApi;
    public bool UseSpotifyApi
    {
        get => _useSpotifyApi;
        set => SetProperty(ref _useSpotifyApi, value);
    }

    /// <summary>
    /// Search history for recall (last 20 queries)
    /// </summary>
    public ObservableCollection<string> SearchHistory { get; } = new();

    private string? _selectedHistoryItem;
    public string? SelectedHistoryItem
    {
        get => _selectedHistoryItem;
        set
        {
            if (SetProperty(ref _selectedHistoryItem, value) && value != null)
            {
                SearchQuery = value;
                _selectedHistoryItem = null; // Reset for next selection
            }
        }
    }

    private bool _checkForDuplicates;
    public bool CheckForDuplicates
    {
        get => _checkForDuplicates;
        set => SetProperty(ref _checkForDuplicates, value);
    }

    private string _activeFiltersSummary = "No active filters.";
    public string ActiveFiltersSummary
    {
        get => _activeFiltersSummary;
        set => SetProperty(ref _activeFiltersSummary, value);
    }


    private async Task LoginAsync(string? password)
    {
        if (string.IsNullOrEmpty(Username) || string.IsNullOrEmpty(password))
        {
            StatusText = "Username and password required";
            return;
        }

        try
        {
            // Ensure the username from the UI is passed to the config before connecting
            _config.Username = Username;
            _config.RememberPassword = RememberPassword;
            // Note: AutoConnectEnabled is a UI preference, not persisted to config

            // Save password to config if "Remember Me" is checked
            if (RememberPassword)
            {
                // Encrypt the password before saving
                _config.Password = _protectedDataService.Protect(password);
            }
            else
            {
                _config.Password = null; // Clear password if not remembered
            }
            
            StatusText = "Connecting...";
            await _soulseek.ConnectAsync(password);
            IsConnected = true;
            StatusText = "";
            
            // Save config to persist credentials and auto-connect preference
            _configManager.Save(_config);
            
            _logger.LogInformation("Login successful - AutoConnect={AutoConnect}", AutoConnectEnabled);
        }
        catch (Exception ex)
        {
            var message = ex.Message.ToLowerInvariant();
            
            // Show login overlay again on failed login attempt
            _loginDismissed = false;
            OnPropertyChanged(nameof(IsLoginOverlayVisible));
            
            // Provide specific, user-friendly error messages
            if (message.Contains("invalid username or password") || 
                message.Contains("authentication failed") ||
                message.Contains("login failed"))
            {
                StatusText = "❌ Invalid username or password";
            }
            else if (message.Contains("timeout") || message.Contains("timed out"))
            {
                StatusText = "⏱️ Connection timeout - check your network";
            }
            else if (message.Contains("refused") || message.Contains("connection refused"))
            {
                StatusText = "🚫 Connection refused - server may be down";
            }
            else if (message.Contains("network") || message.Contains("unreachable"))
            {
                StatusText = "🌐 Network error - check your internet connection";
            }
            else
            {
                StatusText = $"Login failed: {ex.Message}";
            }
            
            _logger.LogError(ex, "Login failed");
        }
    }

    private async Task SearchAsync()
    {
        _logger.LogInformation("=== SearchAsync called ===");
        _logger.LogInformation("SearchQuery: {Query}", SearchQuery);
        _logger.LogInformation("IsConnected: {IsConnected}", IsConnected);
        _logger.LogInformation("IsSearching: {IsSearching}", IsSearching);
        
        if (string.IsNullOrEmpty(SearchQuery))
        {
            StatusText = "Enter a search query";
            _logger.LogWarning("Search cancelled - empty query");
            return;
        }

        // Add to search history (max 20 items)
        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            if (SearchHistory.Contains(SearchQuery))
            {
                SearchHistory.Remove(SearchQuery);
            }
            SearchHistory.Insert(0, SearchQuery);
            while (SearchHistory.Count > 20)
            {
                SearchHistory.RemoveAt(SearchHistory.Count - 1);
            }
        }

        // CSV file path detection: C:\path\to\file.csv or /path/to/file.csv
        if (LooksLikeCsvFilePath(SearchQuery))
        {
            try
            {
                IsSearching = true;
                StatusText = $"Importing CSV file: {System.IO.Path.GetFileName(SearchQuery)}";
                _logger.LogInformation("Detected CSV file path: {Path}", SearchQuery);
                await ImportCsvAsync(SearchQuery);
            }
            finally
            {
                IsSearching = false;
            }
            return;
        }

        // Spotify URL path: import without requiring Soulseek connection
        if (LooksLikeSpotifyUrl(SearchQuery))
        {
            try
            {
                IsSearching = true;
                StatusText = "Importing from Spotify...";
                await ImportFromSpotifyAsync(SearchQuery);
            }
            finally
            {
                IsSearching = false;
            }
            return;
        }

        if (!IsConnected)
        {
            StatusText = "Not connected to Soulseek";
            _logger.LogWarning("Search cancelled - not connected");
            return;
        }

        // Reset cancellation token for a fresh search run
        _searchCts.Cancel();
        _searchCts.Dispose();
        _searchCts = new CancellationTokenSource();
        var searchToken = _searchCts.Token;

        IsSearching = true;
        StatusText = $"Searching for '{SearchQuery}'...";
        _logger.LogInformation("Search started for: {Query}", SearchQuery);

        try
        {
            var normalizedQuery = _searchQueryNormalizer.RemoveFeatArtists(SearchQuery);
            normalizedQuery = _searchQueryNormalizer.RemoveYoutubeMarkers(normalizedQuery);
            _logger.LogInformation("Normalized query: {Query}", normalizedQuery);

            var formatFilter = PreferredFormats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _logger.LogInformation("Format filter: {Formats}", string.Join(", ", formatFilter));
            _logger.LogInformation("Bitrate filter: Min={Min}, Max={Max}", MinBitrate, MaxBitrate);

            SearchResults.Clear();
            var resultsBuffer = new ConcurrentBag<Track>();
            var allResults = new List<Track>(); // Collect all results for ranking
            
            // Use a timer to batch UI updates - only for album mode or if ranking is disabled
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            var batchUpdateTask = Task.Run(async () =>
            {
                while (await timer.WaitForNextTickAsync(searchToken))
                {
                    if (!resultsBuffer.IsEmpty && IsAlbumSearch)
                    {
                        var batch = new List<Track>();
                        while(resultsBuffer.TryTake(out var track))
                        {
                            batch.Add(track);
                            allResults.Add(track); // Accumulate for ranking
                        }

                        if (batch.Any())
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                foreach (var track in batch)
                                {
                                    SearchResults.Add(track);
                                }
                                StatusText = $"Searching... {SearchResults.Count} found.";
                            });
                        }
                    }
                    else if (!resultsBuffer.IsEmpty)
                    {
                        // For track mode, just accumulate without showing - we'll rank and display at the end
                        while(resultsBuffer.TryTake(out var track))
                        {
                            allResults.Add(track);
                        }
                    }
                }
            }, searchToken);

            var actualCount = await _soulseek.SearchAsync(normalizedQuery, formatFilter, (MinBitrate, MaxBitrate), DownloadMode.Normal, tracks =>
            {
                foreach(var track in tracks)
                {
                    // Collect for final ranking
                    resultsBuffer.Add(track);
                }
            }, searchToken);
            
            // Rank results before displaying
            if (allResults.Count > 0)
            {
                _logger.LogInformation("Ranking {Count} search results", allResults.Count);

                if (IsAlbumSearch)
                {
                    var albums = GroupResultsByAlbum(allResults);
                     Dispatcher.UIThread.Post(() =>
                    {
                        AlbumResults.Clear();
                        foreach (var album in albums)
                        {
                            AlbumResults.Add(album);
                        }
                        StatusText = $"Found {actualCount} tracks (grouped into {AlbumResults.Count} albums)";
                    });
                }
                else 
                {
                    // Create search track from query for ranking
                    var searchTrack = new Track { Title = normalizedQuery };
                    
                    // Create evaluator based on current filter settings
                    var evaluator = new FileConditionEvaluator();
                    if (!string.IsNullOrWhiteSpace(PreferredFormats))
                    {
                        var formats = formatFilter.ToList();
                        if (formats.Count > 0)
                        {
                            evaluator.AddRequired(new FormatCondition { AllowedFormats = formats });
                        }
                    }
                    
                    if (MinBitrate > 0 || MaxBitrate > 0)
                    {
                        evaluator.AddPreferred(new BitrateCondition 
                        { 
                            MinBitrate = MinBitrate > 0 ? MinBitrate : null, 
                            MaxBitrate = MaxBitrate > 0 ? MaxBitrate : null 
                        });
                    }
                    
                    // Rank the results
                    var rankedResults = ResultSorter.OrderResults(allResults, searchTrack, evaluator);
                    
                    // Update UI with ranked results
                    Dispatcher.UIThread.Post(() =>
                    {
                        SearchResults.Clear();
                        foreach (var track in rankedResults)
                        {
                            SearchResults.Add(track);
                        }
                        StatusText = $"Found {actualCount} results (ranked)";
                    });
                }
                
                _logger.LogInformation("Results ranked and displayed");
            }
            else
            {
                StatusText = $"Found {actualCount} results";
            }
            
            _logger.LogInformation("Search completed with {Count} results", actualCount);
            await batchUpdateTask; // Allow any final batch to complete
        }
        catch (OperationCanceledException)
        {
            StatusText = "Search cancelled";
            _logger.LogWarning("Search was cancelled");
        }
        catch (Exception ex)
        {
            StatusText = $"Search failed: {ex.Message}";
            _logger.LogError(ex, "Search failed: {Message}", ex.Message);
        }
        finally
        {
            IsSearching = false;
            _logger.LogInformation("=== SearchAsync completed, IsSearching set to false ===");
        }
    }

    private void CancelSearch()
    {
        if (_searchCts != null && !_searchCts.IsCancellationRequested)
        {
            _logger.LogInformation("CancelSearch called. Requesting cancellation.");
            _searchCts.Cancel();
            StatusText = "Cancelling search...";
        }
        else
        {
            _logger.LogWarning("CancelSearch called, but cancellation was already requested or CTS is null.");
        }
    }

    private void AddToDownloads(IList<object>? selectedItems)
    {
        if (selectedItems == null || !selectedItems.Any())
            return;

        var tracks = selectedItems.Cast<Track>().ToList();
        var tracksToAdd = new List<Track>();
        int skippedCount = 0;

        if (CheckForDuplicates)
        {
            var library = _downloadLogService.GetEntries();
            foreach (var track in tracks)
            {
                // Check if a track with the same filename and user already exists in the library
                if (library.Any(libTrack => libTrack.Filename == track.Filename && libTrack.Username == track.Username))
                {
                    skippedCount++;
                }
                else
                {
                    tracksToAdd.Add(track);
                }
            }
        }
        else
        {
            tracksToAdd.AddRange(tracks);
        }

        if (skippedCount > 0)
        {
            _notificationService.Show("Duplicates Skipped", $"{skippedCount} selected track(s) are already in your library.", NotificationType.Information, TimeSpan.FromSeconds(4));
        }

        if (!tracksToAdd.Any())
        {
            StatusText = $"Skipped {skippedCount} duplicate(s). No new tracks to add.";
            return;
        }

        foreach (var track in tracksToAdd)
        {
             _downloadManager.EnqueueTrack(track);
             // Downloads.Add(job); // Old collection deprecated
        }
        
        StatusText = $"Added {tracksToAdd.Count} item(s) to download queue. Skipped {skippedCount} duplicate(s).";
        _logger.LogInformation("Added {AddedCount} items to download queue, skipped {SkippedCount}", tracksToAdd.Count, skippedCount);
    }

    /// <summary>
    /// Quick download a single track without showing the Downloads view.
    /// </summary>
    private void QuickDownload(Track? track)
    {
        if (track == null)
            return;

        // Check for duplicates if enabled
        if (CheckForDuplicates)
        {
            var library = _downloadLogService.GetEntries();
            if (library.Any(libTrack => libTrack.Filename == track.Filename && libTrack.Username == track.Username))
            {
                _notificationService.Show("Duplicate", "This track is already in your library.", NotificationType.Information, TimeSpan.FromSeconds(3));
                return;
            }
        }

        _downloadManager.EnqueueTrack(track);
        _notificationService.Show("Download Started", $"📥 {track.Artist} - {track.Title}", NotificationType.Success, TimeSpan.FromSeconds(3));
        _logger.LogInformation("Quick download initiated for: {Artist} - {Title}", track.Artist, track.Title);
    }

    private async Task ImportCsvAsync(string? filePath = null)
    {
        if (string.IsNullOrEmpty(filePath))
            return;

        try
        {
            StatusText = "Importing CSV...";
            var queries = await _csvInputSource.ParseAsync(filePath);

            // Show preview page instead of auto-importing
            if (queries.Any())
            {
                _logger.LogInformation("Initializing ImportPreviewViewModel for {Count} CSV tracks", queries.Count);
                var importPreviewVm = CreateImportPreviewViewModel();
                var fileName = System.IO.Path.GetFileNameWithoutExtension(filePath);
                await importPreviewVm.InitializePreviewAsync(
                    fileName ?? "CSV Import",
                    "CSV",
                    queries);

                ImportPreviewViewModel = importPreviewVm;
                OnPropertyChanged(nameof(ImportPreviewViewModel));

                // Show the preview in the search page
                IsImportPreviewVisible = true;
                OnPropertyChanged(nameof(IsImportPreviewVisible));

                StatusText = $"Preview: {queries.Count} tracks loaded from CSV";
            }
            else
            {
                StatusText = "No tracks found in the CSV file";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"CSV import failed: {ex.Message}";
            _logger.LogError(ex, "CSV import failed");
        }
    }

    private async Task ImportFromSpotifyAsync(string? playlistUrlOverride = null)
    {
        string? playlistUrl = playlistUrlOverride ?? _userInputService.GetInput("Enter Spotify Playlist URL", "Import from Spotify");

        if (string.IsNullOrEmpty(playlistUrl)) return;

        try
        {
            StatusText = "Importing from Spotify...";

            List<SearchQuery> queries;
            var useApi = !string.IsNullOrWhiteSpace(SpotifyClientId) && !string.IsNullOrWhiteSpace(SpotifyClientSecret) && !_config.SpotifyUsePublicOnly;
            if (useApi)
            {
                _logger.LogInformation("Using Spotify API for import");
                queries = await _spotifyInputSource.ParseAsync(playlistUrl);
            }
            else
            {
                _logger.LogInformation("Using Spotify public scraping for import");
                queries = await _spotifyScraperInputSource.ParseAsync(playlistUrl);
            }

            if (queries.Any())
            {
                _logger.LogInformation("Initializing ImportPreviewViewModel for {Count} Spotify tracks", queries.Count);
                var importPreviewVm = CreateImportPreviewViewModel();
                await importPreviewVm.InitializePreviewAsync(
                    queries.FirstOrDefault()?.SourceTitle ?? "Spotify Playlist",
                    "Spotify",
                    queries);

                ImportPreviewViewModel = importPreviewVm;
                OnPropertyChanged(nameof(ImportPreviewViewModel));

                // Show the preview in the search page
                IsImportPreviewVisible = true;
                OnPropertyChanged(nameof(IsImportPreviewVisible));

                StatusText = $"Preview: {queries.Count} tracks loaded from Spotify";
            }
            else
            {
                StatusText = "No tracks found in the Spotify playlist";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Spotify import failed: {ex.Message}";
            _logger.LogError(ex, "Spotify import failed");
        }
    }

    /// <summary>
    /// Dev helper to scrape a Spotify playlist/album URL and log a short preview for troubleshooting.
    /// </summary>
    public async Task<List<SearchQuery>> DevFetchSpotifyAsync(string url, int previewCount = 5)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL cannot be empty", nameof(url));

        try
        {
            var queries = await _spotifyScraperInputSource.ParseAsync(url);

            var preview = queries
                .Take(previewCount)
                .Select(q => $"{q.Artist} - {q.Title}")
                .ToList();

            _logger.LogInformation("DevSpotify: extracted {Count} track(s). Preview: {Preview}", queries.Count, string.Join(" | ", preview));
            StatusText = $"DevSpotify: {queries.Count} track(s) parsed (showing {Math.Min(previewCount, queries.Count)} preview).";

            return queries;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DevSpotify scrape failed for {Url}", url);
            StatusText = $"DevSpotify failed: {ex.Message}";
            return new List<SearchQuery>();
        }
    }

    private async Task RunDiagnosticsHarnessAsync()
    {
        if (_isDiagnosticsRunning)
        {
            _logger.LogWarning("Diagnostics harness already running; ignoring new request.");
            return;
        }

        _isDiagnosticsRunning = true;

        var tempFiles = new List<string>();
        var diagnosticTrackIds = new HashSet<string>();
        PlaylistJob? diagnosticsJob = null;

        T InvokeOnUi<T>(Func<T> func)
        {
            if (Dispatcher.UIThread.CheckAccess())
                return func();

            return Dispatcher.UIThread.InvokeAsync(func).Result;
        }

        void InvokeOnUiAction(Action action)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                action();
            }
            else
            {
                Dispatcher.UIThread.InvokeAsync(action).Wait();
            }
        }

        var preservedQueries = InvokeOnUi(() => ImportedQueries.ToList());

        try
        {
            StatusText = "Running diagnostics harness...";
            _logger.LogInformation("Diagnostics harness started.");

            diagnosticsJob = new PlaylistJob
            {
                Id = Guid.NewGuid(),
                SourceTitle = $"Diagnostics Harness {DateTime.UtcNow:HHmmss}",
                SourceType = "Diagnostics",
                DestinationFolder = DownloadPath,
                CreatedAt = DateTime.UtcNow,
                OriginalTracks = new ObservableCollection<Track>(),
                PlaylistTracks = new List<PlaylistTrack>()
            };

            for (int index = 0; index < 3; index++)
            {
                var sampleTrack = new Track
                {
                    Artist = $"Diagnostics Artist {index + 1}",
                    Title = $"Diagnostics Track {index + 1}",
                    Album = "Diagnostics Suite",
                    Length = 180 + (index * 12),
                    SourceTitle = diagnosticsJob.SourceTitle
                };
                diagnosticsJob.OriginalTracks.Add(sampleTrack);

                var playlistTrack = new PlaylistTrack
                {
                    Id = Guid.NewGuid(),
                    PlaylistId = diagnosticsJob.Id,
                    Artist = sampleTrack.Artist ?? string.Empty,
                    Title = sampleTrack.Title ?? string.Empty,
                    Album = sampleTrack.Album ?? string.Empty,
                    TrackUniqueHash = Guid.NewGuid().ToString("N"),
                    Status = TrackStatus.Downloaded,
                    TrackNumber = index + 1,
                    AddedAt = DateTime.UtcNow
                };

                var tempFile = Path.Combine(Path.GetTempPath(), $"qmusic_diag_{playlistTrack.Id:N}.tmp");
                await File.WriteAllTextAsync(tempFile, "diagnostics");
                playlistTrack.ResolvedFilePath = tempFile;

                diagnosticsJob.PlaylistTracks.Add(playlistTrack);
                diagnosticTrackIds.Add(playlistTrack.TrackUniqueHash);
                tempFiles.Add(tempFile);
            }

            diagnosticsJob.RefreshStatusCounts();

            _logger.LogInformation("Diagnostics: queueing synthetic job {JobId} with {Count} tracks.", diagnosticsJob.Id, diagnosticsJob.PlaylistTracks.Count);
            await _downloadManager.QueueProject(diagnosticsJob);

            await Task.Delay(250);

            var persistedJob = await _libraryService.FindPlaylistJobAsync(diagnosticsJob.Id);
            if (persistedJob == null)
            {
                _logger.LogError("Diagnostics: job {JobId} not found after persistence.", diagnosticsJob.Id);
            }
            else
            {
                _logger.LogInformation(
                    "Diagnostics: persisted job '{Title}' => Tracks={TrackCount}, Success={Success}, Missing={Missing}, Failed={Failed}.",
                    persistedJob.SourceTitle,
                    persistedJob.PlaylistTracks.Count,
                    persistedJob.SuccessfulCount,
                    persistedJob.MissingCount,
                    persistedJob.FailedCount);

                foreach (var track in persistedJob.PlaylistTracks.OrderBy(t => t.TrackNumber))
                {
                    _logger.LogInformation(
                        "Diagnostics: Track #{Number}: {Artist} - {Title} [{Status}] ({Hash})",
                        track.TrackNumber,
                        track.Artist,
                        track.Title,
                        track.Status,
                        track.TrackUniqueHash);
                }
            }

            var registeredTracks = InvokeOnUi(() =>
                _downloadManager.AllGlobalTracks
                    .Where(vm => diagnosticTrackIds.Contains(vm.GlobalId))
                    .Select(vm => vm.GlobalId)
                    .ToList());

            _logger.LogInformation("Diagnostics: DownloadManager registered {Count} global tracks for diagnostics job.", registeredTracks.Count);

            if (IsConnected)
            {
                var diagnosticsSourceTitle = diagnosticsJob.SourceTitle;
                InvokeOnUiAction(() =>
                {
                    ImportedQueries.Clear();
                    for (int index = 0; index < 3; index++)
                    {
                        ImportedQueries.Add(new SearchQuery
                        {
                            Artist = $"Diagnostics Artist {index + 1}",
                            Title = $"Diagnostics Track {index + 1}",
                            Album = "Diagnostics Suite",
                            Length = 200,
                            SourceTitle = diagnosticsSourceTitle
                        });
                    }
                });
                RebuildUniqueImports();

                var diagnosticQueryCount = InvokeOnUi(() => ImportedQueries.Count);
                _logger.LogInformation("Diagnostics: starting cancellation smoke test with {Count} queries.", diagnosticQueryCount);

                var searchTask = SearchAllImportedAsync();
                await Task.Delay(TimeSpan.FromSeconds(1));
                CancelSearch();
                await searchTask;

                _logger.LogInformation("Diagnostics: cancellation completed. IsSearching={IsSearching}, Status='{StatusText}'.", IsSearching, StatusText);
            }
            else
            {
                _logger.LogWarning("Diagnostics harness skipped cancellation test because Soulseek is disconnected.");
            }

            // === Step 4: Concurrency Probe for Library Entry Upsert ===
            StatusText = "Diagnostics: Running concurrency probe...";
            _logger.LogInformation("Diagnostics: Running concurrency probe for SaveOrUpdateLibraryEntryAsync.");

            var concurrentEntry = new LibraryEntry
            {
                UniqueHash = "concurrent_probe_hash",
                Artist = "Probe Artist",
                Title = "Probe Title",
                Album = "Probe Album",
                FilePath = "C:\\probe\\path.mp3",
                Bitrate = 320,
                DurationSeconds = 180,
                Format = "mp3"
            };

            // Run 10 concurrent save operations on the same entry
            var tasks = Enumerable.Range(0, 10)
                .Select(_ => _libraryService.SaveOrUpdateLibraryEntryAsync(concurrentEntry))
                .ToList();

            try
            {
                await Task.WhenAll(tasks);
                _logger.LogInformation("Diagnostics: Concurrency probe completed. All upserts finished without PK violation.");
            }
            catch (Exception ex)
            {
                // A primary key violation would throw an exception here, likely from the database provider.
                // If we get here, the test has failed.
                _logger.LogError(ex, "Diagnostics: CONCURRENCY PROBE FAILED! A primary key violation or other error occurred.");
                StatusText = "❌ Diagnostics: Concurrency probe FAILED!";
            }

            StatusText = "Diagnostics harness completed. Review logs for details.";
        }
        catch (Exception ex)
        {
            StatusText = $"Diagnostics harness failed: {ex.Message}";
            _logger.LogError(ex, "Diagnostics harness failed.");
        }
        finally
        {
            InvokeOnUiAction(() =>
            {
                ImportedQueries.Clear();
                foreach (var query in preservedQueries)
                {
                    ImportedQueries.Add(query);
                }
            });
            RebuildUniqueImports();

            if (diagnosticsJob != null)
            {
                try
                {
                    await _libraryService.DeletePlaylistJobAsync(diagnosticsJob.Id);

                    // Clean up the concurrency probe entry.
                    // This requires a method to delete a LibraryEntry, which doesn't exist yet.
                    // For now, we'll log it. A proper implementation would be:
                    // await _libraryService.DeleteLibraryEntryAsync("concurrent_probe_hash");
                    _logger.LogInformation("Diagnostics: Manual cleanup of 'concurrent_probe_hash' from LibraryEntries may be needed.");

                    // To fully clean up, you would need to add a `DeleteLibraryEntryAsync` method to ILibraryService
                    // and implement it in LibraryService/DatabaseService.

                    _logger.LogInformation("Diagnostics: soft-deleted synthetic job {JobId}.", diagnosticsJob.Id);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "Diagnostics: failed to delete synthetic job {JobId}.", diagnosticsJob.Id);
                }

                InvokeOnUiAction(() =>
                {
                    var toRemove = _downloadManager.AllGlobalTracks
                        .Where(vm => diagnosticTrackIds.Contains(vm.GlobalId))
                        .ToList();

                    foreach (var vm in toRemove)
                    {
                        _downloadManager.AllGlobalTracks.Remove(vm);
                    }
                });
            }

            foreach (var path in tempFiles)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception fileEx)
                {
                    _logger.LogWarning(fileEx, "Diagnostics: failed to delete temporary file {Path}.", path);
                }
            }

            _isDiagnosticsRunning = false;
        }
    }

    private async Task SearchAllImportedAsync()
    {
        IsSearching = true;
        _searchCts = new CancellationTokenSource(); // Create a new CTS for this operation
        var searchToken = _searchCts.Token;

        StatusText = $"Orchestrating batch search for {ImportedQueries.Count} imported items...";
        _logger.LogInformation("SearchAllImportedAsync started for {count} queries.", ImportedQueries.Count);
        
        Dispatcher.UIThread.Post(() =>
        {
            SearchResults.Clear();
            OrchestratedQueries.Clear();
        });

        var formatFilter = PreferredFormats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var bestMatchesPerQuery = new ConcurrentBag<Track>(); // Only the best match per query
        var orchestrationProgress = new ConcurrentDictionary<string, OrchestratedQueryProgress>();
        int processedQueries = 0;

        try
        {
            // Initialize progress tracking UI with all queries in "Queued" state
            Dispatcher.UIThread.Post(() =>
            {
                int idx = 0;
                foreach (var query in ImportedQueries)
                {
                    var progress = new OrchestratedQueryProgress(idx.ToString(), query.ToString());
                    OrchestratedQueries.Add(progress);
                    orchestrationProgress.TryAdd(query.ToString(), progress);
                    idx++;
                }
            });

            // Create file condition evaluator with current filter settings
            var evaluator = new FileConditionEvaluator();
            if (formatFilter.Length > 0)
            {
                evaluator.AddRequired(new FormatCondition { AllowedFormats = formatFilter.ToList() });
            }
            if (MinBitrate > 0 || MaxBitrate > 0)
            {
                evaluator.AddPreferred(new BitrateCondition 
                { 
                    MinBitrate = MinBitrate > 0 ? MinBitrate : null, 
                    MaxBitrate = MaxBitrate > 0 ? MaxBitrate : null 
                });
            }

            // Orchestrate: Search each query, rank, and select best match
            await Parallel.ForEachAsync(ImportedQueries, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = searchToken }, async (query, ct) =>
            {
                var queryStr = query.ToString();
                var progress = orchestrationProgress[queryStr];

                try
                {
                    _logger.LogInformation("Orchestrating search for: {Query}", queryStr);
                    
                    // Update UI: Searching state
                    Dispatcher.UIThread.Post(() =>
                    {
                        progress.State = "Searching";
                        progress.IsProcessing = true;
                    });

                    // 1. SEARCH: Collect all results for this query
                    var allResultsForQuery = new List<Track>();
                    var resultCount = await _soulseek.SearchAsync(
                        queryStr, 
                        formatFilter, 
                        (MinBitrate, MaxBitrate), 
                        DownloadMode.Normal, 
                        tracks =>
                        {
                            foreach (var track in tracks)
                            {
                                allResultsForQuery.Add(track);
                            }
                        }, 
                        ct);

                    // Update UI: Ranking state
                    Dispatcher.UIThread.Post(() =>
                    {
                        progress.TotalResults = allResultsForQuery.Count;
                        progress.State = "Ranking";
                    });

                    // 2. RANK & SELECT: Find the single best match
                    if (allResultsForQuery.Count > 0)
                    {
                        _logger.LogInformation("Ranking {Count} results for query: {Query}", allResultsForQuery.Count, queryStr);
                        
                        // Create a search track from the query for ranking comparison
                        var searchTrack = new Track 
                        { 
                            Title = query.Title,
                            Artist = query.Artist,
                            Album = query.Album,
                            Length = query.Length
                        };

                        // Rank all results and get the best one
                        var rankedResults = ResultSorter.OrderResults(allResultsForQuery, searchTrack, evaluator);
                        var bestMatch = rankedResults.FirstOrDefault();

                        if (bestMatch != null)
                        {
                            _logger.LogInformation(
                                "Best match selected: {Artist} - {Title} (bitrate: {Bitrate}, rank: {Rank})",
                                bestMatch.Artist, bestMatch.Title, bestMatch.Bitrate, bestMatch.CurrentRank);
                            
                            bestMatchesPerQuery.Add(bestMatch);

                            // Update UI: Matched state with result info
                            Dispatcher.UIThread.Post(() =>
                            {
                                progress.State = "Matched";
                                progress.MatchedTrack = $"{bestMatch.Artist} - {bestMatch.Title}";
                                progress.MatchScore = bestMatch.CurrentRank;
                                progress.IsProcessing = false;
                                progress.IsComplete = true;
                            });
                        }
                    }
                    else
                    {
                        _logger.LogWarning("No results found for query: {Query}", queryStr);
                        
                        // Update UI: Failed state
                        Dispatcher.UIThread.Post(() =>
                        {
                            progress.State = "Failed";
                            progress.ErrorMessage = "No results found";
                            progress.IsProcessing = false;
                            progress.IsComplete = true;
                        });
                    }

                    var completed = Interlocked.Increment(ref processedQueries);
                    Dispatcher.UIThread.Post(() =>
                    {
                        StatusText = $"Processed {completed}/{ImportedQueries.Count} queries. Found {bestMatchesPerQuery.Count} best matches.";
                    });

                }
                catch (OperationCanceledException)
                {
                    // This is an expected exception when the user cancels the search.
                    // We log it and update the UI for this specific item.
                    // Then we rethrow to signal cancellation to the Parallel.ForEachAsync.
                    Dispatcher.UIThread.Post(() =>
                    {
                        progress.State = "Cancelled";
                        progress.IsProcessing = false;
                        progress.IsComplete = true;
                    });
                    _logger.LogInformation("Orchestration for query was cancelled: {Query}", queryStr);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing query: {Query}", queryStr);
                    
                    // Update UI: Failed state with error
                    Dispatcher.UIThread.Post(() =>
                    {
                        progress.State = "Failed";
                        progress.ErrorMessage = ex.Message;
                        progress.IsProcessing = false;
                        progress.IsComplete = true;
                    });
                }
            });

            StatusText = $"✓ Orchestration complete: {bestMatchesPerQuery.Count} best matches selected from {ImportedQueries.Count} queries.";
            
            // Display ranked results in UI
            Dispatcher.UIThread.Post(() =>
            {
                SearchResults.Clear();
                foreach (var track in bestMatchesPerQuery.OrderByDescending(t => t.CurrentRank))
                {
                    SearchResults.Add(track);
                }
            });

            // 3. CREATE PLAYLIST JOB and QUEUE: Auto-enqueue all selected best matches as a single project
            if (bestMatchesPerQuery.Count > 0)
            {
                // Create a PlaylistJob to group all these tracks together
                var playlistJob = new PlaylistJob
                {
                    Id = Guid.NewGuid(),
                    SourceTitle = "Spotify Playlist",
                    SourceType = "Spotify",
                    CreatedAt = DateTime.UtcNow,
                    PlaylistTracks = new List<PlaylistTrack>()
                };

                // Convert ranked Tracks to PlaylistTracks with PlaylistId linking
                foreach (var track in bestMatchesPerQuery.OrderByDescending(t => t.CurrentRank))
                {
                    var playlistTrack = new PlaylistTrack
                    {
                        Id = Guid.NewGuid(),
                        PlaylistId = playlistJob.Id,
                        Artist = track.Artist ?? "Unknown",
                        Title = track.Title ?? "Unknown",
                        Album = track.Album ?? "Unknown",
                        Status = TrackStatus.Missing,
                        ResolvedFilePath = Path.Combine(
                            _config.DownloadDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                            (new FileNameFormatter()).Format(_config.NameFormat ?? "{artist} - {title}", track) + "." + track.GetExtension()),
                        TrackUniqueHash = track.UniqueHash
                    };
                    playlistJob.PlaylistTracks.Add(playlistTrack);
                }

                // Queue the entire job (this fires ProjectAdded event for Library UI)
                await _downloadManager.QueueProject(playlistJob);
                
                Dispatcher.UIThread.Post(() =>
                {
                    StatusText = $"Created Spotify Playlist job with {bestMatchesPerQuery.Count} tracks. Starting download...";
                    _logger.LogInformation("Orchestration: queued {Count} best matches as PlaylistJob", bestMatchesPerQuery.Count);
                });

                // Auto-start downloads
                await StartDownloadsAsync();
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Batch search cancelled.";
            _logger.LogInformation("SearchAllImportedAsync was cancelled.");
        }
        catch (Exception ex)
        {
            StatusText = $"Batch orchestration failed: {ex.Message}";
            _logger.LogError(ex, "Batch orchestration failed");
        }
        finally
        {
            IsSearching = false;
            _logger.LogInformation("SearchAllImportedAsync finished.");
        }
    }

    private IEnumerable<AlbumResultViewModel> GroupResultsByAlbum(IEnumerable<Track> tracks)
    {
        // Group by User + Directory
        var groups = tracks
            .GroupBy(t => new { t.Username, t.Directory })
            .Where(g => !string.IsNullOrEmpty(g.Key.Directory)) // Must have a directory
            .Select(g => new AlbumResultViewModel(g.ToList(), _downloadManager))
            .OrderByDescending(a => a.HasFreeSlot) // Prioritize free slots
            .ThenByDescending(a => a.TrackCount)   // Then completeness
            .ToList();

        return groups;
    }

    private async Task StartDownloadsAsync()
    {
        if (!Downloads.Any())
        {
            StatusText = "No downloads queued";
            return;
        }

        try
        {
            StatusText = "Starting downloads...";
            await _downloadManager.StartAsync();
            StatusText = "Downloads completed";
        }
        catch (Exception ex)
        {
            StatusText = $"Download error: {ex.Message}";
            _logger.LogError(ex, "Download error");
        }
    }

    private void CancelDownloads()
    {
        foreach(var t in _downloadManager.AllGlobalTracks)
        {
             t.CancelCommand?.Execute(null);
        }
        StatusText = "Downloads cancelled";
    }

    // LEGACY: This method is no longer used. Library management now handled by LibraryViewModel.
    /*
    private void RemoveFromLibrary(IList<object>? selectedItems)
    {
        if (selectedItems == null || !selectedItems.Any()) return;

        var tracksToRemove = selectedItems.Cast<Track>().ToList();
        _downloadLogService.RemoveEntries(tracksToRemove);

        foreach (var track in tracksToRemove)
        {
            LibraryEntries.Remove(track);
        }
        StatusText = $"Removed {tracksToRemove.Count} entries from the library.";
    }
    */

    private async Task RescanLibraryAsync()
    {
        var folder = string.IsNullOrWhiteSpace(DownloadPath) ? _config.DownloadDirectory : DownloadPath;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            _notificationService.Show("Rescan skipped", "Download folder is not set or missing.", NotificationType.Warning, TimeSpan.FromSeconds(4));
            return;
        }

        // TODO: Replace with a proper async confirmation dialog in Avalonia.
        _notificationService.Show("Library Rescan", $"Rescanning '{folder}'...", NotificationType.Information, TimeSpan.FromSeconds(3));

        StatusText = "Rescanning download folder...";

        var (added, removed) = await Task.Run(() => _downloadLogService.SyncWithFolder(folder));
        StatusText = $"Rescan complete: {added} added, {removed} removed.";
        _logger.LogInformation("Rescan complete: Added {Added}, Removed {Removed}", added, removed);
    }

    private void ToggleFiltersPanel()
    {
        IsFiltersPanelVisible = !IsFiltersPanelVisible;
        if (IsFiltersPanelVisible)
        {
            UpdateActiveFiltersSummary();
        }
    }

    private void ClearSearchHistory()
    {
        SearchHistory.Clear();
        StatusText = "Search history cleared.";
    }

    private void UpdateActiveFiltersSummary()
    {
        var filters = new List<string>();
        if (MinBitrate > 0) filters.Add($"Min Bitrate: {MinBitrate}kbps");
        if (MaxBitrate > 0) filters.Add($"Max Bitrate: {MaxBitrate}kbps");
        if (!string.IsNullOrEmpty(PreferredFormats)) filters.Add($"Formats: {PreferredFormats}");

        ActiveFiltersSummary = filters.Any()
            ? "Active Filters: " + string.Join(" | ", filters)
            : "No active filters.";
    }

    private void UpdateConfigFromViewModel()
    {
        _config.Username = Username;
        _config.DownloadDirectory = DownloadPath;
        _config.SharedFolderPath = SharedFolderPath;
        _config.MaxConcurrentDownloads = MaxConcurrentDownloads;
        _config.NameFormat = FileNameFormat;
        _config.PreferredFormats = PreferredFormats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        _config.CheckForDuplicates = CheckForDuplicates;
        _config.RememberPassword = RememberPassword;

        _config.SpotifyClientId = SpotifyClientId;
        _config.SpotifyClientSecret = SpotifyClientSecret;
        _config.SpotifyUsePublicOnly = !UseSpotifyApi;

        _config.PreferredMinBitrate = MinBitrate;
        _config.PreferredMaxBitrate = MaxBitrate;
    }


    private void BrowseDownloadPath()
    {
        // TODO: Implement using Avalonia's StorageProvider API
        _notificationService.Show("Not Implemented", "Folder browsing will be enabled in a future version.", NotificationType.Warning);
    }

    private void BrowseSharedFolder()
    {
        // TODO: Implement using Avalonia's StorageProvider API
        _notificationService.Show("Not Implemented", "Folder browsing will be enabled in a future version.", NotificationType.Warning);
    }

    private readonly SearchQueryNormalizer _searchQueryNormalizer;
    private readonly SpotifyScraperInputSource _spotifyScraperInputSource;
    private readonly SpotifyInputSource _spotifyInputSource;
    private readonly CsvInputSource _csvInputSource;
    private readonly DownloadLogService _downloadLogService;
    private readonly ProtectedDataService _protectedDataService;
    private readonly IUserInputService _userInputService;
    // private readonly INavigationService _navigationService;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Rebuilds the UniqueImports collection from ImportedQueries, grouping by SourceTitle.
    /// Each unique import appears once with a track count.
    /// </summary>
    private void RebuildUniqueImports()
    {
        Dispatcher.UIThread.Post(() =>
        {
            UniqueImports.Clear();
            
            // Group by SourceTitle and get the first item from each group
            var uniquesBySource = ImportedQueries
                .GroupBy(q => q.SourceTitle)
                .Select(g => new { Source = g.Key, Count = g.Count(), FirstItem = g.First() })
                .ToList();
            
            // Add one entry per unique source
            foreach (var group in uniquesBySource)
            {
                // Create a representative query for this import group
                var importItem = group.FirstItem;
                // Update TotalTracks to reflect all tracks in this import
                importItem.TotalTracks = group.Count;
                UniqueImports.Add(importItem);
            }
        });
    }

    /// <summary>
    /// Updates FilteredLibraryEntries based on the selected import.
    /// Shows all imported queries (search entries) from the same source playlist/CSV.
    /// </summary>
    private void UpdateFilteredTracks()
    {
        // LEGACY: Filtering now handled by LibraryViewModel
        /*
        Dispatcher.UIThread.Post(() =>
        {
            FilteredLibraryEntries.Clear();
            
            if (SelectedImport == null)
            {
                // No import selected - show nothing or show all library entries from downloaded files
                foreach (var track in LibraryEntries)
                { 
                    FilteredLibraryEntries.Add(track);
                }
            }
            else
            {
                // Get the source title of the selected import
                var selectedSourceTitle = SelectedImport.SourceTitle;
                
                // Show all imported queries from the same source
                foreach (var query in ImportedQueries.Where(q => q.SourceTitle == selectedSourceTitle))
                {
                    // Convert SearchQuery to Track for display
                    var track = new Track
                    {
                        Artist = query.Artist,
                        Title = query.Title,
                        Album = query.Album,
                        Length = query.Length,
                        SourceTitle = query.SourceTitle
                    };
                    FilteredLibraryEntries.Add(track);
                }
            }
        });
        */
    }

    /// <summary>
    /// Updates download stats for the selected import based on download jobs.
    /// </summary>
    private void UpdateImportDownloadStats()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (SelectedImport == null)
            {
                ImportDownloadStats = new ImportDownloadStats { Total = 0 };
                return;
            }

            var selectedSourceTitle = SelectedImport.SourceTitle;
            var stats = new ImportDownloadStats { Total = SelectedImport.TotalTracks };

            // Count download statuses for tracks matching this import
            foreach (var job in Downloads.Where(j => j.Track?.SourceTitle == selectedSourceTitle))
            {
                switch (job.State)
                {
                    case DownloadState.Completed:
                        stats.Completed++;
                        break;
                    case DownloadState.Downloading:
                    case DownloadState.Searching:
                        stats.InProgress++;
                        break;
                    case DownloadState.Pending:
                        stats.Queued++;
                        break;
                    case DownloadState.Failed:
                    case DownloadState.Cancelled:
                        stats.Failed++;
                        break;
                }
            }

            ImportDownloadStats = stats;
        });
    }

    private bool FilterDownloads(object item)
    {
        if (string.IsNullOrWhiteSpace(DownloadsSearchText)) return true;

        if (item is PlaylistTrackViewModel vm)
        {
            return (vm.Artist?.Contains(DownloadsSearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (vm.Title?.Contains(DownloadsSearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (vm.Album?.Contains(DownloadsSearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (vm.GlobalId?.Contains(DownloadsSearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (vm.ErrorMessage?.Contains(DownloadsSearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   (vm.State.ToString().Contains(DownloadsSearchText, StringComparison.OrdinalIgnoreCase));
        }
        return false;
    }

    /// <summary>
    /// Helper to set backing fields and raise PropertyChanged if the value changed.
    /// </summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private ImportPreviewViewModel CreateImportPreviewViewModel()
    {
        // Use NullLogger for now - the important part is the event wiring
        var importLogger = NullLogger<ImportPreviewViewModel>.Instance;
        var vm = new ImportPreviewViewModel(importLogger, _downloadManager, _navigationService, _libraryService);
        
        // Wire up Cancel event to hide preview and return to search
        vm.Cancelled += (s, e) =>
        {
            _logger.LogInformation("Import preview cancelled by user");
            IsImportPreviewVisible = false;
            OnPropertyChanged(nameof(IsImportPreviewVisible));
        };
        
        // Wire up AddedToLibrary event to process the job
        vm.AddedToLibrary += async (s, job) =>
        {
            _logger.LogInformation("Import preview confirmed, adding {Count} tracks to library", job.OriginalTracks.Count);
            await vm.HandlePlaylistJobAddedAsync(job);
            IsImportPreviewVisible = false;
            OnPropertyChanged(nameof(IsImportPreviewVisible));
        };
        
        return vm;
    }
}
