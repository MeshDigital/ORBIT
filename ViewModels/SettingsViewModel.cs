using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Services;
using SLSKDONET.Services.Platform;
using SLSKDONET.Views; // For AsyncRelayCommand

namespace SLSKDONET.ViewModels;

public class SettingsViewModel : INotifyPropertyChanged
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly AppConfig _config;
    private readonly ConfigManager _configManager;
    private readonly IFileInteractionService _fileInteractionService;
    private readonly SpotifyAuthService _spotifyAuthService;

    // Hardcoded public client ID provided by user/project
    // Ideally this would be in a secured config, but for this desktop app scenario it's acceptable as a default.
    private const string DefaultSpotifyClientId = "67842a599c6f45edbf3de3d84231deb4";

    public event PropertyChangedEventHandler? PropertyChanged;

    // Settings Properties
    public string DownloadPath
    {
        get => _config.DownloadDirectory;
        set { _config.DownloadDirectory = value; OnPropertyChanged(); }
    }

    public string SharedFolderPath
    {
        get => _config.SharedFolderPath ?? "";
        set { _config.SharedFolderPath = value; OnPropertyChanged(); }
    }

    public int MaxConcurrentDownloads
    {
        get => _config.MaxConcurrentDownloads;
        set { _config.MaxConcurrentDownloads = value; OnPropertyChanged(); }
    }

    public string FileNameFormat
    {
        get => _config.NameFormat ?? "{artist} - {title}";
        set { _config.NameFormat = value; OnPropertyChanged(); }
    }
    
    public bool CheckForDuplicates
    {
        get => _config.CheckForDuplicates;
        set { _config.CheckForDuplicates = value; OnPropertyChanged(); }
    }

    public int MinBitrate
    {
        get => _config.PreferredMinBitrate;
        set { _config.PreferredMinBitrate = value; OnPropertyChanged(); }
    }

    public int MaxBitrate
    {
        get => _config.PreferredMaxBitrate;
        set { _config.PreferredMaxBitrate = value; OnPropertyChanged(); }
    }

    public string PreferredFormats
    {
        get => string.Join(",", _config.PreferredFormats ?? new List<string>());
        set { _config.PreferredFormats = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); OnPropertyChanged(); }
    }

    public bool UseSpotifyApi
    {
        get => _config.SpotifyUseApi;
        set { _config.SpotifyUseApi = value; OnPropertyChanged(); }
    }

    public string SpotifyClientId
    {
        get => _config.SpotifyClientId;
        set { _config.SpotifyClientId = value; OnPropertyChanged(); }
    }
    
    public string SpotifyClientSecret
    {
        get => _config.SpotifyClientSecret;
        set { _config.SpotifyClientSecret = value; OnPropertyChanged(); }
    }

    // SSO State
    private bool _isSpotifyConnected;
    public bool IsSpotifyConnected
    {
        get => _isSpotifyConnected;
        set
        {
            if (SetProperty(ref _isSpotifyConnected, value))
            {
                OnPropertyChanged(nameof(SpotifyStatusColor));
                OnPropertyChanged(nameof(SpotifyStatusIcon));
            }
        }
    }

    public string SpotifyStatusColor => IsSpotifyConnected ? "#1DB954" : "#333333";
    public string SpotifyStatusIcon => IsSpotifyConnected ? "✓" : "🚫";

    private string _spotifyDisplayName = "Not Connected";
    public string SpotifyDisplayName
    {
        get => _spotifyDisplayName;
        set => SetProperty(ref _spotifyDisplayName, value);
    }

    private bool _isAuthenticating;
    public bool IsAuthenticating
    {
        get => _isAuthenticating;
        set
        {
            if (SetProperty(ref _isAuthenticating, value))
            {
                // notify commands if needed, or rely on command manager
                (ConnectSpotifyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand SaveSettingsCommand { get; }
    public ICommand BrowseDownloadPathCommand { get; }
    public ICommand BrowseSharedFolderCommand { get; }
    public ICommand ConnectSpotifyCommand { get; }
    public ICommand DisconnectSpotifyCommand { get; }

    public SettingsViewModel(
        ILogger<SettingsViewModel> logger,
        AppConfig config,
        ConfigManager configManager,
        IFileInteractionService fileInteractionService,
        SpotifyAuthService spotifyAuthService)
    {
        _logger = logger;
        _config = config;
        _configManager = configManager;
        _fileInteractionService = fileInteractionService;
        _spotifyAuthService = spotifyAuthService;

        // Ensure default Client ID is set if empty
        if (string.IsNullOrEmpty(_config.SpotifyClientId))
        {
            _config.SpotifyClientId = DefaultSpotifyClientId;
            // Clear secret if we are setting the public ID, as PKCE doesn't use it
            _config.SpotifyClientSecret = ""; 
        }

        SaveSettingsCommand = new RelayCommand(SaveSettings);
        BrowseDownloadPathCommand = new AsyncRelayCommand(BrowseDownloadPathAsync);
        BrowseSharedFolderCommand = new AsyncRelayCommand(BrowseSharedFolderAsync);
        ConnectSpotifyCommand = new AsyncRelayCommand(ConnectSpotifyAsync, () => !IsAuthenticating);
        DisconnectSpotifyCommand = new AsyncRelayCommand(DisconnectSpotifyAsync);

        // check initial connection status
        _ = CheckSpotifyConnectionStatusAsync();
    }

    private async Task CheckSpotifyConnectionStatusAsync()
    {
        try
        {
            IsAuthenticating = true;
            if (await _spotifyAuthService.IsAuthenticatedAsync())
            {
                var user = await _spotifyAuthService.GetCurrentUserAsync();
                SpotifyDisplayName = user.DisplayName ?? user.Id;
                IsSpotifyConnected = true;
            }
            else
            {
                IsSpotifyConnected = false;
                SpotifyDisplayName = "Not Connected";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check Spotify connection status");
            IsSpotifyConnected = false;
        }
        finally
        {
            IsAuthenticating = false;
        }
    }

    private async Task ConnectSpotifyAsync()
    {
        try
        {
            IsAuthenticating = true;
            
            // Ensure config is saved first so the service uses the correct Client ID
            _configManager.Save(_config);

            var success = await _spotifyAuthService.StartAuthorizationAsync();
            
            if (success)
            {
                await CheckSpotifyConnectionStatusAsync();
                UseSpotifyApi = true; // Auto-enable API usage on success
                _configManager.Save(_config); // Save the enabled state
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spotify connection failed");
            // TODO: Show error notification
        }
        finally
        {
            IsAuthenticating = false;
        }
    }

    private async Task DisconnectSpotifyAsync()
    {
        await _spotifyAuthService.SignOutAsync();
        IsSpotifyConnected = false;
        SpotifyDisplayName = "Not Connected";
        UseSpotifyApi = false; // Optional: Auto-disable? Maybe let user decide.
    }

    private void SaveSettings()
    {
        try
        {
            _configManager.Save(_config);
            // TODO: Show toast notification?
            _logger.LogInformation("Settings saved");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
        }
    }

    private async Task BrowseDownloadPathAsync()
    {
        var path = await _fileInteractionService.OpenFolderDialogAsync("Select Download Folder");
        if (!string.IsNullOrEmpty(path))
        {
            DownloadPath = path;
        }
    }

    private async Task BrowseSharedFolderAsync()
    {
        var path = await _fileInteractionService.OpenFolderDialogAsync("Select Shared Folder");
        if (!string.IsNullOrEmpty(path))
        {
            SharedFolderPath = path;
        }
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
