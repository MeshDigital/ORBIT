using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services.InputParsers;
using SLSKDONET.Utils;

namespace SLSKDONET.Services;

/// <summary>
/// Orchestrates search operations including Soulseek searches, result ranking, and album grouping.
/// Extracted from MainViewModel to separate business logic from UI coordination.
/// </summary>
public class SearchOrchestrationService
{
    private readonly ILogger<SearchOrchestrationService> _logger;
    private readonly ISoulseekAdapter _soulseek;
    private readonly SearchQueryNormalizer _searchQueryNormalizer;
    private readonly AppConfig _config;
    
    public SearchOrchestrationService(
        ILogger<SearchOrchestrationService> logger,
        ISoulseekAdapter soulseek,
        SearchQueryNormalizer searchQueryNormalizer,
        AppConfig config)
    {
        _logger = logger;
        _soulseek = soulseek;
        _searchQueryNormalizer = searchQueryNormalizer;
        _config = config;
    }
    
    /// <summary>
    /// Execute a search with the given parameters and stream ranked results.
    /// </summary>
    public async IAsyncEnumerable<Track> SearchAsync(
        string query,
        string preferredFormats,
        int minBitrate,
        int maxBitrate,
        bool isAlbumSearch, // Kept for API compatibility, but grouping is now consumer responsibility
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _logger.LogInformation("Streaming search started for: {Query}", query);
        
        var normalizedQuery = _searchQueryNormalizer.RemoveFeatArtists(query);
        normalizedQuery = _searchQueryNormalizer.RemoveYoutubeMarkers(normalizedQuery);
        
        var formatFilter = preferredFormats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        
        // Channel to bridge callback-based Adapter to IAsyncEnumerable
        var channel = System.Threading.Channels.Channel.CreateUnbounded<IList<Track>>();

        // Start the search in a background task
        var searchTask = Task.Run(async () =>
        {
            try
            {
                await _soulseek.SearchAsync(
                    normalizedQuery,
                    formatFilter,
                    (minBitrate, maxBitrate),
                    DownloadMode.Normal,
                    batch =>
                    {
                        // Rank the batch immediately
                        var rankedBatch = RankTrackResults(
                            batch.ToList(),
                            normalizedQuery,
                            formatFilter,
                            minBitrate,
                            maxBitrate);

                        if (rankedBatch.Any())
                        {
                            channel.Writer.TryWrite(rankedBatch);
                        }
                    },
                    cancellationToken);
                
                // Signal completion
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                // Propagate error to channel
                channel.Writer.Complete(ex);
            }
        }, cancellationToken);

        // Yield results as they arrive
        while (await channel.Reader.WaitToReadAsync(cancellationToken))
        {
            while (channel.Reader.TryRead(out var batch))
            {
                foreach (var track in batch)
                {
                    yield return track;
                }
            }
        }
        
        // Await the task to ensure any final exceptions are propagated (though Channel handles most)
        // gracefully ignore cancellation exceptions during shutdown
        try 
        { 
            await searchTask; 
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) 
        {
             _logger.LogError(ex, "Background search task failed");
             throw;
        }
    }
    
    private List<Track> RankTrackResults(
        List<Track> results, 
        string normalizedQuery, 
        string[] formatFilter, 
        int minBitrate, 
        int maxBitrate)
    {
        if (results.Count == 0)
            return results;
            
        _logger.LogInformation("Ranking {Count} search results", results.Count);
        
        // Create search track from query for ranking
        var searchTrack = new Track { Title = normalizedQuery };
        
        // Create evaluator based on current filter settings
        var evaluator = new FileConditionEvaluator();
        if (formatFilter.Length > 0)
        {
            evaluator.AddRequired(new FormatCondition { AllowedFormats = formatFilter.ToList() });
        }
        
        if (minBitrate > 0 || maxBitrate > 0)
        {
            evaluator.AddPreferred(new BitrateCondition 
            { 
                MinBitrate = minBitrate > 0 ? minBitrate : null, 
                MaxBitrate = maxBitrate > 0 ? maxBitrate : null 
            });
        }
        
        // Rank the results
        var rankedResults = ResultSorter.OrderResults(results, searchTrack, evaluator);
        
        _logger.LogInformation("Results ranked successfully");
        return rankedResults.ToList();
    }
    
    private List<AlbumSearchResult> GroupResultsByAlbum(List<Track> tracks)
    {
        _logger.LogInformation("Grouping {Count} tracks into albums", tracks.Count);
        
        // Group by Album + Artist
        var grouped = tracks
            .Where(t => !string.IsNullOrEmpty(t.Album))
            .GroupBy(t => new { t.Album, t.Artist })
            .Select(g => new AlbumSearchResult
            {
                Album = g.Key.Album ?? "Unknown Album",
                Artist = g.Key.Artist ?? "Unknown Artist",
                TrackCount = g.Count(),
                Tracks = g.ToList(),
                // Use the highest bitrate track's info for album metadata
                AverageBitrate = (int)g.Average(t => t.Bitrate),
                Format = g.OrderByDescending(t => t.Bitrate).First().Format
            })
            .OrderByDescending(a => a.TrackCount)
            .ThenByDescending(a => a.AverageBitrate)
            .ToList();
        
        _logger.LogInformation("Grouped into {Count} albums", grouped.Count);
        return grouped;
    }
}

/// <summary>
/// Result of a search operation.
/// </summary>
public class SearchResult
{
    public int TotalCount { get; set; }
    public List<Track> Tracks { get; set; } = new();
    public List<AlbumSearchResult> Albums { get; set; } = new();
    public bool IsAlbumSearch { get; set; }
}

/// <summary>
/// Represents an album in search results.
/// </summary>
public class AlbumSearchResult
{
    public string Album { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public int TrackCount { get; set; }
    public int AverageBitrate { get; set; }
    public string? Format { get; set; }
    public List<Track> Tracks { get; set; } = new();
}
