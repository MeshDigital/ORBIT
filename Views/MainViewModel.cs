using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection; // Added for GetRequiredService
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using Avalonia.Threading;
using System.Collections.Generic; // Added this using directive
using SLSKDONET.Models;
using SpotifyAPI.Web; // For SimplePlaylist
using SLSKDONET.Services.ImportProviders; // Added for SpotifyLikedSongsImportProvider

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
    private readonly IFileInteractionService _fileInteractionService;

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
        SpotifyAuthService spotifyAuth,
        IFileInteractionService fileInteractionService,
        IEventBus eventBus)
    {
        _logger = logger;
        _config = config;
        _configManager = configManager;
        _soulseek = soulseek;
        _credentialService = credentialService;
        _navigationService = navigationService;
        _fileInteractionService = fileInteractionService;
        
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
        NavigateUpgradeScoutCommand = new RelayCommand(NavigateUpgradeScout);
        NavigateInspectorCommand = new RelayCommand(NavigateInspector);
        NavigateImportCommand = new RelayCommand(NavigateToImport); // Phase 6D
        ToggleNavigationCommand = new RelayCommand(() => IsNavigationCollapsed = !IsNavigationCollapsed);
        TogglePlayerCommand = new RelayCommand(() => IsPlayerSidebarVisible = !IsPlayerSidebarVisible);
        TogglePlayerLocationCommand = new RelayCommand(() => IsPlayerAtBottom = !IsPlayerAtBottom);
        ZoomInCommand = new RelayCommand(ZoomIn);
        ZoomOutCommand = new RelayCommand(ZoomOut);
        ResetZoomCommand = new RelayCommand(ResetZoom);

        // Spotify Hub Initialization (TODO: Phase 7 - Implement when needed)
        // Downloads Page Commands
        PauseAllDownloadsCommand = new RelayCommand(PauseAllDownloads);
        ResumeAllDownloadsCommand = new RelayCommand(ResumeAllDownloads);
        CancelDownloadsCommand = new RelayCommand(CancelAllowedDownloads);
        // Using generic RelayCommand<PlaylistTrackViewModel> for DeleteTrackCommand
        DeleteTrackCommand = new AsyncRelayCommand<PlaylistTrackViewModel>(DeleteTrackAsync);
        
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
        // Set application version
        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var infoVersion = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion;
            
            // Clean up the version string (e.g. remove commit hash if present)
            if (infoVersion != null && infoVersion.Contains('+'))
            {
                infoVersion = infoVersion.Split('+')[0];
            }

            ApplicationVersion = !string.IsNullOrEmpty(infoVersion) ? infoVersion : "0.1.0-alpha";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get application version");
            ApplicationVersion = "0.1.0-alpha";
        }

        // Set LibraryViewModel's MainViewModel reference
        LibraryViewModel.SetMainViewModel(this);

        _logger.LogInformation("MainViewModel initialized");

        // Sync Spotify auth state
        IsSpotifyAuthenticated = _spotifyAuth.IsAuthenticated;
        _spotifyAuth.AuthenticationChanged += (s, e) => {
            IsSpotifyAuthenticated = e;
        };

        // Register pages for navigation service
        _navigationService.RegisterPage("Home", typeof(Avalonia.HomePage));
        _navigationService.RegisterPage("Search", typeof(Avalonia.SearchPage));
        _navigationService.RegisterPage("Library", typeof(Avalonia.LibraryPage));
        _navigationService.RegisterPage("Downloads", typeof(Avalonia.DownloadsPage));
        _navigationService.RegisterPage("Settings", typeof(Avalonia.SettingsPage));
        _navigationService.RegisterPage("Import", typeof(Avalonia.ImportPage));
        _navigationService.RegisterPage("ImportPreview", typeof(Avalonia.ImportPreviewPage));
        _navigationService.RegisterPage("UpgradeScout", typeof(Avalonia.UpgradeScoutView));
        _navigationService.RegisterPage("Inspector", typeof(Avalonia.InspectorPage));
        
        // Subscribe to navigation events
        _navigationService.Navigated += OnNavigated;

        // Navigate to Home page by default
        NavigateToHome();

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
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Spotify silent refresh failed");
        }
    }

    private void NavigateToSettings()
    {
        _navigationService.NavigateTo("Settings");
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

    // TODO: Phase 7 - Spotify Hub


    // Event-Driven Collection
    public System.Collections.ObjectModel.ObservableCollection<PlaylistTrackViewModel> AllGlobalTracks { get; } = new();
    
    // Filtered Collection for Downloads Page
    private System.Collections.ObjectModel.ObservableCollection<PlaylistTrackViewModel> _filteredGlobalTracks = new();
    public System.Collections.ObjectModel.ObservableCollection<PlaylistTrackViewModel> FilteredGlobalTracks
    {
        get => _filteredGlobalTracks;
        set => SetProperty(ref _filteredGlobalTracks, value);
    }
    
    private string _downloadsSearchText = "";
    public string DownloadsSearchText
    {
        get => _downloadsSearchText;
        set
        {
            if (SetProperty(ref _downloadsSearchText, value))
            {
                 UpdateDownloadsFilter();
            }
        }
    }

    private int _downloadsFilterIndex = 0; 
    public int DownloadsFilterIndex
    {
        get => _downloadsFilterIndex;
        set
        {
            if (SetProperty(ref _downloadsFilterIndex, value))
            {
                UpdateDownloadsFilter();
            }
        }
    }

    // Navigation Commands

    public ICommand NavigateHomeCommand { get; } // Phase 6D
    public ICommand NavigateSearchCommand { get; }
    public ICommand NavigateLibraryCommand { get; }
    public ICommand NavigateDownloadsCommand { get; }
    public ICommand NavigateSettingsCommand { get; }
    public ICommand NavigateUpgradeScoutCommand { get; }
    public ICommand NavigateInspectorCommand { get; }
    public ICommand NavigateImportCommand { get; } // Phase 6D
    public ICommand ToggleNavigationCommand { get; }
    public ICommand TogglePlayerCommand { get; }
    public ICommand TogglePlayerLocationCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ResetZoomCommand { get; }
    public ICommand ExecuteBrainTestCommand { get; }
    
    // Downloads Page Commands
    public ICommand PauseAllDownloadsCommand { get; }
    public ICommand ResumeAllDownloadsCommand { get; }
    public ICommand CancelDownloadsCommand { get; }
    public ICommand DeleteTrackCommand { get; }

    // Page instances (lazy-loaded)
    // Lazy-loaded page instances
    // Page instances no longer needed here as they are managed by NavigationService

    private void OnNavigated(object? sender, global::Avalonia.Controls.UserControl page)
    {
        if (page != null)
        {
            CurrentPage = page;
            
            // Sync CurrentPageType based on the view type to keep UI highlights correct
            CurrentPageType = page.GetType().Name switch
            {
                "HomePage" => PageType.Home,
                "SearchPage" => PageType.Search,
                "LibraryPage" => PageType.Library,
                "DownloadsPage" => PageType.Downloads,
                "SettingsPage" => PageType.Settings,
                "ImportPage" => PageType.Import,
                "ImportPreviewPage" => PageType.Import, // Map preview to Import category
                "UpgradeScoutView" => PageType.UpgradeScout,
                "InspectorPage" => PageType.Inspector,
                _ => CurrentPageType
            };
            
            _logger.LogInformation("Navigation sync: CurrentPage updated to {PageType}", CurrentPageType);

            // Structure Fix B.2: Reset Search State on Navigation
            // If we have navigated away from Search (or just generally navigating), ensure search state is clean
            // unless we are specifically in a search-related flow (like ImportPreview).
            // But user requested "whenever a navigation event occurs".
            // We'll reset if we are NOT on Search page anymore.
            if (CurrentPageType != PageType.Search)
            {
               SearchViewModel.ResetState();
            }
        }
    }

    // Navigation Methods (lazy-loading pattern)

    private void NavigateToHome()
    {
        _navigationService.NavigateTo("Home");
    }

    private void NavigateToSearch()
    {
        _navigationService.NavigateTo("Search");
    }

    private void NavigateToLibrary()
    {
        _navigationService.NavigateTo("Library");
    }

    private void NavigateToDownloads()
    {
        _navigationService.NavigateTo("Downloads");
    }

    private void NavigateUpgradeScout()
    {
        _navigationService.NavigateTo("UpgradeScout");
    }

    private void NavigateInspector()
    {
        _navigationService.NavigateTo("Inspector");
    }

    private void NavigateToImport()
    {
        _navigationService.NavigateTo("Import");
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
            UpdateDownloadsFilter(); // Refresh filter
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
                UpdateDownloadsFilter(); // Refresh filter
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

    private void UpdateDownloadsFilter()
    {
        var search = DownloadsSearchText.Trim();
        var filterIdx = DownloadsFilterIndex;

        IEnumerable<PlaylistTrackViewModel> query = AllGlobalTracks;

        // 1. Apply Search
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(t => 
                (t.Title?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (t.Artist?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        // 2. Apply State Filter
        // 0=All, 1=Downloading, 2=Completed, 3=Failed, 4=Pending
        if (filterIdx > 0)
        {
            query = filterIdx switch
            {
                1 => query.Where(t => t.State == PlaylistTrackState.Downloading),
                2 => query.Where(t => t.State == PlaylistTrackState.Completed),
                3 => query.Where(t => t.State == PlaylistTrackState.Failed),
                4 => query.Where(t => t.State == PlaylistTrackState.Pending || t.State == PlaylistTrackState.Searching || t.State == PlaylistTrackState.Queued),
                _ => query
            };
        }

        // Update ObservableCollection
        // Note: For large lists this is inefficient, but for <1000 downloads it's fine for now.
        // Optimization: Use DynamicData or similar if list grows large.
        FilteredGlobalTracks = new System.Collections.ObjectModel.ObservableCollection<PlaylistTrackViewModel>(query.ToList());
    }

    // Command Implementations
    private void PauseAllDownloads()
    {
        foreach (var track in AllGlobalTracks.Where(t => t.CanPause))
        {
            _downloadManager.PauseTrack(track.GlobalId);
        }
    }

    private void ResumeAllDownloads()
    {
        foreach (var track in AllGlobalTracks.Where(t => t.State == PlaylistTrackState.Paused))
        {
            _downloadManager.ResumeTrack(track.GlobalId);
        }
    }

    private void CancelAllowedDownloads()
    {
        foreach (var track in AllGlobalTracks.Where(t => t.CanCancel))
        {
            _downloadManager.CancelTrack(track.GlobalId);
        }
    }

    private async Task DeleteTrackAsync(PlaylistTrackViewModel? track)
    {
        if (track == null) return;
        await _downloadManager.DeleteTrackFromDiskAndHistoryAsync(track.GlobalId);
    }
}
