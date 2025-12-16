using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SpotifyAPI.Web;

namespace SLSKDONET.Services;

/// <summary>
/// Service for enriching tracks with Spotify metadata (IDs, artwork, genres, popularity).
/// Implements rate limiting and caching to avoid API spam.
/// </summary>
public class SpotifyMetadataService : ISpotifyMetadataService
{
    private readonly ILogger<SpotifyMetadataService> _logger;
    private readonly AppConfig _config;
    private readonly SpotifyAuthService? _authService;
    private readonly SemaphoreSlim _rateLimiter;
    private readonly DatabaseService _databaseService; // Use DatabaseService wrapper for cache access
    
    // Spotify API rate limit: 30 requests per second (we'll be conservative)
    private const int MaxRequestsPerSecond = 20;
    private const int RateLimitWindowMs = 1000;

    public SpotifyMetadataService(
        ILogger<SpotifyMetadataService> logger,
        AppConfig config,
        DatabaseService databaseService,
        SpotifyAuthService? authService = null)
    {
        _logger = logger;
        _config = config;
        _databaseService = databaseService;
        _authService = authService;
        _rateLimiter = new SemaphoreSlim(MaxRequestsPerSecond, MaxRequestsPerSecond);
    }

    /// <summary>
    /// Enriches a PlaylistTrack with Spotify metadata by searching for it.
    /// </summary>
    public async Task<bool> EnrichTrackAsync(PlaylistTrack track)
    {
        if (string.IsNullOrWhiteSpace(track.Artist) || string.IsNullOrWhiteSpace(track.Title))
        {
            _logger.LogWarning("Cannot enrich track without artist and title");
            return false;
        }

        try
        {
            var metadata = await SearchTrackAsync(track.Artist, track.Title);
            if (metadata == null)
            {
                _logger.LogDebug("No Spotify metadata found for {Artist} - {Title}", track.Artist, track.Title);
                return false;
            }

            // Apply metadata to track
            track.SpotifyTrackId = metadata.Id;
            track.SpotifyAlbumId = metadata.SpotifyAlbumId;
            track.SpotifyArtistId = metadata.SpotifyArtistId;
            track.AlbumArtUrl = metadata.AlbumArtUrl;
            track.ArtistImageUrl = metadata.ArtistImageUrl;
            track.Genres = metadata.Genres != null ? JsonSerializer.Serialize(metadata.Genres) : null;
            track.Popularity = metadata.Popularity;
            track.CanonicalDuration = metadata.DurationMs;
            track.ReleaseDate = ParseReleaseDate(metadata.ReleaseDate); // Parse string from record

            _logger.LogInformation("Enriched track {Artist} - {Title} with Spotify ID: {Id}", 
                track.Artist, track.Title, metadata.Id);
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enrich track {Artist} - {Title}", track.Artist, track.Title);
            return false;
        }
    }

    /// <summary>
    /// Searches Spotify for track metadata by artist and title.
    /// </summary>
    public async Task<SpotifyMetadata?> SearchTrackAsync(string artist, string title)
    {
        var cacheKey = $"search:{artist}|{title}".ToLowerInvariant();
        
        // Check persistent cache
        if (await _databaseService.GetCachedSpotifyMetadataAsync(cacheKey) is { } cached) 
        {
             _logger.LogDebug("Cache hit for {Artist} - {Title}", artist, title);
             return cached;
        }

        // Rate limiting
        await _rateLimiter.WaitAsync();
        try
        {
            var client = await GetSpotifyClientAsync();
            if (client == null)
            {
                _logger.LogWarning("Spotify client not available for metadata lookup");
                return null;
            }

            // Search for track
            var query = $"artist:{artist} track:{title}";
            var searchRequest = new SearchRequest(SearchRequest.Types.Track, query)
            {
                Limit = 5 // Get top 5 results for better matching
            };

            var searchResponse = await client.Search.Item(searchRequest);
            
            if (searchResponse.Tracks?.Items == null || !searchResponse.Tracks.Items.Any())
            {
                _logger.LogDebug("No Spotify results for query: {Query}", query);
                return null;
            }

            // Find best match (first result is usually best, but we could add fuzzy matching here)
            var track = searchResponse.Tracks.Items.First();
            
            var metadata = new SpotifyMetadata(
                Id: track.Id,
                Title: track.Name,
                Artist: track.Artists?.FirstOrDefault()?.Name ?? artist,
                Album: track.Album?.Name ?? string.Empty,
                AlbumArtUrl: track.Album?.Images?.FirstOrDefault()?.Url ?? string.Empty,
                ArtistImageUrl: string.Empty, // Would need separate artist lookup
                ReleaseDate: track.Album?.ReleaseDate ?? string.Empty,
                Popularity: track.Popularity,
                DurationMs: track.DurationMs,
                Genres: new List<string>(), // Genres are on artist/album, not track
                SpotifyAlbumId: track.Album?.Id ?? string.Empty,
                SpotifyArtistId: track.Artists?.FirstOrDefault()?.Id ?? string.Empty
            );

            // Cache the result
            await _databaseService.CacheSpotifyMetadataAsync(cacheKey, metadata);
            // Also cache by ID for faster future lookups
            if (!string.IsNullOrEmpty(metadata.Id))
            {
                 await _databaseService.CacheSpotifyMetadataAsync($"id:{metadata.Id}", metadata);
            }
            
            _logger.LogDebug("Found Spotify metadata for {Artist} - {Title}: {Id}", 
                artist, title, metadata.Id);
            
            return metadata;
        }
        catch (APIException ex)
        {
            _logger.LogError(ex, "Spotify API error searching for {Artist} - {Title}", artist, title);
            return null;
        }
        finally
        {
            // Release rate limiter after delay
            _ = Task.Delay(RateLimitWindowMs / MaxRequestsPerSecond).ContinueWith(_ => _rateLimiter.Release());
        }
    }

