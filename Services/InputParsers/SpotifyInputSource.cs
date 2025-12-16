using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SpotifyAPI.Web;

namespace SLSKDONET.Services.InputParsers;

/// <summary>
/// Spotify API-based playlist/album fetcher (Client Credentials flow).
/// </summary>
public class SpotifyInputSource : IInputSource
{
	private readonly ILogger<SpotifyInputSource> _logger;
	private readonly AppConfig _config;

	public InputType InputType => InputType.Spotify;

	public bool IsConfigured =>
		!string.IsNullOrWhiteSpace(_config.SpotifyClientId) &&
		!string.IsNullOrWhiteSpace(_config.SpotifyClientSecret);

	public SpotifyInputSource(ILogger<SpotifyInputSource> logger, AppConfig config)
	{
		_logger = logger;
		_config = config;
	}

	public async Task<List<SearchQuery>> ParseAsync(string url)
	{
		if (!IsConfigured)
			throw new InvalidOperationException("Spotify API credentials are not configured.");

		var playlistId = ExtractPlaylistId(url);
		if (string.IsNullOrEmpty(playlistId))
			throw new InvalidOperationException("Invalid Spotify playlist URL.");

		_logger.LogInformation("Spotify API: fetching playlist {PlaylistId}", playlistId);

		var client = await CreateClientAsync();

		var playlist = await client.Playlists.Get(playlistId);
		var total = playlist.Tracks?.Total ?? 0;
		var queries = new List<SearchQuery>();

		if (playlist.Tracks != null)
		{
			await foreach (var item in client.Paginate(playlist.Tracks))
			{
				if (item.Track is FullTrack track)
				{
					var artist = track.Artists?.FirstOrDefault()?.Name;
					var title = track.Name;
					var album = track.Album?.Name;

					if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(artist))
						continue;

					queries.Add(new SearchQuery
					{
						Artist = artist,
						Title = title,
						Album = album,
						SourceTitle = playlist.Name,
						TotalTracks = total,
						DownloadMode = DownloadMode.Normal,
						// Spotify Metadata (Phase 0: Metadata Gravity Well)
						SpotifyTrackId = track.Id,
						SpotifyAlbumId = track.Album?.Id,
						SpotifyArtistId = track.Artists?.FirstOrDefault()?.Id,
						AlbumArtUrl = track.Album?.Images?.FirstOrDefault()?.Url,
						Popularity = track.Popularity,
						CanonicalDuration = track.DurationMs,
						ReleaseDate = DateTime.TryParse(track.Album?.ReleaseDate, out var rd) ? rd : null
					});
				}
			}
		}

		_logger.LogInformation("Spotify API: extracted {Count} tracks", queries.Count);
		return queries;
	}

	private async Task<SpotifyClient> CreateClientAsync()
	{
		var config = SpotifyClientConfig.CreateDefault();
		var request = new ClientCredentialsRequest(_config.SpotifyClientId!, _config.SpotifyClientSecret!);
		var response = await new OAuthClient(config).RequestToken(request);
		return new SpotifyClient(config.WithToken(response.AccessToken));
	}

	public static string? ExtractPlaylistId(string url)
	{
		if (string.IsNullOrWhiteSpace(url)) return null;

		if (url.StartsWith("spotify:playlist:", StringComparison.OrdinalIgnoreCase))
			return url.Replace("spotify:playlist:", "");

		if (url.Contains("spotify.com") && url.Contains("/playlist/"))
		{
			var parts = url.Split('/');
			var idx = Array.IndexOf(parts, "playlist");
			if (idx >= 0 && idx + 1 < parts.Length)
			{
				var id = parts[idx + 1].Split('?')[0];
				return string.IsNullOrEmpty(id) ? null : id;
			}
		}

		return null;
	}
}
