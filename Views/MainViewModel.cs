using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using Avalonia.Threading;
using System.Collections.Generic; // Added this using directive
using SLSKDONET.Models;
using SpotifyAPI.Web; // For SimplePlaylist

namespace SLSKDONET.Views;

/// <summary>
/// Main window ViewModel - coordinates navigation and global app state.
/// Delegates responsibilities to specialized child ViewModels.
/// </summary>
public class MainViewModel : INotifyPropertyChanged
{
    private readonly ILogger<MainViewModel> _logger;
    private readonly AppConfig _config;
    private readonly ConfigManager _configManager;
    private readonly ISoulseekAdapter _soulseek;
    private readonly ISoulseekCredentialService _credentialService;
    private readonly INavigationService _navigationService;
    private readonly IEventBus _eventBus;
    private readonly DownloadManager _downloadManager;
    private readonly ISpotifyMetadataService _spotifyMetadata;
    private readonly SpotifyAuthService _spotifyAuth;

    // Child ViewModels
    public PlayerViewModel PlayerViewModel { get; }
    public LibraryViewModel LibraryViewModel { get; }
    public SearchViewModel SearchViewModel { get; }
    public ConnectionViewModel ConnectionViewModel { get; }
    public SettingsViewModel SettingsViewModel { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    // Navigation state
    private object? _currentPage;
    public object? CurrentPage
    {
        get => _currentPage;
        set => SetProperty(ref _currentPage, value);
    }
    
    private PageType _currentPageType;
    public PageType CurrentPageType
    {
        get => _currentPageType;
        set => SetProperty(ref _currentPageType, value);
    }
    
    // ... (StatusText property omitted for brevity, keeping existing) ...

    // ... (UI State properties omitted for brevity, keeping existing) ...

    public MainViewModel(
        ILogger<MainViewModel> logger,
        AppConfig config,
        ConfigManager configManager,
        ISoulseekAdapter soulseek,
        ISoulseekCredentialService credentialService,
        INavigationService navigationService,
        PlayerViewModel playerViewModel,
        LibraryViewModel libraryViewModel,
        SearchViewModel searchViewModel,
        ConnectionViewModel connectionViewModel,
        SettingsViewModel settingsViewModel,
        DownloadManager downloadManager,
        ISpotifyMetadataService spotifyMetadata,
        SpotifyAuthService spotifyAuth)
    {
        _logger = logger;
        _config = config;
        _configManager = configManager;
        _soulseek = soulseek;
        _credentialService = credentialService;
        _navigationService = navigationService;
        
        // Assign missing fields
        _eventBus = eventBus;
        _downloadManager = downloadManager;
        _spotifyMetadata = spotifyMetadata;
        _spotifyAuth = spotifyAuth;

        PlayerViewModel = playerViewModel;
        LibraryViewModel = libraryViewModel;
        SearchViewModel = searchViewModel;
        ConnectionViewModel = connectionViewModel;
        SettingsViewModel = settingsViewModel;

        // Initialize commands
        NavigateHomeCommand = new RelayCommand(NavigateToHome); // Phase 6D
        NavigateSearchCommand = new RelayCommand(NavigateToSearch);
        NavigateLibraryCommand = new RelayCommand(NavigateToLibrary);
        NavigateDownloadsCommand = new RelayCommand(NavigateToDownloads);
        NavigateSettingsCommand = new RelayCommand(NavigateToSettings);
        NavigateImportCommand = new RelayCommand(NavigateToImport); // Phase 6D
        ToggleNavigationCommand = new RelayCommand(() => IsNavigationCollapsed = !IsNavigationCollapsed);
        TogglePlayerCommand = new RelayCommand(() => IsPlayerSidebarVisible = !IsPlayerSidebarVisible);
        TogglePlayerLocationCommand = new RelayCommand(() => IsPlayerAtBottom = !IsPlayerAtBottom);
        ZoomInCommand = new RelayCommand(ZoomIn);
        ZoomOutCommand = new RelayCommand(ZoomOut);
        ResetZoomCommand = new RelayCommand(ResetZoom);

        // Spotify Hub Initialization (TODO: Phase 7 - Implement when needed)
        LoadSpotifyPlaylistsCommand = new AsyncRelayCommand(LoadSpotifyPlaylistsAsync);
        // ImportSpotifyPlaylistCommand = new AsyncRelayCommand<SimplePlaylist>(ImportSpotifyPlaylistAsync);
        ImportLikedSongsCommand = new AsyncRelayCommand(ImportLikedSongsAsync);
        
        // Subscribe to EventBus events
        // Subscribe to EventBus events
        _eventBus.GetEvent<TrackUpdatedEvent>().Subscribe(evt => OnTrackUpdated(this, evt.Track));
        _eventBus.GetEvent<SoulseekStateChangedEvent>().Subscribe(evt => HandleStateChange(evt.State));
        _eventBus.GetEvent<TrackAddedEvent>().Subscribe(evt => OnTrackAdded(evt.TrackModel));
        _eventBus.GetEvent<TrackRemovedEvent>().Subscribe(evt => OnTrackRemoved(evt.TrackGlobalId));
        
        // Local collection monitoring for stats
        AllGlobalTracks.CollectionChanged += (s, e) => 
        {
             OnPropertyChanged(nameof(SuccessfulCount));
             OnPropertyChanged(nameof(FailedCount));
             OnPropertyChanged(nameof(TodoCount));
             OnPropertyChanged(nameof(DownloadProgressPercentage));
        };
        
        // Set application version from assembly
        try
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            ApplicationVersion = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get application version");
            ApplicationVersion = "1.0.0";
        }

        // Set LibraryViewModel's MainViewModel reference
        LibraryViewModel.SetMainViewModel(this);

        _logger.LogInformation("MainViewModel initialized");

        // Sync Spotify auth state
        IsSpotifyAuthenticated = _spotifyAuth.IsAuthenticated;
        _spotifyAuth.AuthenticationChanged += (s, e) => {
            IsSpotifyAuthenticated = e;
            if (e) _ = LoadSpotifyPlaylistsAsync();
        };

        // Register pages for navigation service
        _navigationService.RegisterPage("ImportPreview", typeof(Avalonia.ImportPreviewPage));
        
        // Subscribe to navigation events
        _navigationService.Navigated += OnNavigated;

        // Navigate to Search page by default
        NavigateToSearch();

        // Phase 0.3: Brain Command
        ExecuteBrainTestCommand = new AsyncRelayCommand(ExecuteBrainTestAsync);

        // Phase 7: Spotify Silent Refresh
        _ = InitializeSpotifyAsync();
    }

