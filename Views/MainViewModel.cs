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
    private readonly SoulseekAdapter _soulseek;
    private readonly ISoulseekCredentialService _credentialService;
    private readonly INavigationService _navigationService;

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
    
    // ... (StatusText property omitted for brevity, keeping existing) ...

    // ... (UI State properties omitted for brevity, keeping existing) ...

    public MainViewModel(
        ILogger<MainViewModel> logger,
        AppConfig config,
        ConfigManager configManager,
        SoulseekAdapter soulseek,
        ISoulseekCredentialService credentialService,
        INavigationService navigationService,
        PlayerViewModel playerViewModel,
        LibraryViewModel libraryViewModel,
        SearchViewModel searchViewModel,
        ConnectionViewModel connectionViewModel,
        SettingsViewModel settingsViewModel)
    {
        _logger = logger;
        _config = config;
        _configManager = configManager;
        _soulseek = soulseek;
        _credentialService = credentialService;
        _navigationService = navigationService;

        PlayerViewModel = playerViewModel;
        LibraryViewModel = libraryViewModel;
        SearchViewModel = searchViewModel;
        ConnectionViewModel = connectionViewModel;
        SettingsViewModel = settingsViewModel;

        // Initialize commands
        NavigateSearchCommand = new RelayCommand(NavigateToSearch);
        NavigateLibraryCommand = new RelayCommand(NavigateToLibrary);
        NavigateDownloadsCommand = new RelayCommand(NavigateToDownloads);
        NavigateSettingsCommand = new RelayCommand(NavigateToSettings);
        ToggleNavigationCommand = new RelayCommand(() => IsNavigationCollapsed = !IsNavigationCollapsed);
        TogglePlayerCommand = new RelayCommand(() => IsPlayerSidebarVisible = !IsPlayerSidebarVisible);
        TogglePlayerLocationCommand = new RelayCommand(() => IsPlayerAtBottom = !IsPlayerAtBottom);
        ZoomInCommand = new RelayCommand(ZoomIn);
        ZoomOutCommand = new RelayCommand(ZoomOut);
        ResetZoomCommand = new RelayCommand(ResetZoom);
        
        // ... (Rest of constructor) ...
    }

    // ... (Navigate methods) ...

    private void NavigateToSettings()
    {
        if (_settingsPage == null)
        {
            _settingsPage = new Avalonia.SettingsPage { DataContext = SettingsViewModel };
        }
        CurrentPage = _settingsPage;
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

    // Expose download manager for backward compatibility
    public System.Collections.ObjectModel.ObservableCollection<PlaylistTrackViewModel> AllGlobalTracks { get; } = new();





    // Navigation Commands

    public ICommand NavigateSearchCommand { get; }
    public ICommand NavigateLibraryCommand { get; }
    public ICommand NavigateDownloadsCommand { get; }
    public ICommand NavigateSettingsCommand { get; }
    public ICommand ToggleNavigationCommand { get; }
    public ICommand TogglePlayerCommand { get; }
    public ICommand TogglePlayerLocationCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ResetZoomCommand { get; }

    // Page instances (lazy-loaded)
    private object? _searchPage;
    private object? _libraryPage;
    private object? _downloadsPage;
    private object? _settingsPage;

    public MainViewModel(
        ILogger<MainViewModel> logger,
        AppConfig config,
        ConfigManager configManager,
        SoulseekAdapter soulseek,
        ISoulseekCredentialService credentialService,
        INavigationService navigationService,
        PlayerViewModel playerViewModel,
        LibraryViewModel libraryViewModel,
        SearchViewModel searchViewModel,
        ConnectionViewModel connectionViewModel)
    {
        _logger = logger;
        _config = config;
        _configManager = configManager;
        _soulseek = soulseek;
        _credentialService = credentialService;
        _navigationService = navigationService;

        PlayerViewModel = playerViewModel;
        LibraryViewModel = libraryViewModel;
        SearchViewModel = searchViewModel;
        ConnectionViewModel = connectionViewModel;

        // Initialize commands
        NavigateSearchCommand = new RelayCommand(NavigateToSearch);
        NavigateLibraryCommand = new RelayCommand(NavigateToLibrary);
        NavigateDownloadsCommand = new RelayCommand(NavigateToDownloads);
        NavigateSettingsCommand = new RelayCommand(NavigateToSettings);
        ToggleNavigationCommand = new RelayCommand(() => IsNavigationCollapsed = !IsNavigationCollapsed);
        TogglePlayerCommand = new RelayCommand(() => IsPlayerSidebarVisible = !IsPlayerSidebarVisible);
        TogglePlayerLocationCommand = new RelayCommand(() => IsPlayerAtBottom = !IsPlayerAtBottom);
        ZoomInCommand = new RelayCommand(ZoomIn);
        ZoomOutCommand = new RelayCommand(ZoomOut);
        ResetZoomCommand = new RelayCommand(ResetZoom);



        // Set application version
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

        // Register pages for navigation service
        _navigationService.RegisterPage("ImportPreview", typeof(Avalonia.ImportPreviewPage));
        
        // Subscribe to navigation events
        _navigationService.Navigated += OnNavigated;

        // Navigate to Search page by default
        NavigateToSearch();
    }

    private void OnNavigated(object? sender, global::Avalonia.Controls.UserControl page)
    {
        if (page != null)
        {
            CurrentPage = page;
        }
    }

    private void NavigateToSearch()
    {
        if (_searchPage == null)
        {
            _searchPage = new Avalonia.SearchPage { DataContext = SearchViewModel };
        }
        CurrentPage = _searchPage;
    }

    private void NavigateToLibrary()
    {
        if (_libraryPage == null)
        {
            _libraryPage = new Avalonia.LibraryPage { DataContext = LibraryViewModel };
        }
        CurrentPage = _libraryPage;
    }

    private void NavigateToDownloads()
    {
        if (_downloadsPage == null)
        {
            _downloadsPage = new Avalonia.DownloadsPage { DataContext = this };
        }
        CurrentPage = _downloadsPage;
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
}
