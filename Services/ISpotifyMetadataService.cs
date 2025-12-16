using System.Collections.Generic;
using System.Threading.Tasks;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

public interface ISpotifyMetadataService
{
    /// <summary>
    /// Search for a track by Artist and Title to find its Spotify metadata.
    /// </summary>
    Task<SpotifyMetadata?> SearchTrackAsync(string artist, string title);

    /// <summary>
    /// Enrich a playlist track with Spotify metadata using its SpotifyID or fuzzy search.
    /// </summary>
    Task<bool> EnrichTrackAsync(PlaylistTrack track);

    /// <summary>
    /// Enrich a list of tracks in batch to optimize API usage.
    /// </summary>
    Task<int> EnrichTracksAsync(IEnumerable<PlaylistTrack> tracks);
    
    /// <summary>
    /// Get metadata by Spotify ID.
    /// </summary>
    Task<SpotifyMetadata?> GetTrackByIdAsync(string spotifyId);
}

public record SpotifyMetadata(
    string Id,
    string Title,
    string Artist,
    string Album,
    string AlbumArtUrl,
    string ArtistImageUrl,
    string ReleaseDate,
    int? Popularity,
    int? DurationMs,
    List<string> Genres,
    string SpotifyAlbumId,
    string SpotifyArtistId
);
