using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Utils;
using SpotifyAPI.Web;

namespace SLSKDONET.Services;

/// <summary>
/// Manages Spotify OAuth authentication using PKCE flow.
/// Handles authorization, token exchange, refresh, and secure storage.
/// </summary>
public class SpotifyAuthService
{
    private readonly ILogger<SpotifyAuthService> _logger;
    private readonly AppConfig _config;
    private readonly LocalHttpServer _httpServer;
    private readonly ISecureTokenStorage _tokenStorage;

    private string? _currentCodeVerifier;
    private SpotifyClient? _authenticatedClient;
    private PKCETokenResponse? _currentTokenResponse;
    private DateTime _tokenExpiresAt;
    private DateTime _lastTokenRefreshTime = DateTime.MinValue; // Track last refresh for throttling
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private Task? _refreshTask;

    public event EventHandler<bool>? AuthenticationChanged;

    private bool _isAuthenticated;
    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        private set
        {
            if (_isAuthenticated != value)
            {
                _isAuthenticated = value;
                AuthenticationChanged?.Invoke(this, value);
            }
        }
    }

    public SpotifyAuthService(
        ILogger<SpotifyAuthService> logger,
        AppConfig config,
        LocalHttpServer httpServer,
        ISecureTokenStorage tokenStorage)
    {
        _logger = logger;
        _config = config;
        _httpServer = httpServer;
        _tokenStorage = tokenStorage;
        
        // Initial check is now handled by consumers (e.g. SettingsViewModel) explicitly
        // to avoid race conditions with file storage during startup.
    }

    /// <summary>
    /// Proactively verifies Spotify connection on app startup.
    /// This method VALIDATES the stored token by making an actual API call,
    /// rather than just checking if a token file exists.
    /// 
    /// This is the fix for the "zombie token" bug where the app would show
    /// "Connected" after restart but the token was actually invalid.
    /// </summary>
    public async Task VerifyConnectionAsync()
    {
        try
        {
            _logger.LogInformation("Starting proactive Spotify connection verification...");

            // Load token from secure storage
            var refreshToken = await _tokenStorage.LoadRefreshTokenAsync();
            if (string.IsNullOrEmpty(refreshToken))
            {
                _logger.LogInformation("No stored refresh token found - user is not authenticated");
                IsAuthenticated = false;
                return;
            }

            _logger.LogInformation("Stored refresh token found, attempting to validate...");

            // Try to refresh the token to verify it's still valid with Spotify
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var verifyTask = Task.Run(async () =>
            {
                try
                {
                    await RefreshAccessTokenAsync();
                    // If refresh succeeded, we're authenticated
                    IsAuthenticated = _authenticatedClient != null;
                    
                    if (IsAuthenticated)
                    {
                        _logger.LogInformation("✓ Token verification succeeded - user is authenticated");
                    }
                    else
                    {
                        _logger.LogWarning("✗ Token verification failed - RefreshAccessTokenAsync did not create client");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "✗ Token verification failed during refresh - clearing invalid token");
                    IsAuthenticated = false;
                    // Don't re-throw; we want to degrade gracefully
                }
            }, cts.Token);

            await verifyTask;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Token verification timed out after 5 seconds - assuming not authenticated");
            IsAuthenticated = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during Spotify connection verification");
            IsAuthenticated = false;
        }
    }

    /// <summary>
    /// Checks if the user is currently authenticated with Spotify.
    /// Does not trigger a refresh if the current token is still valid.
    /// </summary>
    public async Task<bool> IsAuthenticatedAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var checkTask = Task.Run(async () =>
            {
                // 1. Check if we have a valid client and non-expired token
                if (_authenticatedClient != null && DateTime.UtcNow < _tokenExpiresAt.AddMinutes(-1))
                {
                    return true;
                }

                // 2. Check if we have a stored refresh token
                var refreshToken = await _tokenStorage.LoadRefreshTokenAsync();
                if (string.IsNullOrEmpty(refreshToken))
                    return false;

                // 3. Try to refresh the token to verify it's still valid
                try
                {
                    await RefreshAccessTokenAsync();
                    return _authenticatedClient != null;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "IsAuthenticatedAsync check failed during refresh");
                    return false;
                }
            }, cts.Token);

            return await checkTask;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("IsAuthenticatedAsync timed out after 3 seconds");
            return false;
        }
    }

    /// <summary>
    /// Starts the OAuth authorization flow.
    /// Opens the browser for user consent and waits for the callback.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if authorization succeeded, false otherwise</returns>
    public async Task<bool> StartAuthorizationAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_config.SpotifyClientId))
        {
            _logger.LogError("Spotify Client ID is not configured");
            throw new InvalidOperationException("Spotify Client ID must be configured in Settings");
        }

        try
        {
            // Generate PKCE parameters
            _currentCodeVerifier = PKCEHelper.GenerateCodeVerifier();
            var codeChallenge = PKCEHelper.GenerateCodeChallenge(_currentCodeVerifier);

            _logger.LogInformation("Generated PKCE code verifier and challenge");

            // Build authorization URL
            var scopes = new[]
            {
                Scopes.UserReadPrivate,
                Scopes.UserReadEmail,
                Scopes.PlaylistReadPrivate,
                Scopes.PlaylistReadCollaborative,
                Scopes.UserLibraryRead,
                Scopes.UserFollowRead
            };

            var loginRequest = new LoginRequest(
                new Uri(_config.SpotifyRedirectUri),
                _config.SpotifyClientId,
                LoginRequest.ResponseType.Code)
            {
                CodeChallengeMethod = "S256",
                CodeChallenge = codeChallenge,
                Scope = scopes
            };

            var authUrl = loginRequest.ToUri();
            _logger.LogInformation("Opening browser for Spotify authorization: {Url}", authUrl);

            // Open browser
            OpenBrowser(authUrl.ToString());

            // Wait for callback with a 2-minute timeout
            string? authCode = null;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));

            try 
            {
                authCode = await _httpServer.WaitForCallbackAsync(_config.SpotifyRedirectUri, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Spotify authorization timed out after 2 minutes");
                throw new TimeoutException("Spotify authorization timed out. Please try again.");
            }
            catch (InvalidOperationException)
            {
                // Port conflict or listener failure
                throw; 
            }

            if (string.IsNullOrEmpty(authCode))
            {
                _logger.LogWarning("Authorization failed or was cancelled");
                return false;
            }

            // Exchange code for tokens
            await ExchangeCodeForTokensAsync(authCode);

            _logger.LogInformation("Successfully completed Spotify OAuth flow");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Spotify authorization");
            throw;
        }
    }

    /// <summary>
    /// Exchanges the authorization code for access and refresh tokens.
    /// </summary>
    private async Task ExchangeCodeForTokensAsync(string authCode)
    {
        if (string.IsNullOrEmpty(_currentCodeVerifier))
            throw new InvalidOperationException("Code verifier not set. Call StartAuthorizationAsync first.");

        if (string.IsNullOrEmpty(_config.SpotifyClientId))
            throw new InvalidOperationException("Spotify Client ID is not configured.");

        var tokenRequest = new PKCETokenRequest(_config.SpotifyClientId, authCode, new Uri(_config.SpotifyRedirectUri), _currentCodeVerifier);

        var config = SpotifyClientConfig.CreateDefault();
        var oauthClient = new OAuthClient(config);

        // Add 30-second timeout to token exchange
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var tokenResponse = await oauthClient.RequestToken(tokenRequest).WaitAsync(cts.Token);

        _currentTokenResponse = tokenResponse;
        _tokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn);

        // Store refresh token securely
        if (_config.SpotifyRememberAuth && !string.IsNullOrEmpty(tokenResponse.RefreshToken))
        {
            await _tokenStorage.SaveRefreshTokenAsync(tokenResponse.RefreshToken);
            _logger.LogInformation("Refresh token stored securely");
        }

        // Save the successful callback port to config for deterministic RedirectUri
        // This ensures future auth attempts use the same port that worked
        _config.SpotifyCallbackPort = new Uri(_config.SpotifyRedirectUri).Port;
        _logger.LogInformation("Saved Spotify callback port {Port} for future auth attempts", _config.SpotifyCallbackPort);

        // Create authenticated client
        _authenticatedClient = new SpotifyClient(tokenResponse.AccessToken);
        IsAuthenticated = true;

        _logger.LogInformation("Successfully exchanged authorization code for tokens");
    }

    /// <summary>
    /// Refreshes the access token using the stored refresh token.
    /// Thread-safe: concurrent calls will wait for the same refresh operation.
    /// </summary>
    public async Task RefreshAccessTokenAsync()
    {
        Task? taskToWait = null;

        await _authLock.WaitAsync();
        try
        {
            // If a refresh is already in progress, capture the task and wait for it outside the lock
            if (_refreshTask != null)
            {
                taskToWait = _refreshTask;
            }
            else
            {
                // Start a new refresh task
                _refreshTask = RefreshAccessTokenInternalAsync();
                taskToWait = _refreshTask;
            }
        }
        finally
        {
            _authLock.Release();
        }

        // Wait for the task outside the lock to allow other callers to check status
        if (taskToWait != null)
        {
            try 
            {
                await taskToWait;
            }
            finally 
            {
                // Only the first caller clears the task, but we use another lock or just check
                await _authLock.WaitAsync();
                if (_refreshTask == taskToWait)
                {
                    _refreshTask = null;
                }
                _authLock.Release();
            }
        }
    }

    private async Task RefreshAccessTokenInternalAsync()
    {
        // Throttle: Don't refresh if we just refreshed less than 5 minutes ago
        // This prevents "double refresh" glitch when both InitializeAsync and VerifyConnectionAsync trigger refreshes
        const int throttleMinutes = 5;
        var timeSinceLastRefresh = DateTime.UtcNow - _lastTokenRefreshTime;
        if (timeSinceLastRefresh.TotalMinutes < throttleMinutes && _currentTokenResponse != null)
        {
            _logger.LogInformation("Token refresh throttled: last refresh was {Seconds} seconds ago", timeSinceLastRefresh.TotalSeconds);
            return;
        }

        var refreshToken = await _tokenStorage.LoadRefreshTokenAsync();
        if (string.IsNullOrEmpty(refreshToken))
        {
            _logger.LogWarning("No refresh token available");
            IsAuthenticated = false;
            throw new InvalidOperationException("No refresh token available. Please sign in again.");
        }

        try
        {
            if (string.IsNullOrEmpty(_config.SpotifyClientId))
                throw new InvalidOperationException("Spotify Client ID is not configured.");

            var tokenRequest = new PKCETokenRefreshRequest(_config.SpotifyClientId, refreshToken);

            var config = SpotifyClientConfig.CreateDefault();
            var oauthClient = new OAuthClient(config);

            // Add 30-second timeout to token refresh
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var tokenResponse = await oauthClient.RequestToken(tokenRequest).WaitAsync(cts.Token);

            _currentTokenResponse = tokenResponse;
            _tokenExpiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn);
            _lastTokenRefreshTime = DateTime.UtcNow; // Record refresh time for throttling

            // Update stored refresh token if a new one was provided
            if (!string.IsNullOrEmpty(tokenResponse.RefreshToken))
            {
                await _tokenStorage.SaveRefreshTokenAsync(tokenResponse.RefreshToken);
            }

            // Create new authenticated client
            _authenticatedClient = new SpotifyClient(tokenResponse.AccessToken);
            IsAuthenticated = true;

            _logger.LogInformation("Successfully refreshed access token");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh access token");
            IsAuthenticated = false;
            throw new InvalidOperationException("Failed to refresh access token. Please sign in again.", ex);
        }
    }

    /// <summary>
    /// Gets an authenticated Spotify client, refreshing the token if necessary.
    /// </summary>
    /// <returns>Authenticated SpotifyClient</returns>
    public async Task<SpotifyClient> GetAuthenticatedClientAsync()
    {
        // Check if we need to refresh the token
        if (_authenticatedClient == null || DateTime.UtcNow >= _tokenExpiresAt.AddMinutes(-5))
        {
            _logger.LogInformation("Access token expired or about to expire, refreshing...");
            await RefreshAccessTokenAsync();
        }

        if (_authenticatedClient == null)
            throw new InvalidOperationException("Not authenticated. Please sign in first.");

        return _authenticatedClient;
    }

    /// <summary>
    /// Gets the valid access token string directly.
    /// Needed for SpotifyBatchClient.
    /// </summary>
    public async Task<string> GetAccessTokenAsync()
    {
         // Ensure we have a valid client first (triggers refresh if needed)
         await GetAuthenticatedClientAsync();
         
         if (_currentTokenResponse?.AccessToken == null)
             throw new InvalidOperationException("Not authenticated");
             
         return _currentTokenResponse.AccessToken;
    }

    /// <summary>
    /// Signs out the user and clears stored tokens.
    /// </summary>
    public async Task SignOutAsync()
    {
        await _tokenStorage.DeleteRefreshTokenAsync();
        _authenticatedClient = null;
        _currentTokenResponse = null;
        _currentCodeVerifier = null;
        IsAuthenticated = false;

        _logger.LogInformation("User signed out, tokens cleared");
    }

    /// <summary>
    /// Tests the connection by attempting to fetch the current user profile.
    /// </summary>
    public async Task<bool> TestConnectionAsync()
    {
        try
        {
             var client = await GetAuthenticatedClientAsync();
             var user = await client.UserProfile.Current();
             return user != null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Spotify connection test failed");
            return false;
        }
    }

    /// <summary>
    /// Gets the current user's profile information.
    /// </summary>
    public async Task<PrivateUser> GetCurrentUserAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var userTask = Task.Run(async () =>
        {
            var client = await GetAuthenticatedClientAsync();
            return await client.UserProfile.Current();
        }, cts.Token);

        return await userTask;
    }

    /// <summary>
    /// Opens the default browser to the specified URL.
    /// </summary>
    private void OpenBrowser(string url)
    {
        try
        {
            // Cross-platform browser opening
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
            }
            else if (OperatingSystem.IsLinux())
            {
                Process.Start("xdg-open", url);
            }
            else
            {
                _logger.LogWarning("Unsupported platform for opening browser");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open browser");
            throw new InvalidOperationException($"Failed to open browser. Please manually navigate to: {url}", ex);
        }
    }

    /// <summary>
    /// Clears cached Spotify credentials (diagnostic method for testing auth issues).
    /// This is useful to confirm if the app has a "poisoned" token cache.
    /// </summary>
    public async Task ClearCachedCredentialsAsync()
    {
        try
        {
            _logger.LogInformation("Clearing cached Spotify credentials...");

            // Delete the stored refresh token
            await _tokenStorage.DeleteRefreshTokenAsync();

            // Clear in-memory auth state
            _authenticatedClient = null;
            _currentTokenResponse = null;
            _currentCodeVerifier = null;
            IsAuthenticated = false;
            _lastTokenRefreshTime = DateTime.MinValue;

            _logger.LogInformation("✓ Spotify credentials cleared. User must re-authenticate.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear Spotify credentials");
            throw;
        }
    }
}
