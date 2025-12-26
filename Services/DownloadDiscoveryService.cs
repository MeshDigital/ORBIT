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

    public record DiscoveryResult(Track? BestMatch, SearchAttemptLog? Log)
    {
        public int Bitrate => BestMatch?.Bitrate ?? 0;
    }

    /// <summary>
    /// Searches for a track and returns the single best match based on user preferences.
    /// </summary>
    /// <summary>
    /// Searches for a track and returns the single best match based on user preferences.
    /// Phase T.1: Refactored to accept PlaylistTrack model (decoupled from UI).
    /// Phase 12: Updated to use streaming search logic.
    /// Phase 3B: Added support for peer blacklisting (Health Monitor).
    /// </summary>
    public async Task<DiscoveryResult> FindBestMatchAsync(PlaylistTrack track, CancellationToken ct, HashSet<string>? blacklistedUsers = null)
    {
        var log = new SearchAttemptLog { QueryString = $"{track.Artist} - {track.Title}" };

        // Connectivity Gating: Wait for Soulseek connection before starting search
        if (!_searchOrchestrator.IsConnected)
        {
            _logger.LogInformation("Waiting for Soulseek connection before searching for {Title}...", track.Title);
            var waitStart = DateTime.UtcNow;
            while (!_searchOrchestrator.IsConnected && (DateTime.UtcNow - waitStart).TotalSeconds < 10)
            {
                if (ct.IsCancellationRequested) return new DiscoveryResult(null, log);
                await Task.Delay(500, ct);
            }

            if (!_searchOrchestrator.IsConnected)
            {
                _logger.LogWarning("Timeout waiting for Soulseek connection. Search for {Title} aborted.", track.Title);
                return new DiscoveryResult(null, log);
            }
        }

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
            var searchStartTime = DateTime.UtcNow;
            Track? bestSilverMatch = null;
            double bestSilverScore = 0;

            // Consume the stream
            await foreach (var searchTrack in _searchOrchestrator.SearchAsync(
                query,
                preferredFormats,
                minBitrate,
                maxBitrate,
                isAlbumSearch: false,
                cancellationToken: ct))
            {
                log.ResultsCount++;

                // Phase 3B: Peer Blacklisting
                if (blacklistedUsers != null && 
                    !string.IsNullOrEmpty(searchTrack.Username) && 
                    blacklistedUsers.Contains(searchTrack.Username))
                {
                    log.RejectedByBlacklist++;
                    continue;
                }

                // Phase 3C.4: Threshold Trigger (Race & Replace)
                // Real-time evaluation of incoming results
                var matchResult = _matcher.CalculateMatchResult(track, searchTrack);
                var score = matchResult.Score;
                
                // If we find a "Gold" match (>0.92) early, trigger immediate download
                if (score > 0.92)
                {
                    _logger.LogInformation("🚀 THRESHOLD TRIGGER: Found 'Gold' match ({Score:P0}) early! Skipping rest of search. File: {File}", 
                        score, searchTrack.Filename);
                    return new DiscoveryResult(searchTrack, log);
                }

                // Phase 3C.5: Speculative Start (Silver Match)
                // If we have a decent match (>0.7) and 5 seconds have passed, take it.
                if (score > 0.7)
                {
                    // Track best silver match found so far
                    if (bestSilverMatch == null || score > bestSilverScore)
                    {
                        bestSilverMatch = searchTrack;
                        bestSilverScore = score;
                    }
                }
                else
                {
                    // Track why it was rejected if it's in top 100 results to avoid overcounting
                    if (allTracks.Count < 100)
                    {
                        if (matchResult.ShortReason?.StartsWith("Duration") == true) log.RejectedByQuality++;
                        else if (matchResult.ShortReason?.Contains("Mismatch") == true) log.RejectedByQuality++;
                    }
                }

                // Check speculative timeout (5s)
                if ((DateTime.UtcNow - searchStartTime).TotalSeconds > 5 && bestSilverMatch != null)
                {
                    _logger.LogInformation("🥈 SPECULATIVE TRIGGER: 5s timeout reached with Silver match ({Score:P0}). Starting speculative download. File: {File}", 
                        bestSilverScore, bestSilverMatch.Filename);
                    return new DiscoveryResult(bestSilverMatch, log);
                }

                allTracks.Add(searchTrack);
            }

            if (!allTracks.Any())
            {
                _logger.LogWarning("No results found for {Query}", query);
                return new DiscoveryResult(null, log);
            }

            // 3. Select Best Match with "The Brain" (Metadata Matching)
            // Use SearchResultMatcher which checks Duration, BPM, Artist/Title similarity
            var diagResult = _matcher.FindBestMatchWithDiagnostics(track, allTracks);
            var bestMatch = diagResult.BestMatch;
            
            // Merge diagnostics: use the comprehensive log from Matcher but keep our discovery counts
            diagResult.Log.ResultsCount = log.ResultsCount;
            diagResult.Log.RejectedByBlacklist = log.RejectedByBlacklist;
            log = diagResult.Log;

            if (bestMatch != null)
            {
                _logger.LogInformation("🧠 BRAIN: Matcher selected: {Filename} (Score > 0.7)", bestMatch.Filename);
                return new DiscoveryResult(bestMatch, log);
            }

            // 4. Adaptive Relaxation Strategy (Phase 2.0) - WITH TIMEOUT
            if (_config.EnableRelaxationStrategy && allTracks.Any())
            {
                _logger.LogInformation("🧠 BRAIN: Strict match failed. Waiting {Timeout}s before relaxation...", 
                    _config.RelaxationTimeoutSeconds);
                
                // Wait for the configured timeout before relaxing criteria
                await Task.Delay(TimeSpan.FromSeconds(_config.RelaxationTimeoutSeconds), ct);
                
                _logger.LogInformation("🧠 BRAIN: Timeout reached. Starting relaxation strategy...");
                
                // Relaxation Tier 1: Lower bitrate floor (e.g. 320 -> 256)
                if (minBitrate > 256)
                {
                    _logger.LogInformation("🧠 BRAIN: Relaxation Tier 1: Lowering bitrate floor to 256kbps");
                    var relaxedTracks = allTracks.Where(t => t.Bitrate >= 256).ToList();
                    bestMatch = _matcher.FindBestMatch(track, relaxedTracks);
                    if (bestMatch != null)
                    {
                        _logger.LogInformation("🧠 BRAIN: Tier 1 match found: {Filename}", bestMatch.Filename);
                        return new DiscoveryResult(bestMatch, log);
                    }
                }

                // Relaxation Tier 2: Accept any quality (highest available)
                _logger.LogInformation("🧠 BRAIN: Relaxation Tier 2: Accepting highest available quality");
                bestMatch = allTracks.OrderByDescending(t => t.Bitrate).FirstOrDefault();
                
                if (bestMatch != null)
                {
                    _logger.LogInformation("🧠 BRAIN: Tier 2 fallback: {Filename} ({Bitrate}kbps)", 
                        bestMatch.Filename, bestMatch.Bitrate);
                    return new DiscoveryResult(bestMatch, log);
                }
            }

            _logger.LogWarning("🧠 BRAIN: No suitable match found for {Query}. {Summary}", query, log.GetSummary());
            return new DiscoveryResult(null, log);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Discovery cancelled for {Query}", query);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discovery failed for {Query}", query);
            return new DiscoveryResult(null, log);
        }
    }

    /// <summary>
    /// Performs discovery and automatically handles queueing or upgrade evaluation.
    /// </summary>
    public async Task DiscoverAndQueueTrackAsync(PlaylistTrack track, CancellationToken ct = default, HashSet<string>? blacklistedUsers = null)
    {
        // Step T.1: Pass model directly
        var result = await FindBestMatchAsync(track, ct, blacklistedUsers);
        var bestMatch = result.BestMatch;
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