    /// <summary>
    /// Gets track metadata by Spotify ID (faster than search).
    /// </summary>
    public async Task<SpotifyMetadata?> GetTrackByIdAsync(string spotifyId)
    {
        var cacheKey = $"id:{spotifyId}";
        
        if (await _databaseService.GetCachedSpotifyMetadataAsync(cacheKey) is { } cached) 
            return cached;

        await _rateLimiter.WaitAsync();
        try
        {
            var client = await GetSpotifyClientAsync();
            if (client == null)
                return null;

            var track = await client.Tracks.Get(spotifyId);
            
            var metadata = new SpotifyMetadata(
                Id: track.Id,
                Title: track.Name,
                Artist: track.Artists?.FirstOrDefault()?.Name ?? "Unknown",
                Album: track.Album?.Name ?? string.Empty,
                AlbumArtUrl: track.Album?.Images?.FirstOrDefault()?.Url ?? string.Empty,
                ArtistImageUrl: string.Empty,
                ReleaseDate: track.Album?.ReleaseDate ?? string.Empty,
                Popularity: track.Popularity,
                DurationMs: track.DurationMs,
                Genres: new List<string>(),
                SpotifyAlbumId: track.Album?.Id ?? string.Empty,
                SpotifyArtistId: track.Artists?.FirstOrDefault()?.Id ?? string.Empty
            );

            await _databaseService.CacheSpotifyMetadataAsync(cacheKey, metadata);
            return metadata;
        }
        catch (APIException ex)
        {
            _logger.LogError(ex, "Spotify API error fetching track {Id}", spotifyId);
            return null;
        }
        finally
        {
            _ = Task.Delay(RateLimitWindowMs / MaxRequestsPerSecond).ContinueWith(_ => _rateLimiter.Release());
        }
    }

    /// <summary>
    /// Batch enrichment for multiple tracks (more efficient).
    /// </summary>
    public async Task<int> EnrichTracksAsync(IEnumerable<PlaylistTrack> tracks)
    {
        int enrichedCount = 0;
        
        foreach (var track in tracks)
        {
            if (await EnrichTrackAsync(track))
                enrichedCount++;
            
            // Small delay between tracks to avoid overwhelming the API
            await Task.Delay(50);
        }

        return enrichedCount;
    }

    /// <summary>
    /// Gets a Spotify client (authenticated if available, otherwise public).
    /// </summary>
    private async Task<SpotifyClient?> GetSpotifyClientAsync()
    {
        // Try authenticated client first
        if (_authService != null)
        {
            try
            {
                var isAuthenticated = await _authService.IsAuthenticatedAsync();
                if (isAuthenticated)
                {
                    return await _authService.GetAuthenticatedClientAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get authenticated Spotify client, falling back to public");
            }
        }

        // Fall back to client credentials (public API)
        if (!string.IsNullOrWhiteSpace(_config.SpotifyClientId) && 
            !string.IsNullOrWhiteSpace(_config.SpotifyClientSecret))
        {
            try
            {
                var config = SpotifyClientConfig.CreateDefault();
                var request = new ClientCredentialsRequest(_config.SpotifyClientId, _config.SpotifyClientSecret);
                var response = await new OAuthClient(config).RequestToken(request);
                return new SpotifyClient(config.WithToken(response.AccessToken));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create Spotify client with client credentials");
                return null;
            }
        }

        _logger.LogWarning("No Spotify credentials configured");
        return null;
    }

    private static DateTime? ParseReleaseDate(string? releaseDate)
    {
        if (string.IsNullOrWhiteSpace(releaseDate))
            return null;

        // Spotify release dates can be YYYY, YYYY-MM, or YYYY-MM-DD
        if (DateTime.TryParse(releaseDate, out var date))
            return date;

        return null;
    }
}


