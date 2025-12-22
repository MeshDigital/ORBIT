using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SpotifyAPI.Web;
using SLSKDONET.Services.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Service to enrich local tracks with deep metadata from Spotify (Audio Features).
/// Uses SpotifyAPI.Web and existing authentication.
/// </summary>
public class SpotifyEnrichmentService
{
    private readonly SpotifyAuthService _authService;
    private readonly ILogger<SpotifyEnrichmentService> _logger;

    public SpotifyEnrichmentService(SpotifyAuthService authService, ILogger<SpotifyEnrichmentService> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    /// <summary>
    /// Fetches deep metadata (BPM, Energy, Valence) for a track.
    /// </summary>
    /// <summary>
    /// Stage 1: Identify a track (get Spotify ID) from metadata.
    /// </summary>
    public async Task<TrackEnrichmentResult> IdentifyTrackAsync(string artist, string trackName)
    {
        try 
        {
            var client = await _authService.GetAuthenticatedClientAsync();

            var query = $"track:{trackName} artist:{artist}";
            var searchReq = new SearchRequest(SearchRequest.Types.Track, query) { Limit = 1 };
            
            var response = await client.Search.Item(searchReq);
            var track = response.Tracks.Items?.FirstOrDefault();

            if (track == null)
            {
                 // Fallback: Try simpler query
                 var simplerQuery = $"{artist} {trackName}";
                 searchReq = new SearchRequest(SearchRequest.Types.Track, simplerQuery) { Limit = 1 };
                 response = await client.Search.Item(searchReq);
                 track = response.Tracks.Items?.FirstOrDefault();
            }

            if (track == null)
            {
                 return new TrackEnrichmentResult { Success = false, Error = "No match found" };
            }

            return new TrackEnrichmentResult
            {
                Success = true,
                SpotifyId = track.Id,
                OfficialArtist = track.Artists.FirstOrDefault()?.Name ?? artist,
                OfficialTitle = track.Name,
                AlbumArtUrl = track.Album.Images.OrderByDescending(i => i.Width).FirstOrDefault()?.Url ?? "",
                ISRC = track.ExternalIds != null && track.ExternalIds.ContainsKey("isrc") ? track.ExternalIds["isrc"] : null,
                // Feature fields remain null here
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spotify Identification failed for {Artist} - {Track}", artist, trackName);
            return new TrackEnrichmentResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Stage 2: Batch fetch audio features for multiple Spotify IDs.
    /// </summary>
    public async Task<Dictionary<string, TrackAudioFeatures>> GetAudioFeaturesBatchAsync(System.Collections.Generic.List<string> spotifyIds)
    {
        var result = new Dictionary<string, TrackAudioFeatures>();
        if (!spotifyIds.Any()) return result;

        try
        {
            var client = await _authService.GetAuthenticatedClientAsync();
            
            // API allows max 100 IDs per call
            var chunkedIds = spotifyIds.Chunk(100);
            
            foreach (var chunk in chunkedIds)
            {
                var req = new TracksAudioFeaturesRequest(chunk.ToList());
                var features = await client.Tracks.GetSeveralAudioFeatures(req);
                
                if (features?.AudioFeatures != null)
                {
                    foreach (var feature in features.AudioFeatures)
                    {
                        if (feature != null)
                        {
                            result[feature.Id] = feature;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Failed to batch fetch audio features");
        }
        
        return result;
    }

    /// <summary>
    /// Wrapper for single-track enrichment (legacy/convenience).
    /// </summary>
    public async Task<TrackEnrichmentResult> GetDeepMetadataAsync(string artist, string trackName)
    {
        // 1. Identify
        var identification = await IdentifyTrackAsync(artist, trackName);
        if (!identification.Success || identification.SpotifyId == null) return identification;

        // 2. Fetch Features
        var client = await _authService.GetAuthenticatedClientAsync();
        var features = await client.Tracks.GetAudioFeatures(identification.SpotifyId);
        
        if (features != null)
        {
            identification.Bpm = features.Tempo;
            identification.Energy = features.Energy;
            identification.Valence = features.Valence;
            identification.Danceability = features.Danceability;
            _logger.LogInformation("Spotify: Enriched '{Title}' (BPM: {BPM})", identification.OfficialTitle, features.Tempo);
        }

        return identification;
    }

    /// <summary>
    /// Fetches personalized recommendations based on the user's top tracks.
    /// </summary>
    public async Task<System.Collections.Generic.List<SpotifyTrackViewModel>> GetRecommendationsAsync(int limit = 10)
    {
        var result = new System.Collections.Generic.List<SpotifyTrackViewModel>();
        try
        {
            var client = await _authService.GetAuthenticatedClientAsync();
            
            // Get user's top tracks to use as seeds
            var topTracks = await client.Personalization.GetTopTracks(new PersonalizationTopRequest { Limit = 5 });
            var seedTrackIds = topTracks.Items?.Select(t => t.Id).Take(5).ToList() ?? new System.Collections.Generic.List<string>();
            
            if (!seedTrackIds.Any())
            {
                // Fallback: If no top tracks, use some generic seeds or return empty
                return result;
            }

            // SpotifyAPI.Web v7.2.1: Use Browse.GetRecommendations with proper request object
            var recommendationsReq = new RecommendationsRequest();
            recommendationsReq.Limit = limit;
            // SeedTracks is a List<string>, add items individually
            foreach (var trackId in seedTrackIds)
            {
                recommendationsReq.SeedTracks.Add(trackId);
            }

            var recommendations = await client.Browse.GetRecommendations(recommendationsReq);
            
            if (recommendations.Tracks != null)
            {
                foreach (var track in recommendations.Tracks)
                {
                    result.Add(new SpotifyTrackViewModel
                    {
                        Id = track.Id,
                        Title = track.Name,
                        Artist = track.Artists.FirstOrDefault()?.Name,
                        AlbumName = track.Album.Name,
                        ImageUrl = track.Album.Images.OrderByDescending(i => i.Width).LastOrDefault()?.Url ?? "",
                        ISRC = track.ExternalIds != null && track.ExternalIds.ContainsKey("isrc") ? track.ExternalIds["isrc"] : null
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch Spotify recommendations");
        }
        
        return result;
    }
}

public class SpotifyTrackViewModel
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? AlbumName { get; set; }
    public string? ImageUrl { get; set; }
    public string? ISRC { get; set; }
    public bool InLibrary { get; set; }
}