    private async Task InitializeSpotifyAsync()
    {
        try
        {
            if (_config.SpotifyUseApi && await _spotifyAuth.IsAuthenticatedAsync())
            {
                _logger.LogInformation("Spotify silent session refresh successful");
                await LoadSpotifyPlaylistsAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Spotify silent refresh failed");
        }
    }

    private void NavigateToSettings()
    {
        if (_settingsPage == null)
        {
            _settingsPage = new Avalonia.SettingsPage { DataContext = SettingsViewModel };
        }
        CurrentPage = _settingsPage;
        CurrentPageType = PageType.Settings;
        _eventBus.Publish(new NavigationEvent(PageType.Settings));
    }


    // Connection logic moved to ConnectionViewModel
    // StatusText is now delegated/coordinated via ConnectionViewModel binding in UI
    // But MainViewModel might still need a status text for other things? 
    // For now we keep StatusText for "Initializing" status but binding in Main Window should point to ConnectionViewModel for connection status.
    // Simplifying MainViewModel:
    
    private string _statusText = "Ready";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    // UI State
    private bool _isNavigationCollapsed;
    public bool IsNavigationCollapsed
    {
        get => _isNavigationCollapsed;
        set => SetProperty(ref _isNavigationCollapsed, value);
    }

    private bool _isPlayerSidebarVisible = true;
    public bool IsPlayerSidebarVisible
    {
        get => _isPlayerSidebarVisible;
        set => SetProperty(ref _isPlayerSidebarVisible, value);
    }

    private bool _isPlayerAtBottom;
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

    private double _baseFontSize = 14.0;
    public double BaseFontSize
    {
        get => _baseFontSize;
        set
        {
            if (SetProperty(ref _baseFontSize, Math.Clamp(value, 8.0, 24.0)))
            {
                UpdateFontSizeResources();
                OnPropertyChanged(nameof(FontSizeSmall));
                OnPropertyChanged(nameof(FontSizeMedium));
                OnPropertyChanged(nameof(FontSizeLarge));
                OnPropertyChanged(nameof(UIScalePercentage));
            }
        }
    }

    public double FontSizeSmall => BaseFontSize * 0.85;
    public double FontSizeMedium => BaseFontSize;
    public double FontSizeLarge => BaseFontSize * 1.2;
    public string UIScalePercentage => $"{(BaseFontSize / 14.0):P0}";

    private string _applicationVersion = "Unknown";
    public string ApplicationVersion
    {
        get => _applicationVersion;
        set => SetProperty(ref _applicationVersion, value);
    }

    private bool _isInitializing = true;
    public bool IsInitializing
    {
        get => _isInitializing;
        set => SetProperty(ref _isInitializing, value);
    }

    // Phase 7: Spotify Hub Properties
    private bool _isSpotifyAuthenticated;
    public bool IsSpotifyAuthenticated
    {
        get => _isSpotifyAuthenticated;
        set => SetProperty(ref _isSpotifyAuthenticated, value);
    }

    // TODO: Phase 7 - Spotify Hub (Currently disabled due to SimplePlaylist type)
    /*
    private System.Collections.ObjectModel.ObservableCollection<SimplePlaylist> _spotifyPlaylists = new();
    public System.Collections.ObjectModel.ObservableCollection<SimplePlaylist> SpotifyPlaylists
    {
        get => _spotifyPlaylists;
        set => SetProperty(ref _spotifyPlaylists, value);
    }
    */

    private bool _isLoadingPlaylists;
    public bool IsLoadingPlaylists
    {
        get => _isLoadingPlaylists;
        set => SetProperty(ref _isLoadingPlaylists, value);
    }

    // Event-Driven Collection
    public System.Collections.ObjectModel.ObservableCollection<PlaylistTrackViewModel> AllGlobalTracks { get; } = new();

    // Navigation Commands

    public ICommand NavigateHomeCommand { get; } // Phase 6D
    public ICommand NavigateSearchCommand { get; }
    public ICommand NavigateLibraryCommand { get; }
    public ICommand NavigateDownloadsCommand { get; }
    public ICommand NavigateSettingsCommand { get; }
    public ICommand NavigateImportCommand { get; } // Phase 6D
    public ICommand ToggleNavigationCommand { get; }
    public ICommand TogglePlayerCommand { get; }
    public ICommand TogglePlayerLocationCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ResetZoomCommand { get; }
    public ICommand ExecuteBrainTestCommand { get; }
    
    // Spotify Hub Commands
    public ICommand LoadSpotifyPlaylistsCommand { get; }
    // public ICommand ImportSpotifyPlaylistCommand { get; } // TODO: Phase 7
    public ICommand ImportLikedSongsCommand { get; }

    // Page instances (lazy-loaded)
    // Lazy-loaded page instances
    private object? _homePage; // Phase 6D
    private object? _searchPage;
    private object? _libraryPage;
    private object? _downloadsPage;
    private object? _settingsPage;
    private object? _importPage; // Phase 6D

    private void OnNavigated(object? sender, global::Avalonia.Controls.UserControl page)
    {
        if (page != null)
        {
            CurrentPage = page;
        }
    }

    // Navigation Methods (lazy-loading pattern)

    private void NavigateToHome()
    {
        if (_homePage == null)
        {
            _homePage = new Avalonia.HomePage { DataContext = this };
        }
        CurrentPage = _homePage;
        CurrentPageType = PageType.Home;
        _eventBus.Publish(new NavigationEvent(PageType.Home));
    }

    private void NavigateToSearch()
    {
        if (_searchPage == null)
        {
            _searchPage = new Avalonia.SearchPage { DataContext = SearchViewModel };
        }
        CurrentPage = _searchPage;
        CurrentPageType = PageType.Search;
        _eventBus.Publish(new NavigationEvent(PageType.Search));
    }

    private void NavigateToLibrary()
    {
        if (_libraryPage == null)
        {
            _libraryPage = new Avalonia.LibraryPage { DataContext = LibraryViewModel };
        }
        CurrentPage = _libraryPage;
        CurrentPageType = PageType.Library;
        _eventBus.Publish(new NavigationEvent(PageType.Library));
    }

    private void NavigateToDownloads()
    {
        if (_downloadsPage == null)
        {
            _downloadsPage = new Avalonia.DownloadsPage { DataContext = this };
        }
        CurrentPage = _downloadsPage;
        CurrentPageType = PageType.Downloads;
        _eventBus.Publish(new NavigationEvent(PageType.Downloads));
    }

    private void NavigateToImport()
    {
        if (_importPage == null)
        {
            _importPage = new Avalonia.ImportPage { DataContext = this };
        }
        CurrentPage = _importPage;
        CurrentPageType = PageType.Import;
        _eventBus.Publish(new NavigationEvent(PageType.Import));
    }



    private void UpdateFontSizeResources()
    {
        if (global::Avalonia.Application.Current?.Resources != null)
        {
            global::Avalonia.Application.Current.Resources["FontSizeSmall"] = BaseFontSize * 0.85;
            global::Avalonia.Application.Current.Resources["FontSizeMedium"] = BaseFontSize;
            global::Avalonia.Application.Current.Resources["FontSizeLarge"] = BaseFontSize * 1.2;
            global::Avalonia.Application.Current.Resources["FontSizeXLarge"] = BaseFontSize * 1.4;
        }
    }



    private void ZoomIn() => BaseFontSize += 1;
    private void ZoomOut() => BaseFontSize -= 1;
    private void ResetZoom() => BaseFontSize = 14.0;

    // Download Progress Properties (computed from AllGlobalTracks)
    public int SuccessfulCount => AllGlobalTracks.Count(t => t.State == PlaylistTrackState.Completed);
    public int FailedCount => AllGlobalTracks.Count(t => t.State == PlaylistTrackState.Failed);
    public int TodoCount => AllGlobalTracks.Count(t => t.State == PlaylistTrackState.Pending || t.State == PlaylistTrackState.Searching);
    public double DownloadProgressPercentage
    {
        get
        {
            var total = AllGlobalTracks.Count;
            if (total == 0) return 0;
            var completed = AllGlobalTracks.Count(t => t.State == PlaylistTrackState.Completed);
            return (double)completed / total * 100;
        }
    }

    // Event Handlers for Global Status
    private void OnTrackUpdated(object? sender, PlaylistTrackViewModel track)
    {
        // Trigger UI updates for aggregate stats on UI thread
        global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
        {
            OnPropertyChanged(nameof(SuccessfulCount));
            OnPropertyChanged(nameof(FailedCount));
            OnPropertyChanged(nameof(TodoCount));
            OnPropertyChanged(nameof(DownloadProgressPercentage));
        });
    }

