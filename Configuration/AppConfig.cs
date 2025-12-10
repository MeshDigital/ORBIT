namespace SLSKDONET.Configuration;


/// <summary>
/// Application configuration settings.
/// </summary>
public class AppConfig
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool RememberPassword { get; set; }
    public int ListenPort { get; set; } = 49998;
    public bool UseUPnP { get; set; } = false;
    public int ConnectTimeout { get; set; } = 20000; // ms
    public int SearchTimeout { get; set; } = 6000; // ms
    public string? DownloadDirectory { get; set; }
    public int MaxConcurrentDownloads { get; set; } = 2;
    public string? NameFormat { get; set; } = "{artist} - {title}";
    public bool CheckForDuplicates { get; set; } = true;
    
    // File preference conditions
    public List<string> PreferredFormats { get; set; } = new() { "mp3", "flac" };
    public int PreferredMinBitrate { get; set; } = 128; // kbps (more permissive default)
    public int PreferredMaxBitrate { get; set; } = 2500; // kbps
    public int PreferredMaxSampleRate { get; set; } = 48000; // Hz
    public string? PreferredLengthTolerance { get; set; } = "3"; // seconds
    public string? SpotifyClientId { get; set; }
    public string? SpotifyClientSecret { get; set; }
    public string? SpotifyAccessToken { get; set; }
    public string? SpotifyRefreshToken { get; set; }
    
    // Spotify source preference
    public bool SpotifyUsePublicOnly { get; set; } = true; // Default to public scraping only, no API keys
    
    // Search and download preferences
    public int SearchLengthToleranceSeconds { get; set; } = 3; // Allow +/- 3 seconds duration mismatch
    public bool FuzzyMatchEnabled { get; set; } = true; // Enable fuzzy matching for search results
    public int MaxSearchAttempts { get; set; } = 3; // Max progressive search attempts per track
    public bool AutoRetryFailedDownloads { get; set; } = true;
    public int MaxDownloadRetries { get; set; } = 2;

    public override string ToString()
    {
        return $"AppConfig(User={Username}, Port={ListenPort}, Downloads={DownloadDirectory})";
    }
}
