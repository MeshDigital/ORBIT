using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Services;
using SLSKDONET.Views; // For AsyncRelayCommand and RelayCommand

using SLSKDONET.Models;

namespace SLSKDONET.ViewModels;

public class ConnectionViewModel : INotifyPropertyChanged
{
    private readonly ILogger<ConnectionViewModel> _logger;
    private readonly AppConfig _config;
    private readonly ConfigManager _configManager;
    private readonly ISoulseekAdapter _soulseek;
    private readonly ISoulseekCredentialService _credentialService;

    // Connection State
    private string _username = "";
    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

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

    private bool _isInitializing = false; // Used for "Connecting..." spinner on button
    public bool IsInitializing
    {
        get => _isInitializing;
        set => SetProperty(ref _isInitializing, value);
    }

    // Login Overlay State
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

    // Commands
    public ICommand LoginCommand { get; }
    public ICommand DismissLoginCommand { get; }
    public ICommand DisconnectCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ConnectionViewModel(
        ILogger<ConnectionViewModel> logger,
        AppConfig config,
        ConfigManager configManager,
        ISoulseekAdapter soulseek,
        ISoulseekCredentialService credentialService,
        IEventBus eventBus)
    {
        _logger = logger;
        _config = config;
        _configManager = configManager;
        _soulseek = soulseek;
        _credentialService = credentialService;

        // Initialize state from config
        Username = _config.Username ?? "";
        RememberPassword = _config.RememberPassword;
        AutoConnectEnabled = _config.AutoConnectEnabled;
        
        // Show login overlay if not auto-connecting or if credentials missing
        IsLoginOverlayVisible = !_config.AutoConnectEnabled || string.IsNullOrEmpty(_config.Username);

        LoginCommand = new AsyncRelayCommand<string>(LoginAsync);
        DismissLoginCommand = new RelayCommand(DismissLogin);
        DisconnectCommand = new RelayCommand(Disconnect);

        // Subscribe to Soulseek state changes
        // Subscribe to Soulseek state changes
        eventBus.GetEvent<SoulseekStateChangedEvent>().Subscribe(evt =>
        {
            try
            {
                if (evt.IsConnected)
                {
                    HandleStateChange("Connected");
                }
                else
                {
                    HandleStateChange(evt.State);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to handle state change event: {State}", evt.State);
            }
        });

        // Initialize Auto-Connect
        if (AutoConnectEnabled)
        {
             // We use Post to ensure this happens after constructor finishes
             Dispatcher.UIThread.Post(async () => await AttemptAutoConnect());
        }
        else if (RememberPassword)
        {
             Dispatcher.UIThread.Post(async () => await LoadUsernameOnly());
        }
    }

    private async Task AttemptAutoConnect()
    {
        var creds = await _credentialService.LoadCredentialsAsync();
        if (!string.IsNullOrEmpty(creds.Password))
        {
            if (!string.IsNullOrEmpty(creds.Username))
                Username = creds.Username;

            await LoginAsync(creds.Password);
        }
        else
        {
            IsLoginOverlayVisible = true;
        }
    }

    private async Task LoadUsernameOnly()
    {
        var creds = await _credentialService.LoadCredentialsAsync();
        if (!string.IsNullOrEmpty(creds.Username))
            Username = creds.Username;
    }

    public async Task LoginAsync(string? password)
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
            
            // Check immediate status in case event is delayed
            if (_soulseek.IsConnected)
            {
                 HandleStateChange("Connected");
            }

            if (_soulseek.IsConnected)
            {
                Dispatcher.UIThread.Post(() => 
                {
                    IsLoginOverlayVisible = false;
                    IsInitializing = false; 
                });
                
                // Persistence
                _configManager.Save(_config);
                
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
                // Likely waiting for event, but if not connected instantly, just hide overlay and let event handle it?
                // Or keep overlay? Better to hide and let status text show "Connecting..." or error.
                // Keeping overlay hidden matching old logic optimization
                 Dispatcher.UIThread.Post(() => IsLoginOverlayVisible = false); 
                 _configManager.Save(_config);
            }

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Login failed");
            Dispatcher.UIThread.Post(() => StatusText = $"Login error: {ex.Message}");
        }
        finally
        {
            Dispatcher.UIThread.Post(() => IsInitializing = false);
        }
    }

    private void DismissLogin()
    {
        IsLoginOverlayVisible = false;
    }

    public void Disconnect()
    {
        _soulseek.Disconnect();
        IsConnected = false;
        StatusText = "Disconnected";
        IsLoginOverlayVisible = true;
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
                    IsLoginOverlayVisible = false;
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
