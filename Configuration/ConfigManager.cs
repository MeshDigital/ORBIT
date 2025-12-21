using Microsoft.Extensions.Configuration;
using System.IO;

namespace SLSKDONET.Configuration;

/// <summary>
/// Manages configuration loading and saving.
/// </summary>
public class ConfigManager
{
    private readonly string _configPath;
    private AppConfig _config = null!;

    public ConfigManager(string? configPath = null)
    {
        _configPath = configPath ?? GetDefaultConfigPath();
    }

    /// <summary>
    /// Gets the default configuration file path.
    /// </summary>
    public static string GetDefaultConfigPath()
    {
        // Optional dev override: look for config.ini in C:\temp first.
        var devPath = "C:/temp/config.ini";
        if (File.Exists(devPath))
        {
            return devPath;
        }

        // Prioritize config.ini in the application's root directory for portability.
        var localPath = Path.Combine(AppContext.BaseDirectory, "config.ini");
        if (File.Exists(localPath))
        {
            return localPath;
        }

        // Fallback to AppData for a more traditional installation.
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var configDir = Path.Combine(appDataPath, "SLSKDONET");
        Directory.CreateDirectory(configDir);
        return Path.Combine(configDir, "config.ini");
    }

    /// <summary>
    /// Loads configuration from file or creates default.
    /// </summary>
    public AppConfig Load()
    {
        if (File.Exists(_configPath))
        {
            var config = new ConfigurationBuilder()
                .AddIniFile(_configPath, optional: true, reloadOnChange: false)
                .Build();

            _config = new AppConfig
            {
                SoulseekServer = config["Soulseek:Server"] ?? "vps.slsknet.org",
                SoulseekPort = int.TryParse(config["Soulseek:Port"], out var sPort) ? sPort : 2242,
                Username = config["Soulseek:Username"] ?? "",
                // Password is no longer stored in config.ini for security
                ListenPort = int.TryParse(config["Soulseek:ListenPort"], out var port) ? port : 49998,
                UseUPnP = bool.TryParse(config["Soulseek:UseUPnP"], out var upnp) && upnp,
                ConnectTimeout = int.TryParse(config["Soulseek:ConnectTimeout"], out var ct) ? ct : 60000,
                SearchTimeout = int.TryParse(config["Soulseek:SearchTimeout"], out var st) ? st : 6000,
                DownloadDirectory = config["Download:Directory"],
                MaxConcurrentDownloads = int.TryParse(config["Download:MaxConcurrent"], out var mcd) ? mcd : 2,
                NameFormat = config["Download:NameFormat"] ?? "{artist} - {title}",
                RememberPassword = bool.TryParse(config["Soulseek:RememberPassword"], out var remember) && remember,
                CheckForDuplicates = !bool.TryParse(config["Download:CheckForDuplicates"], out var check) || check, // Default to true
                SpotifyUsePublicOnly = !bool.TryParse(config["Spotify:SpotifyUsePublicOnly"], out var supo) || supo,
                SpotifyClientId = config["Spotify:SpotifyClientId"],
                SpotifyClientSecret = config["Spotify:SpotifyClientSecret"],
                SpotifyUseApi = bool.TryParse(config["Spotify:SpotifyUseApi"], out var sua) && sua,
                SpotifyRememberAuth = !bool.TryParse(config["Spotify:SpotifyRememberAuth"], out var sra) || sra,
                SpotifyCallbackPort = int.TryParse(config["Spotify:SpotifyCallbackPort"], out var scp) ? scp : 5000,
                SpotifyRedirectUri = config["Spotify:SpotifyRedirectUri"] ?? "http://127.0.0.1:5000/callback",
                                ClearSpotifyOnExit = bool.TryParse(config["Spotify:ClearSpotifyOnExit"], out var csoe) && csoe,
                SearchLengthToleranceSeconds = int.TryParse(config["Download:SearchLengthToleranceSeconds"], out var tol) ? tol : 3,
                FuzzyMatchEnabled = !bool.TryParse(config["Download:FuzzyMatchEnabled"], out var fz) || fz,
                MaxSearchAttempts = int.TryParse(config["Download:MaxSearchAttempts"], out var msa) ? msa : 3,
                AutoRetryFailedDownloads = !bool.TryParse(config["Download:AutoRetryFailedDownloads"], out var arf) || arf,
                MaxDownloadRetries = int.TryParse(config["Download:MaxDownloadRetries"], out var mdr) ? mdr : 2,
                AutoConnectEnabled = bool.TryParse(config["Soulseek:AutoConnectEnabled"], out var ace) && ace,
            };
            
            // Apply defaults if loaded values are empty (for backward compatibility with old configs)
            if (string.IsNullOrEmpty(_config.SoulseekServer)) _config.SoulseekServer = "server.slsknet.org";
        }
        else
        {
            _config = new AppConfig();
        }

        return _config;
    }

