namespace SLSKDONET.Configuration;


/// <summary>
/// Application configuration settings.
/// </summary>
public class AppConfig
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool RememberPassword { get; set; }
    public bool AutoConnectEnabled { get; set; }
    public int ListenPort { get; set; } = 49998;
    public bool UseUPnP { get; set; } = false;
    public int ConnectTimeout { get; set; } = 20000; // ms
    public int SearchTimeout { get; set; } = 6000; // ms
    public string? DownloadDirectory { get; set; }
    public string? SharedFolderPath { get; set; }
    public int MaxConcurrentDownloads { get; set; } = 2;
    public string? NameFormat { get; set; } = "{artist} - {title}";
    public bool CheckForDuplicates { get; set; } = true;

    // Soulseek Network Settings (matches Soulseek.NET library defaults)
    public string SoulseekServer { get; set; } = "server.slsknet.org"; 
    public int SoulseekPort { get; set; } = 2242;
    
    // File preference conditions
    public List<string> PreferredFormats { get; set; } = new() { "mp3", "flac" };
    public int PreferredMinBitrate { get; set; } = 128; // kbps (more permissive default)
    public int PreferredMaxBitrate { get; set; } = 2500; // kbps
    public int PreferredMaxSampleRate { get; set; } = 48000; // Hz
    public string? PreferredLengthTolerance { get; set; } = "3"; // seconds
    
    // Spotify integration
    public string? SpotifyClientId { get; set; }
    public string? SpotifyClientSecret { get; set; }
    public bool SpotifyUsePublicOnly { get; set; } = true; // Default to public scraping only
    public bool SpotifyUseApi { get; set; } = false; // Enable Spotify API integration (requires auth)
    
    // Spotify OAuth settings
    public string SpotifyRedirectUri { get; set; } = "http://127.0.0.1:5000/callback";
    public int SpotifyCallbackPort { get; set; } = 5000;
    public bool SpotifyRememberAuth { get; set; } = true; // Store refresh token by default
        public bool ClearSpotifyOnExit { get; set; } = false; // Diagnostic: Clear cached tokens on app close
    
    // Search and download preferences
    public int SearchLengthToleranceSeconds { get; set; } = 3; // Allow +/- 3 seconds duration mismatch
    public bool FuzzyMatchEnabled { get; set; } = true; // Enable fuzzy matching for search results
    public int MaxSearchAttempts { get; set; } = 3; // Max progressive search attempts per track
    public bool AutoRetryFailedDownloads { get; set; } = true;
    public int MaxDownloadRetries { get; set; } = 2;
    public string RankingPreset { get; set; } = "Balanced";
    public ScoringWeights CustomWeights { get; set; } = ScoringWeights.Balanced;
    
    // Window state persistence
    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 900;
    public double WindowX { get; set; } = double.NaN; // NaN means center
    public double WindowY { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; } = false;

    // Library Management
    public List<string> LibraryRootPaths { get; set; } = new(); // Root directories to scan for music files
    public bool EnableFilePathResolution { get; set; } = true; // Enable automatic resolution of moved files
    public double FuzzyMatchThreshold { get; set; } = 0.85; // Minimum similarity score (0.0-1.0) for fuzzy matching

    // Phase 8: Upgrade Scout (Self-Healing Library)
    public bool UpgradeScoutEnabled { get; set; } = false; // Background upgrading
    public int UpgradeMinBitrateThreshold { get; set; } = 320; // Upgrade everything below this
    public int UpgradeMinGainKbps { get; set; } = 128; // Only upgrade if gain is significant
    public bool UpgradeAutoQueueEnabled { get; set; } = false; // Auto-queue vs just notify

    // Phase 8: Dependency Management
    public bool IsFfmpegAvailable { get; set; } = false; // Updated on startup and manual checks
    public string FfmpegVersion { get; set; } = ""; // Detected version (e.g., "6.0.1")

    public override string ToString()
    {
        return $"AppConfig(User={Username}, Port={ListenPort}, Downloads={DownloadDirectory})";
    }
}