    private void HandleStateChange(string state)
    {
        // Update connection status on UI thread
        global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
        {
            if (state.Contains("Login failed", StringComparison.OrdinalIgnoreCase))
            {
                StatusText = "Login failed";
            }
            else if (state.Contains("Connected", StringComparison.OrdinalIgnoreCase))
            {
                StatusText = "Ready";
            }
        });
    }

    private void OnTrackAdded(PlaylistTrack trackModel)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
        {
            var vm = new PlaylistTrackViewModel(trackModel, _eventBus);
            AllGlobalTracks.Add(vm);
        });
    }

    private void OnTrackRemoved(string globalId)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
        {
            var toRemove = System.Linq.Enumerable.FirstOrDefault(AllGlobalTracks, t => t.GlobalId == globalId);
            if (toRemove != null)
            {
                AllGlobalTracks.Remove(toRemove);
            }
        });
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }


    private async Task ExecuteBrainTestAsync()
    {
        try 
        {
            StatusText = "🧠 Running Brain Test...";
            _logger.LogInformation("================ BRAIN TEST STARTED ================");

            // Gauntlet 1: Strobe (Radio Edit) - 3:34 (214000ms)
            // Goal: Should match "Strobe - Radio Edit" or similar, NOT Extended Club Mix (10:37)
            _logger.LogInformation("[Test 1] Strobe (Radio Edit) - Expecting ~3m30s match");
            await _spotifyMetadata.FindTrackAsync("deadmau5", "Strobe", 214000);

            // Gauntlet 2: Strobe (Club Edit) - 10:37 (637000ms)
            // Goal: Should match Extended Version
            _logger.LogInformation("[Test 2] Strobe (Club Edit) - Expecting ~10m match");
            await _spotifyMetadata.FindTrackAsync("deadmau5", "Strobe", 637000);

            // Gauntlet 3: Daft Punk - Around the World (Radio Edit) - 3:11
            _logger.LogInformation("[Test 3] Daft Punk - Around the World - Expecting Exact Match");
            await _spotifyMetadata.FindTrackAsync("Daft Punk", "Around the World", 191000);

            // Gauntlet 4: Cache Hit Verify (Run Test 3 again immediately)
            _logger.LogInformation("[Test 4] Cache Verification (Zero Latency Expected)");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await _spotifyMetadata.FindTrackAsync("Daft Punk", "Around the World", 191000);
            sw.Stop();
            _logger.LogInformation($"Cache Check took {sw.ElapsedMilliseconds}ms");

            StatusText = "🧠 Brain Test Complete - Check Logs";
            _logger.LogInformation("================ BRAIN TEST COMPLETE ================");
        }
        catch (Exception ex)
        {
            StatusText = "🧠 Brain Test Failed";
            _logger.LogError(ex, "Brain Test Verification Failed");
        }
    }

    // TODO: Phase 7 - Spotify Hub
    private async Task LoadSpotifyPlaylistsAsync()
    {
        if (!IsSpotifyAuthenticated) return;
        
        try
        {
            IsLoadingPlaylists = true;
            var client = await _spotifyAuth.GetAuthenticatedClientAsync();
            // var playlists = await client.Playlists.CurrentUsers();
            
            // await Dispatcher.UIThread.InvokeAsync(() => 
            // {
            //     SpotifyPlaylists.Clear();
            //     foreach (var p in playlists.Items ?? new List<SimplePlaylist>())
            //     {
            //         SpotifyPlaylists.Add(p);
            //     }
            // });

            _logger.LogInformation("Spotify playlists feature not yet implemented");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load Spotify playlists");
        }
        finally
        {
            IsLoadingPlaylists = false;
        }
    }

    /*
    private async Task ImportSpotifyPlaylistAsync(SimplePlaylist? playlist)
    {
        if (playlist == null) return;
        
        var orchestrator = Services.GetRequiredService<ImportOrchestrator>();
        var provider = Services.GetRequiredService<SpotifyLikedSongsImportProvider>(); // Need common Spotify provider? 
        // For now using existing provider but it might need to handle playlists too
        
        await orchestrator.StartImportWithPreviewAsync(provider, $"https://open.spotify.com/playlist/{playlist.Id}");
    }
    */

    private async Task ImportLikedSongsAsync()
    {
        var orchestrator = Services.GetRequiredService<ImportOrchestrator>();
        var provider = Services.GetRequiredService<SpotifyLikedSongsImportProvider>();
        
        await orchestrator.StartImportWithPreviewAsync(provider, "liked");
    }
}
