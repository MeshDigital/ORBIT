using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Services;

/// <summary>
/// "The Seeker"
/// Responsible for finding the best available download link for a given track.
/// Encapsulates search Orchestration and Quality Selection logic.
/// </summary>
public class DownloadDiscoveryService
{
    private readonly ILogger<DownloadDiscoveryService> _logger;
    private readonly SearchOrchestrationService _searchOrchestrator;
    private readonly SearchResultMatcher _matcher;
    private readonly AppConfig _config;
    private readonly IEventBus _eventBus;

    public DownloadDiscoveryService(
        ILogger<DownloadDiscoveryService> logger,
        SearchOrchestrationService searchOrchestrator,
        SearchResultMatcher matcher,
        AppConfig config,
        IEventBus eventBus)
    {
        _logger = logger;
        _searchOrchestrator = searchOrchestrator;
        _matcher = matcher;
        _config = config;
        _eventBus = eventBus;
    }

    /// <summary>
    /// Searches for a track and returns the single best match based on user preferences.
    /// </summary>
    /// <summary>
    /// Searches for a track and returns the single best match based on user preferences.
    /// Phase T.1: Refactored to accept PlaylistTrack model (decoupled from UI).
    /// Phase 12: Updated to use streaming search logic.
    /// </summary>
    public async Task<Track?> FindBestMatchAsync(PlaylistTrack track, CancellationToken ct)
    {
        var query = $"{track.Artist} {track.Title}";
        _logger.LogInformation("Discovery started for: {Query} (GlobalId: {Id})", query, track.TrackUniqueHash);

        try
        {
            // 1. Configure preferences (Respect per-track overrides)
            var formatsList = !string.IsNullOrEmpty(track.PreferredFormats)
                ? track.PreferredFormats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                : _config.PreferredFormats ?? new System.Collections.Generic.List<string> { "mp3" };
            
            var preferredFormats = string.Join(",", formatsList);
            var minBitrate = track.MinBitrateOverride ?? _config.PreferredMinBitrate;
            
            // Cap at reasonable high unless strictly set, but for discovery we want quality
            var maxBitrate = 0; 

            // 2. Perform Search via Orchestrator
            // Use streaming, but since we need the 'best' match from the entire set,
            // we probably need to wait a bit or collect a decent buffer.
            // "The Seeker" fundamentally wants the BEST match, which implies seeing most options.
            // However, since results are ranked on-the-fly, if we trust the ranking, we might find good chunks.
            // But 'OverallScore' is relative? No, it's absolute calculation in ResultSorter now.
            
            var allTracks = new System.Collections.Generic.List<Track>();

            // Consume the stream
            await foreach (var searchTrack in _searchOrchestrator.SearchAsync(
                query,
                preferredFormats,
                minBitrate,
                maxBitrate,
                isAlbumSearch: false,
                cancellationToken: ct))
            {
                allTracks.Add(searchTrack);
            }

            if (!allTracks.Any())
            {
                _logger.LogWarning("No results found for {Query}", query);
                return null;
            }

            // 3. Select Best Match with "The Brain" (Metadata Matching)
            // Use SearchResultMatcher which checks Duration, BPM, Artist/Title similarity
            var bestMatch = _matcher.FindBestMatch(track, allTracks);

            if (bestMatch != null)
            {
                _logger.LogInformation("🧠 BRAIN: Matcher selected: {Filename} (Score > 0.7)", bestMatch.Filename);
                return bestMatch;
            }

            // Fallback: If matcher found nothing good (garbage results?), return the highest quality file found
            _logger.LogWarning("🧠 BRAIN: No suitable metadata match found. Falling back to highest quality result.");
            bestMatch = allTracks.First(); // SearchOrchestrator sorts by Quality DESC

            _logger.LogInformation("Best match found: {Filename} ({Bitrate}kbps, {Length}s)", 
                bestMatch.Filename, bestMatch.Bitrate, bestMatch.Length);

            return bestMatch;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Discovery cancelled for {Query}", query);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discovery failed for {Query}", query);
            return null;
        }
    }

    /// <summary>
    /// Performs discovery and automatically handles queueing or upgrade evaluation.
    /// </summary>
    public async Task DiscoverAndQueueTrackAsync(PlaylistTrack track, CancellationToken ct = default)
    {
        // Step T.1: Pass model directly
        var bestMatch = await FindBestMatchAsync(track, ct);
        if (bestMatch == null) return;

        // Determine if this is an upgrade search based on whether the track already has a file
        bool isUpgrade = !string.IsNullOrEmpty(track.ResolvedFilePath);

        if (isUpgrade)
        {
            int currentBitrate = track.Bitrate ?? 0;
            int newBitrate = bestMatch.Bitrate;
            
            // Upgrade Logic: Better bitrate AND minimum gain achieved
            if (newBitrate > currentBitrate && (newBitrate - currentBitrate) >= _config.UpgradeMinGainKbps)
            {
                _logger.LogInformation("Upgrade Found: {Artist} - {Title} ({New} vs {Old} kbps)", 
                    track.Artist, track.Title, newBitrate, currentBitrate);

                if (_config.UpgradeAutoQueueEnabled)
                {
                    _eventBus.Publish(new AutoDownloadUpgradeEvent(track.TrackUniqueHash, bestMatch));
                }
                else
                {
                    _eventBus.Publish(new UpgradeAvailableEvent(track.TrackUniqueHash, bestMatch));
                }
            }
        }
        else
        {
            // Standard missing track discovery - auto download is assumed here for automation flows
            _eventBus.Publish(new AutoDownloadTrackEvent(track.TrackUniqueHash, bestMatch));
        }
    }
}
