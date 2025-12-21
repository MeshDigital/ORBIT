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
    private readonly AppConfig _config;
    private readonly IEventBus _eventBus;

    public DownloadDiscoveryService(
        ILogger<DownloadDiscoveryService> logger,
        SearchOrchestrationService searchOrchestrator,
        AppConfig config,
        IEventBus eventBus)
    {
        _logger = logger;
        _searchOrchestrator = searchOrchestrator;
        _config = config;
        _eventBus = eventBus;
    }

    /// <summary>
    /// Searches for a track and returns the single best match based on user preferences.
    /// </summary>
    public async Task<Track?> FindBestMatchAsync(PlaylistTrackViewModel track, CancellationToken ct)
    {
        var query = $"{track.Artist} {track.Title}";
        _logger.LogInformation("Discovery started for: {Query} (GlobalId: {Id})", query, track.GlobalId);

        try
        {
            // 1. Configure preferences
            var preferredFormats = string.Join(",", _config.PreferredFormats ?? new System.Collections.Generic.List<string> { "mp3" });
            var minBitrate = _config.PreferredMinBitrate;
            var maxBitrate = 3000; // Cap at reasonable high

            // 2. Perform Search via Orchestrator
            // 2. Perform Search via Orchestrator
            // Collect all results from the stream
            var searchResult = new SearchResult();
            var allTracks = new System.Collections.Generic.List<Track>();

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
            searchResult.Tracks = allTracks;

            if (searchResult.TotalCount == 0 || !searchResult.Tracks.Any())
            {
                _logger.LogWarning("No results found for {Query}", query);
                return null;
            }

            // 3. Select Best Match with "The Brain" (Smart Duration Matching)
            var candidates = searchResult.Tracks.ToList();
            
            // Phase 0.3/0.4: Smart Match - Duration Gating
            // We use a tolerance of 15 seconds to account for silence or slight version differences,
            // but strict enough to separate Radio Edits from Extended Mixes.
            if (track.Model.CanonicalDuration.HasValue && track.Model.CanonicalDuration.Value > 0)
            {
                var expectedDurationSec = track.Model.CanonicalDuration.Value / 1000.0;
                var toleranceSec = 15.0; 
                
                var smartMatches = candidates
                    .Where(t => t.Length.HasValue && Math.Abs(t.Length.Value - expectedDurationSec) <= toleranceSec)
                    .ToList();

                if (smartMatches.Any())
                {
                    _logger.LogInformation("🧠 BRAIN: Smart Match Active! Found {Count} candidates matching duration {Duration}s (+/- {Tolerance}s)", 
                        smartMatches.Count, (int)expectedDurationSec, toleranceSec);
                    
                    // Promote smart matches, effectively filtering out the others from the top spot
                    candidates = smartMatches;
                }
                else
                {
                     // Fallback strategy: If no track matches the duration, we warn but allow the "best available" logic to take over
                     _logger.LogWarning("🧠 BRAIN: No candidates matched expected duration {Duration}s. Falling back to best available from {Total} results.", 
                        (int)expectedDurationSec, candidates.Count);
                }
            }

            // Since SearchOrchestrator already ranks results using ResultSorter (which considers bitrate, completeness, etc.),
            // the first result of our filtered (or unfiltered) list *should* be the best one according to our criteria.
            var bestMatch = candidates.First();

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
        var bestMatch = await FindBestMatchAsync(new PlaylistTrackViewModel(track), ct);
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