    /// <summary>
    /// Saves configuration to file.
    /// </summary>
    public void Save(AppConfig config)
    {
        var directory = Path.GetDirectoryName(_configPath);
        if (directory != null)
            Directory.CreateDirectory(directory);

        var iniContent = new System.Text.StringBuilder();
        iniContent.AppendLine("[Soulseek]");
        iniContent.AppendLine($"Server = {config.SoulseekServer}");
        iniContent.AppendLine($"Port = {config.SoulseekPort}");
        iniContent.AppendLine($"Username = {config.Username}");
        // Password saved securely via SoulseekCredentialService instead
        iniContent.AppendLine($"ListenPort = {config.ListenPort}");
        iniContent.AppendLine($"UseUPnP = {config.UseUPnP}");
        iniContent.AppendLine($"ConnectTimeout = {config.ConnectTimeout}");
        iniContent.AppendLine($"SearchTimeout = {config.SearchTimeout}");

        iniContent.AppendLine($"RememberPassword = {config.RememberPassword}");
        iniContent.AppendLine($"AutoConnectEnabled = {config.AutoConnectEnabled}");

        iniContent.AppendLine();
        iniContent.AppendLine("[Spotify]");
        iniContent.AppendLine($"SpotifyClientId = {config.SpotifyClientId}");
        iniContent.AppendLine($"SpotifyClientSecret = {config.SpotifyClientSecret}");
        iniContent.AppendLine($"SpotifyUsePublicOnly = {config.SpotifyUsePublicOnly}");
        iniContent.AppendLine($"SpotifyCallbackPort = {config.SpotifyCallbackPort}");
        iniContent.AppendLine($"SpotifyRedirectUri = {config.SpotifyRedirectUri}");
        iniContent.AppendLine($"SpotifyRememberAuth = {config.SpotifyRememberAuth}");
        iniContent.AppendLine($"SpotifyUseApi = {config.SpotifyUseApi}");
        iniContent.AppendLine($"ClearSpotifyOnExit = {config.ClearSpotifyOnExit}");

        iniContent.AppendLine();
        iniContent.AppendLine("[Download]");
        iniContent.AppendLine($"Directory = {config.DownloadDirectory}");
        iniContent.AppendLine($"MaxConcurrent = {config.MaxConcurrentDownloads}");
        iniContent.AppendLine($"NameFormat = {config.NameFormat}");
        iniContent.AppendLine($"CheckForDuplicates = {config.CheckForDuplicates}");
        iniContent.AppendLine($"SearchLengthToleranceSeconds = {config.SearchLengthToleranceSeconds}");
        iniContent.AppendLine($"FuzzyMatchEnabled = {config.FuzzyMatchEnabled}");
        iniContent.AppendLine($"MaxSearchAttempts = {config.MaxSearchAttempts}");
        iniContent.AppendLine($"AutoRetryFailedDownloads = {config.AutoRetryFailedDownloads}");
        iniContent.AppendLine($"MaxDownloadRetries = {config.MaxDownloadRetries}");
        
        // This setting belongs in the [Soulseek] section.
        // It was misplaced under [Download].
        // iniContent.AppendLine($"RememberPassword = {config.RememberPassword}");

        File.WriteAllText(_configPath, iniContent.ToString());
        _config = config;
    }

    public AppConfig GetCurrent() => _config;
}
