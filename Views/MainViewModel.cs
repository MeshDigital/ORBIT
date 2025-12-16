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

    public event PropertyChangedEventHandler? PropertyChanged;

    // Navigation state
    private object? _currentPage;
    public object? CurrentPage
    {
        get => _currentPage;
        set => SetProperty(ref _currentPage, value);
    }

    // Connection state
    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set => SetProperty(ref _isConnected, value);
    }

    private string _statusText = "Disconnected";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    private string _username = "";
    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
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

    // Search-related properties (TODO: Move to SearchViewModel)
    private Models.SearchInputMode _currentSearchMode = default;
    public Models.SearchInputMode CurrentSearchMode
    {
        get => _currentSearchMode;
        set => SetProperty(ref _currentSearchMode, value);
    }

    private string _searchQuery = "";
    public string SearchQuery
    {
        get => _searchQuery;
        set => SetProperty(ref _searchQuery, value);
    }

    // Login Overlay Properties
    private bool _isLoginOverlayVisible;
    public bool IsLoginOverlayVisible
    {
        get => _isLoginOverlayVisible;
        set => SetProperty(ref _isLoginOverlayVisible, value);
    }

    private bool _rememberPassword;
    public bool RememberPassword
    {
        get => _rememberPassword;
        set => SetProperty(ref _rememberPassword, value);
    }

    private bool _autoConnectEnabled;
    public bool AutoConnectEnabled
    {
        get => _autoConnectEnabled;
        set => SetProperty(ref _autoConnectEnabled, value);
    }

    // Navigation Commands
    public ICommand LoginCommand { get; }
    public ICommand DismissLoginCommand { get; }
    public ICommand DisconnectCommand { get; }
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
        LibraryViewModel libraryViewModel)
    {
        _logger = logger;
        _config = config;
        _configManager = configManager;
        _soulseek = soulseek;
        _credentialService = credentialService;
        _navigationService = navigationService;

        PlayerViewModel = playerViewModel;
        LibraryViewModel = libraryViewModel;

        // Initialize state from config
        Username = _config.Username ?? "";
        RememberPassword = _config.RememberPassword;
        AutoConnectEnabled = _config.AutoConnectEnabled;
        
        // Show login overlay if not auto-connecting or if credentials missing
        IsLoginOverlayVisible = !_config.AutoConnectEnabled || string.IsNullOrEmpty(_config.Username);

        // Attempt to load password and auto-connect if enabled
        if (AutoConnectEnabled)
        {
            Dispatcher.UIThread.Post(async () => 
            {
                var creds = await _credentialService.LoadCredentialsAsync();
                if (!string.IsNullOrEmpty(creds.Password))
                {
                    // Update username if stored one is different (or empty)
                    if (!string.IsNullOrEmpty(creds.Username))
                        Username = creds.Username;

                    await LoginAsync(creds.Password);
                }
                else
                {
                    IsLoginOverlayVisible = true; // Auto-connect failed due to missing password
                }
            });
        }
        else if (RememberPassword)
        {
             Dispatcher.UIThread.Post(async () => 
            {
                var creds = await _credentialService.LoadCredentialsAsync();
                if (!string.IsNullOrEmpty(creds.Username))
                    Username = creds.Username;
                
                // We don't populate the PasswordBox directly for security, but we have it ready 
                // if the user clicks Connect without typing. 
                // However, PasswordBox usually requires user types or we bind to a property (bad).
                // Better approach: If RememberPassword is true, and they click Login with empty password, we use stored one.
            });
        }

        // Initialize commands
        LoginCommand = new AsyncRelayCommand<string>(LoginAsync);
        DismissLoginCommand = new RelayCommand(DismissLogin);
        DisconnectCommand = new RelayCommand(Disconnect);
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

        // Subscribe to Soulseek state changes
        _soulseek.EventBus.Subscribe(evt =>
        {
            try
            {
                if (evt.eventType == "state_changed")
                {
                    dynamic data = evt.data;
                    string state = data.state;
                    HandleStateChange(state);
                }
                else if (evt.eventType == "connection_status")
                {
                    dynamic data = evt.data;
                    string status = data.status;
                    // Map 'connected' to PascalCase 'Connected' to match state enum string
                    if (status.Equals("connected", StringComparison.OrdinalIgnoreCase))
                    {
                        HandleStateChange("Connected");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to handle internal event: {EventType}", evt.eventType);
            }
        });

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

        // Navigate to Search page by default
        NavigateToSearch();
    }

    private void NavigateToSearch()
    {
        if (_searchPage == null)
        {
            _searchPage = new Avalonia.SearchPage { DataContext = this };
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

    private void NavigateToSettings()
    {
        if (_settingsPage == null)
        {
            _settingsPage = new Avalonia.SettingsPage { DataContext = this };
        }
        CurrentPage = _settingsPage;
    }

    private void HandleStateChange(string state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (state)
            {
                case "Connected":
                    IsConnected = true;
                    StatusText = $"Connected as {Username}";
                    IsLoginOverlayVisible = false; // Ensure overlay is hidden on connection
                    break;
                case "Disconnected":
                    IsConnected = false;
                    StatusText = "Disconnected";
                    break;
                default:
                    StatusText = state;
                    break;
            }
        });
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

    private async Task LoginAsync(string? password)
    {
        if (string.IsNullOrWhiteSpace(Username))
        {
            StatusText = "Please enter a username";
            return;
        }

        // If password provided in UI, use it. Otherwise try to use stored password if remembering is enabled.
        string? passwordToUse = password;

        if (string.IsNullOrEmpty(passwordToUse) && RememberPassword)
        {
             var creds = await _credentialService.LoadCredentialsAsync();
             if (!string.IsNullOrEmpty(creds.Password))
             {
                 passwordToUse = creds.Password;
             }
        }

        if (string.IsNullOrEmpty(passwordToUse))
        {
             StatusText = "Please enter a password";
             return;
        }

        IsInitializing = true;
        StatusText = "Connecting...";

        try
        {
            // Update config
            _config.Username = Username;
            _config.RememberPassword = RememberPassword;
            _config.AutoConnectEnabled = AutoConnectEnabled;

            await _soulseek.ConnectAsync(passwordToUse);
            // Internal event bus will trigger "Connected" state change which sets IsConnected = true

            // FORCE update state if connected, in case event was missed or race condition occurred
            if (_soulseek.IsConnected)
            {
                 HandleStateChange("Connected");
            }

            if (_soulseek.IsConnected)
            {
                IsLoginOverlayVisible = false;
                
                // Saving config
                _configManager.Save(_config); // Save updated preferences
                
                // Save credentials securely if RememberPassword is checked
                if (RememberPassword)
                {
                    await _credentialService.SaveCredentialsAsync(Username, passwordToUse);
                }
                else
                {
                    await _credentialService.DeleteCredentialsAsync();
                }
            }
            else
            {
                // Note: ConnectAsync might not return false but throw exception, or connection happens async.
                // Assuming success if no exception for now, but checking IsConnected property to be safe.
                 if (!_soulseek.IsConnected) 
                 {
                      // If still not connected (and no exception), maybe it's in progress?
                      // But ConnectAsync is awaited.
                      // Let's rely on exception handling for failures.
                 }
                IsLoginOverlayVisible = false; // Hide overlay on success
                _configManager.Save(_config);
            }

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login failed");
            StatusText = $"Login error: {ex.Message}";
        }
        finally
        {
            IsInitializing = false;
        }
    }

    private void DismissLogin()
    {
        IsLoginOverlayVisible = false;
    }

    private void Disconnect()
    {
        _soulseek.Disconnect();
        IsConnected = false;
        StatusText = "Disconnected";
        IsLoginOverlayVisible = true;
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
