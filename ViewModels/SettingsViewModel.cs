using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Services;
using SLSKDONET.Services.Platform;
using SLSKDONET.Services.Ranking;
using SLSKDONET.Views; // For AsyncRelayCommand

namespace SLSKDONET.ViewModels;

public class SettingsViewModel : INotifyPropertyChanged
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly AppConfig _config;
    private readonly ConfigManager _configManager;
    private readonly IFileInteractionService _fileInteractionService;
    private readonly SpotifyAuthService _spotifyAuthService;
    private readonly ISpotifyMetadataService _spotifyMetadataService;

    // Hardcoded public client ID provided by user/project
    // Ideally this would be in a secured config, but for this desktop app scenario it's acceptable as a default.
    private const string DefaultSpotifyClientId = "67842a599c6f45edbf3de3d84231deb4";

    public event PropertyChangedEventHandler? PropertyChanged;

    // Settings Properties
    public string DownloadPath
    {
        get => _config.DownloadDirectory ?? "";
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

    // Phase 8: Upgrade Scout
    public bool UpgradeScoutEnabled
    {
        get => _config.UpgradeScoutEnabled;
        set { _config.UpgradeScoutEnabled = value; OnPropertyChanged(); }
    }

    public int UpgradeMinBitrateThreshold
    {
        get => _config.UpgradeMinBitrateThreshold;
        set { _config.UpgradeMinBitrateThreshold = value; OnPropertyChanged(); }
    }

    public int UpgradeMinGainKbps
    {
        get => _config.UpgradeMinGainKbps;
        set { _config.UpgradeMinGainKbps = value; OnPropertyChanged(); }
    }

    public bool UpgradeAutoQueueEnabled
    {
        get => _config.UpgradeAutoQueueEnabled;
        set { _config.UpgradeAutoQueueEnabled = value; OnPropertyChanged(); }
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
        get => _config.SpotifyClientId ?? "";
        set { _config.SpotifyClientId = value; OnPropertyChanged(); }
    }
    
    public string SpotifyClientSecret
    {
        get => _config.SpotifyClientSecret ?? "";
        set { _config.SpotifyClientSecret = value; OnPropertyChanged(); }
    }
    
    // Phase 2.4: Ranking Strategy Selection
    public string SelectedRankingMode
    {
        get => _config.RankingPreset;
        set
        {
            if (_config.RankingPreset != value)
            {
                _config.RankingPreset = value;
                
                // Sync weights when preset changes
                if (value == "Balanced") CustomWeights = ScoringWeights.Balanced;
                else if (value == "Quality First") CustomWeights = ScoringWeights.QualityFirst;
                else if (value == "DJ Mode") CustomWeights = ScoringWeights.DjMode;
                
                ApplyRankingStrategy(value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(RankingModeDescription));
            }
        }
    }
    
    public ScoringWeights CustomWeights
    {
        get => _config.CustomWeights ?? ScoringWeights.Balanced;
        set
        {
            _config.CustomWeights = value;
            ResultSorter.SetWeights(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(AvailabilityWeight));
            OnPropertyChanged(nameof(QualityWeight));
            OnPropertyChanged(nameof(MusicalWeight));
            OnPropertyChanged(nameof(MetadataWeight));
            OnPropertyChanged(nameof(StringWeight));
            OnPropertyChanged(nameof(ConditionsWeight));
            UpdateLivePreview();
        }
    }

    public double AvailabilityWeight
    {
        get => CustomWeights.AvailabilityWeight;
        set { CustomWeights.AvailabilityWeight = value; ResultSorter.SetWeights(CustomWeights); UpdateLivePreview(); OnPropertyChanged(); }
    }

    public double QualityWeight
    {
        get => CustomWeights.QualityWeight;
        set { CustomWeights.QualityWeight = value; ResultSorter.SetWeights(CustomWeights); UpdateLivePreview(); OnPropertyChanged(); }
    }

    public double MusicalWeight
    {
        get => CustomWeights.MusicalWeight;
        set { CustomWeights.MusicalWeight = value; ResultSorter.SetWeights(CustomWeights); UpdateLivePreview(); OnPropertyChanged(); }
    }

    public double MetadataWeight
    {
        get => CustomWeights.MetadataWeight;
        set { CustomWeights.MetadataWeight = value; ResultSorter.SetWeights(CustomWeights); UpdateLivePreview(); OnPropertyChanged(); }
    }

    public double StringWeight
    {
        get => CustomWeights.StringWeight;
        set { CustomWeights.StringWeight = value; ResultSorter.SetWeights(CustomWeights); UpdateLivePreview(); OnPropertyChanged(); }
    }

    public double ConditionsWeight
    {
        get => CustomWeights.ConditionsWeight;
        set { CustomWeights.ConditionsWeight = value; ResultSorter.SetWeights(CustomWeights); UpdateLivePreview(); OnPropertyChanged(); }
    }

    private List<SLSKDONET.Models.Track> _livePreviewTracks = new();
    public List<SLSKDONET.Models.Track> LivePreviewTracks
    {
        get => _livePreviewTracks;
        set => SetProperty(ref _livePreviewTracks, value);
    }

    private void UpdateLivePreview()
    {
        if (_livePreviewTracks == null || _livePreviewTracks.Count == 0)
        {
            InitializeLivePreview();
            return;
        }

        var searchTrack = new SLSKDONET.Models.Track 
        { 
            Artist = "Strobe", Title = "Strobe", 
            Length = 600, // 10 minutes
            BPM = 128 
        };
        
        LivePreviewTracks = ResultSorter.OrderResults(_livePreviewTracks, searchTrack);
    }

    private void InitializeLivePreview()
    {
        _livePreviewTracks = new List<SLSKDONET.Models.Track>
        {
            new() { Title = "Strobe (Club Mix)", Artist = "deadmau5", Bitrate = 320, Length = 600, Username = "AudiophileUser", BPM = 128 },
            new() { Title = "Strobe (Radio Edit)", Artist = "deadmau5", Bitrate = 128, Length = 210, Username = "FastDownloader", BPM = 128 },
            new() { Title = "Strobe (Extended Mix)", Artist = "deadmau5", Bitrate = 1011, Length = 620, Username = "LoverOfFLAC", Format = "FLAC", BPM = 128 },
            new() { Title = "Strobe (Remix)", Artist = "OtherArtist", Bitrate = 256, Length = 450, Username = "RemixGuy", BPM = 130 }
        };
        UpdateLivePreview();
    }
    
    public List<string> RankingModes { get; } = new()
    {
        "Balanced",
        "Quality First",
        "DJ Mode"
    };
    
    public string RankingModeDescription
    {
        get
        {
            return SelectedRankingMode switch
            {
                "Quality First" => "Prioritizes bitrate and format quality. BPM/Key are minor tiebreakers.",
                "DJ Mode" => "Prioritizes BPM and Key matching. Quality is secondary.",
                _ => "Equal weight to quality and musical intelligence. Default mode."
            };
        }
    }
    
    private void ApplyRankingStrategy(string mode)
    {
        ISortingStrategy strategy = mode switch
        {
            "Quality First" => new QualityFirstStrategy(),
            "DJ Mode" => new DJModeStrategy(),
            _ => new BalancedStrategy()
        };
        
        ResultSorter.SetStrategy(strategy);
        _logger.LogInformation("Ranking strategy changed to: {Mode}", mode);
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
                // Notify all Spotify commands to re-evaluate their CanExecute state
                (ConnectSpotifyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (DisconnectSpotifyCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand SaveSettingsCommand { get; }
    public ICommand BrowseDownloadPathCommand { get; }
    public ICommand BrowseSharedFolderCommand { get; }
    public ICommand ConnectSpotifyCommand { get; }
    public ICommand DisconnectSpotifyCommand { get; }
    public ICommand ClearSpotifyCacheCommand { get; }
        public ICommand RevokeAndReAuthCommand { get; }
    public ICommand CheckFfmpegCommand { get; } // Phase 8: Dependency validation

    // Phase 8: FFmpeg Dependency State
    private bool _isFfmpegInstalled;
    public bool IsFfmpegInstalled
    {
        get => _isFfmpegInstalled;
        set
        {
            if (SetProperty(ref _isFfmpegInstalled, value))
            {
                OnPropertyChanged(nameof(FfmpegBorderColor));
            }
        }
    }

    private string _ffmpegStatus = "Checking...";
    public string FfmpegStatus
    {
        get => _ffmpegStatus;
        set => SetProperty(ref _ffmpegStatus, value);
    }

    private string _ffmpegVersion = "";
    public string FfmpegVersion
    {
        get => _ffmpegVersion;
        set => SetProperty(ref _ffmpegVersion, value);
    }

    public string FfmpegBorderColor => IsFfmpegInstalled ? "#1DB954" : "#FFA500";

    public SettingsViewModel(
        ILogger<SettingsViewModel> logger,
        AppConfig config,
        ConfigManager configManager,
        IFileInteractionService fileInteractionService,
        SpotifyAuthService spotifyAuthService,
        ISpotifyMetadataService spotifyMetadataService)
    {
        _logger = logger;
        _config = config;
        _configManager = configManager;
        _fileInteractionService = fileInteractionService;
        _spotifyAuthService = spotifyAuthService;
        _spotifyMetadataService = spotifyMetadataService;

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
        DisconnectSpotifyCommand = new AsyncRelayCommand(DisconnectSpotifyAsync, () => !IsAuthenticating);
        ClearSpotifyCacheCommand = new AsyncRelayCommand(ClearSpotifyCacheAsync);
            RevokeAndReAuthCommand = new AsyncRelayCommand(RevokeAndReAuthAsync, () => IsSpotifyConnected && !IsAuthenticating);
        CheckFfmpegCommand = new AsyncRelayCommand(CheckFfmpegAsync); // Phase 8

        // Explicitly initialize IsAuthenticating to false
        IsAuthenticating = false;

        // Note: Initial Spotify verification is already done in App.axaml.cs during startup
        // via SpotifyAuthService.VerifyConnectionAsync() to fix the "zombie token" bug.
        // Just set initial display based on current auth state
        if (_spotifyAuthService.IsAuthenticated)
        {
            IsSpotifyConnected = true;
            SpotifyDisplayName = "Connected";
        }
        else
        {
            IsSpotifyConnected = false;
            SpotifyDisplayName = "Not Connected";
        }

        _ = CheckFfmpegAsync(); // Phase 8: Check FFmpeg on startup
        UpdateLivePreview();
    }

    /// <summary>
    /// Phase 8: Enhanced FFmpeg dependency checker with timeout, stderr capture, and fallback paths.
    /// </summary>
    private async Task CheckFfmpegAsync()
    {
        try
        {
            FfmpegStatus = "Checking...";
            
            // Try standard PATH lookup first
            var (success, version) = await TryFfmpegCommandAsync("ffmpeg");
            
            if (!success)
            {
                // Fallback: Check common install directories (Windows-specific)
                if (OperatingSystem.IsWindows())
                {
                    var commonPaths = new[]
                    {
                        @"C:\ffmpeg\bin\ffmpeg.exe",
                        @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ffmpeg", "bin", "ffmpeg.exe")
                    };
                    
                    foreach (var path in commonPaths)
                    {
                        if (File.Exists(path))
                        {
                            (success, version) = await TryFfmpegCommandAsync(path);
                            if (success)
                            {
                                _logger.LogInformation("FFmpeg found via fallback path: {Path}", path);
                                break;
                            }
                        }
                    }
                }
            }
            
            if (success)
            {
                IsFfmpegInstalled = true;
                FfmpegVersion = version;
                FfmpegStatus = $"✅ Installed (v{version})";
                
                // Update global config
                _config.IsFfmpegAvailable = true;
                _config.FfmpegVersion = version;
                _configManager.Save(_config);
                
                _logger.LogInformation("FFmpeg validation successful: v{Version}", version);
            }
            else
            {
                IsFfmpegInstalled = false;
                FfmpegStatus = "❌ Not Found in PATH";
                
                // Update global config
                _config.IsFfmpegAvailable = false;
                _config.FfmpegVersion = "";
                _configManager.Save(_config);
                
                _logger.LogWarning("FFmpeg not found. Sonic Integrity features will be disabled.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FFmpeg validation failed unexpectedly");
            IsFfmpegInstalled = false;
            FfmpegStatus = "❌ Check Failed";
        }
        
        OnPropertyChanged(nameof(IsFfmpegInstalled));
        OnPropertyChanged(nameof(FfmpegStatus));
        OnPropertyChanged(nameof(FfmpegVersion));
    }

    /// <summary>
    /// Attempts to run ffmpeg -version with timeout and captures stderr (where FFmpeg prints version info).
    /// </summary>
    private async Task<(bool success, string version)> TryFfmpegCommandAsync(string ffmpegPath)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)); // 5-second timeout
        
        try
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true, // FFmpeg writes to stderr!
                    CreateNoWindow = true,
                    UseShellExecute = false
                }
            };
            
            var outputBuilder = new System.Text.StringBuilder();
            process.OutputDataReceived += (s, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
            
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            
            await process.WaitForExitAsync(cts.Token);
            
            if (process.ExitCode == 0)
            {
                var output = outputBuilder.ToString();
                
                // Parse version: "ffmpeg version 6.0.1-full_build-www.gyan.dev" or "ffmpeg version N-109688-g5...github.com/BtbN/FFmpeg-Builds"
                var match = System.Text.RegularExpressions.Regex.Match(output, @"ffmpeg version (\d+(\.\d+)+)");
                var version = match.Success ? match.Groups[1].Value : "unknown";
                
                return (true, version);
            }
            
            return (false, "");
        }
        catch (System.ComponentModel.Win32Exception) // File not found
        {
            return (false, "");
        }
        catch (OperationCanceledException) // Timeout
        {
            _logger.LogWarning("FFmpeg command timed out after 5 seconds at path: {Path}", ffmpegPath);
            return (false, "");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to execute FFmpeg at path: {Path}", ffmpegPath);
            return (false, "");
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
                // Update display based on new auth state
                IsSpotifyConnected = _spotifyAuthService.IsAuthenticated;
                SpotifyDisplayName = IsSpotifyConnected ? "Connected" : "Not Connected";
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

    private async Task ClearSpotifyCacheAsync()
    {
        try
        {
            await _spotifyMetadataService.ClearCacheAsync();
            // Optional: NotificationService usage here if available, for now just log
            _logger.LogInformation("Cache cleared via Settings");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear cache via Settings");
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

    /// <summary>
    /// Diagnostic method: Clears cached credentials and re-authenticates.
    /// Useful for testing if the app has a "poisoned" token cache.
    /// </summary>
    private async Task RevokeAndReAuthAsync()
    {
        try
        {
            IsAuthenticating = true;
            _logger.LogInformation("Revoking cached credentials and re-authenticating...");
            
            // Step 1: Clear cached credentials
            await _spotifyAuthService.ClearCachedCredentialsAsync();
            IsSpotifyConnected = false;
            SpotifyDisplayName = "Not Connected";
            
            _logger.LogInformation("Credentials cleared. Starting fresh authentication...");
            
            // Step 2: Start fresh authentication
            var success = await _spotifyAuthService.StartAuthorizationAsync();
            
            if (success)
            {
                // Update display based on new auth state
                IsSpotifyConnected = _spotifyAuthService.IsAuthenticated;
                SpotifyDisplayName = IsSpotifyConnected ? "Connected" : "Not Connected";
                UseSpotifyApi = true;
                _configManager.Save(_config);
                _logger.LogInformation("✓ Revoke & Re-auth completed successfully");
            }
            else
            {
                _logger.LogWarning("Revoke & Re-auth failed - user cancelled or error occurred");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Revoke & Re-auth failed");
            // TODO: Show error notification to user
        }
        finally
        {
            IsAuthenticating = false;
            (RevokeAndReAuthCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }
}
