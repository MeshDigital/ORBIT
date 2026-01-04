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
    private readonly SearchNormalizationService _searchNormalization; // Phase 4.6: Replaces broken parenthesis stripping
    private readonly ISafetyFilterService _safetyFilter; // Week 2: Gatekeeper
    private readonly AppConfig _config;
    
    private readonly ILibraryService _libraryService;
    private readonly Services.AI.PersonalClassifierService _personalClassifier;
    
    // Throttling: Prevent getting banned by issuing too many searches at once
    private readonly SemaphoreSlim _searchSemaphore;
    
    public SearchOrchestrationService(
        ILogger<SearchOrchestrationService> logger,
        ISoulseekAdapter soulseek,
        SearchQueryNormalizer searchQueryNormalizer,
        SearchNormalizationService searchNormalization,
        ISafetyFilterService safetyFilter,
        AppConfig config,
        ILibraryService libraryService,
        Services.AI.PersonalClassifierService personalClassifier)
    {
        _logger = logger;
        _soulseek = soulseek;
        _searchQueryNormalizer = searchQueryNormalizer;
        _searchNormalization = searchNormalization;
        _safetyFilter = safetyFilter;
        _config = config;
        _libraryService = libraryService;
        _personalClassifier = personalClassifier;
        
        // Initialize simple signaling semaphore
        _searchSemaphore = new SemaphoreSlim(Math.Max(1, _config.MaxConcurrentSearches));
    }
    
    public bool IsConnected => _soulseek.IsConnected;
    private int _activeSearchCount = 0;
    public int GetActiveSearchCount() => _activeSearchCount;

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
        // Throttling Check
        bool acquired = false;
        try 
        {
            // Try to acquire slot immediately or wait briefly
            // This prevents queueing 500 searches that will execute 10 minutes later
            await _searchSemaphore.WaitAsync(cancellationToken);
            acquired = true;

            Interlocked.Increment(ref _activeSearchCount);

            _logger.LogInformation("Streaming search started for: {Query} (Slots free: {Count})", 
                query, _searchSemaphore.CurrentCount);
        
        // Phase 4.6 HOTFIX: Use new SearchNormalizationService instead of aggressive parenthesis stripping
        // OLD (BROKEN): Removes ALL parentheses including (VIP), (feat. X), (Remix)
        // NEW: Preserves musical identity, only removes junk
        var (normalizedArtist, normalizedTitle) = _searchNormalization.NormalizeForSoulseek("", query);
        var normalizedQuery = normalizedTitle;
        
        // Legacy normalization (feat removal) - now redundant but kept for safety
        // SearchNormalizationService already handles this better
        // normalizedQuery = _searchQueryNormalizer.RemoveFeatArtists(normalizedQuery);
        // normalizedQuery = _searchQueryNormalizer.RemoveYoutubeMarkers(normalizedQuery); // REMOVED: This was the bug!
        
        var formatFilter = preferredFormats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        
        // Prepare ranking context once
        var searchTrack = new Track { Title = normalizedQuery };
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

        // Stream results from adapter
        await foreach (var track in _soulseek.StreamResultsAsync(
            normalizedQuery,
            formatFilter,
            (minBitrate, maxBitrate),
            DownloadMode.Normal,
            cancellationToken))
        {
            // GATEKEEPER CHECK (Week 2/4)
            // Filter out unsafe/banned/irrelevant results before they even reach the ranking engine
            if (!_safetyFilter.IsSafe(track, normalizedQuery))
            {
                // _logger.LogTrace("Gatekeeper rejected track: {Filename}", track.Filename);
                continue;
            }

            // Rank on-the-fly
            ResultSorter.CalculateRank(track, searchTrack, evaluator);
            yield return track;
        }
        }
        finally
        {
            if (acquired)
            {
                Interlocked.Decrement(ref _activeSearchCount);
                _searchSemaphore.Release();
            }
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
    
    public async Task<List<Track>> SearchSimilarAsync(string seedTrackHash, int limit = 50)
    {
        _logger.LogInformation("Finding sonic twins for track: {Hash}", seedTrackHash);
        
        // 1. Get all candidates (Audio Features)
        var allFeatures = await _libraryService.GetAllAudioFeaturesAsync();
        
        // 2. Find the seed track's embedding
        var seedFeatures = allFeatures.FirstOrDefault(f => f.TrackUniqueHash == seedTrackHash);
        if (seedFeatures == null || string.IsNullOrEmpty(seedFeatures.AiEmbeddingJson))
        {
            _logger.LogWarning("Seed track {Hash} has no embedding. Cannot find similar tracks.", seedTrackHash);
            return new List<Track>();
        }
        
        float[]? seedVector;
        try 
        {
            seedVector = System.Text.Json.JsonSerializer.Deserialize<float[]>(seedFeatures.AiEmbeddingJson);
        }
        catch 
        {
            return new List<Track>();
        }
        
        if (seedVector == null || seedVector.Length != 128)
            return new List<Track>();

        // 3. Perform Vector Search
        var matches = _personalClassifier.FindSimilarTracks(seedVector, (float)seedFeatures.Bpm, allFeatures, limit);
        
        if (matches.Count == 0)
        {
             return new List<Track>();
        }
        
        // 4. Map back to Track objects
        // We need metadata for these hashes.
        var matchedHashes = matches.ToDictionary(m => m.TrackHash, m => m.Similarity);
        var results = new List<Track>();
        
        foreach (var match in matches)
        {
             var entry = await _libraryService.FindLibraryEntryAsync(match.TrackHash);
             if (entry != null)
             {
                 // Create a "Track" that represents this library file
                 // This allows it to be displayed in the SearchResults UI
                 var t = new Track
                 {
                     Filename = System.IO.Path.GetFileName(entry.FilePath),
                     Directory = System.IO.Path.GetDirectoryName(entry.FilePath),
                     Artist = entry.Artist,
                     Title = entry.Title,
                     Album = entry.Album,
                     Size = 0, // LibraryEntry model currently lacks size property, using default 0 for now.
                     Bitrate = entry.Bitrate,
                     Length = entry.DurationSeconds ?? (int)((entry.CanonicalDuration ?? 0) / 1000),
                     Format = System.IO.Path.GetExtension(entry.FilePath)?.TrimStart('.'),
                     FilePath = entry.FilePath,
                     LocalPath = entry.FilePath,
                     IsInLibrary = true,
                     
                     // Compatibility / UI fields
                     CurrentRank = match.Similarity * 100, // Convert 0-1 to 0-100 scale for UI consistency
                     ScoreBreakdown = $"Sonic Twin ({match.Similarity:P0} Match)",
                     
                     // Spotify / Metadata
                     AlbumArtUrl = entry.AlbumArtUrl,
                     SpotifyTrackId = entry.SpotifyTrackId,
                     BPM = entry.BPM,
                     MusicalKey = entry.MusicalKey,
                     Energy = entry.Energy,
                     Danceability = entry.Danceability,
                     Valence = entry.Valence
                 };
                 results.Add(t);
             }
        }
        
        return results;
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
