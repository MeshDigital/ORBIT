using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Avalonia.Threading;
using SLSKDONET.Services; // Ensure using for Services namespace
using SLSKDONET.Models;
using SLSKDONET.ViewModels;
using System.Linq; // For batching logic eventually
using SLSKDONET.Events; // Add Events namespace

namespace SLSKDONET.Services;

/// <summary>
/// "The Enricher"
/// Orchestrates background metadata enrichment and file tagging.
/// Decouples the "Fire & Forget" logic from DownloadManager.
/// </summary>
public class MetadataEnrichmentOrchestrator : IDisposable
{
    private readonly ILogger<MetadataEnrichmentOrchestrator> _logger;
    private readonly ISpotifyMetadataService _metadataService;
    private readonly ITaggerService _taggerService;
    private readonly DatabaseService _databaseService;
    private readonly SpotifyAuthService _spotifyAuthService; // Injected for strict auth check
    private readonly SonicIntegrityService _sonicIntegrityService;
    private readonly IEventBus _eventBus; // Injected
    private readonly Configuration.AppConfig _config;

    // Queue for tracks needing enrichment
    private readonly ConcurrentQueue<PlaylistTrack> _enrichmentQueue = new(); // Use Model
    private readonly CancellationTokenSource _cts = new();
    private Task? _processingTask;
    private readonly SemaphoreSlim _signal = new(0);

    public MetadataEnrichmentOrchestrator(
        ILogger<MetadataEnrichmentOrchestrator> logger,
        ISpotifyMetadataService metadataService,
        ITaggerService taggerService,
        DatabaseService databaseService,
        SpotifyAuthService spotifyAuthService,
        SonicIntegrityService sonicIntegrityService,
        IEventBus eventBus,
        Configuration.AppConfig config)
    {
        _logger = logger;
        _metadataService = metadataService;
        _taggerService = taggerService;
        _databaseService = databaseService;
        _spotifyAuthService = spotifyAuthService;
        _sonicIntegrityService = sonicIntegrityService;
        _eventBus = eventBus;
        _config = config;
    }

    public void Start()
    {
        if (_processingTask != null) return;
        _processingTask = ProcessQueueLoop(_cts.Token);
        _logger.LogInformation("Metadata Enrichment Orchestrator started.");
        
        // Phase 7: Ghost Download Recovery
        _ = RecoverPendingOrchestrationsAsync();
    }

    private async Task RecoverPendingOrchestrationsAsync()
    {
        try
        {
            var pendingIds = await _databaseService.GetPendingOrchestrationsAsync();
            if (pendingIds.Any())
            {
                _logger.LogInformation("Recovering {Count} pending orchestrations...", pendingIds.Count);
                foreach (var id in pendingIds)
                {
                    // Find the track in DB
                    var trackEntity = await _databaseService.FindTrackAsync(id);
                    if (trackEntity != null && !string.IsNullOrEmpty(trackEntity.Filename))
                    {
                        var track = MapEntityToModel(trackEntity);
                        QueueForEnrichment(track);
                    }
                    else
                    {
                        // Stale orchestration, clean up
                        await _databaseService.RemovePendingOrchestrationAsync(id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recover pending orchestrations");
        }
    }

    private PlaylistTrack MapEntityToModel(Data.TrackEntity e)
    {
        return new PlaylistTrack
        {
            TrackUniqueHash = e.GlobalId,
            Artist = e.Artist,
            Title = e.Title,
            ResolvedFilePath = e.Filename,
            SpotifyTrackId = e.SpotifyTrackId,
            // Phase 8: Sonic Integrity
            SpectralHash = e.SpectralHash,
            QualityConfidence = e.QualityConfidence,
            FrequencyCutoff = e.FrequencyCutoff,
            IsTrustworthy = e.IsTrustworthy ?? true,
            QualityDetails = e.QualityDetails,
        };
    }

    /// <summary>
    /// Queues a track for metadata enrichment (Spotify lookup).
    /// </summary>
    public void QueueForEnrichment(PlaylistTrack track)
    {
        _enrichmentQueue.Enqueue(track);
        _signal.Release();
    }

    /// <summary>
    /// Finalizes a downloaded track: Applies tags and updates DB.
    /// Should be called after file download completes.
    /// </summary>
    public async Task FinalizeDownloadedTrackAsync(PlaylistTrack track)
    {
        if (string.IsNullOrEmpty(track.ResolvedFilePath)) return;

        // Phase 7: Mark as pending orchestration for reliability
        await _databaseService.AddPendingOrchestrationAsync(track.TrackUniqueHash);

        try
        {
            // 1. Enrich Metadata (Last effort if not already done)
            // Note: This matches original logic, but we could check track.SpotifyId here
            await EnrichTrackAsync(track);

            // 2. Write ID3 Tags
            // Create a rich Track object for tagging
            var trackInfo = new Track 
            { 
                Artist = track.Artist, 
                Title = track.Title, 
                Album = track.Album,
                Metadata = new Dictionary<string, object>()
            };

            // Populate Metadata for the Tagger
            if (track.TrackNumber > 0) trackInfo.Metadata["TrackNumber"] = track.TrackNumber;
            if (track.ReleaseDate.HasValue) trackInfo.Metadata["ReleaseDate"] = track.ReleaseDate.Value;
            if (!string.IsNullOrEmpty(track.Genres)) trackInfo.Metadata["Genre"] = track.Genres; // Json parsing logic might be needed in model, but for now passing raw
            if (!string.IsNullOrEmpty(track.AlbumArtUrl)) trackInfo.Metadata["AlbumArtUrl"] = track.AlbumArtUrl;
            
            // Phase 0.5: Enriched Metadata
            if (!string.IsNullOrEmpty(track.SpotifyTrackId)) trackInfo.Metadata["SpotifyTrackId"] = track.SpotifyTrackId;
            if (!string.IsNullOrEmpty(track.SpotifyAlbumId)) trackInfo.Metadata["SpotifyAlbumId"] = track.SpotifyAlbumId;
            if (!string.IsNullOrEmpty(track.SpotifyArtistId)) trackInfo.Metadata["SpotifyArtistId"] = track.SpotifyArtistId;
            
            if (!string.IsNullOrEmpty(track.MusicalKey)) trackInfo.Metadata["MusicalKey"] = track.MusicalKey;
            if (track.BPM.HasValue) trackInfo.Metadata["BPM"] = track.BPM.Value;

            await _taggerService.TagFileAsync(trackInfo, track.ResolvedFilePath);
            _logger.LogInformation("Tagged file: {File}", track.ResolvedFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to finalize/tag track {Artist} - {Title}: {Msg}", track.Artist, track.Title, ex.Message);
        }
    }

    private async Task ProcessQueueLoop(CancellationToken token)
    {
        // Phase 9: Startup Fairness
        // Wait 15s before processing to allow UI and MissionControl to stabilize
        // This staggers it from LibraryEnrichmentWorker (30s)
        try
        {
             _logger.LogInformation("Orchestrator warming up... (15s delay)");
             await Task.Delay(15000, token);
        }
        catch (OperationCanceledException) { return; }

        while (!token.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(token); // Wait for item
                
                // Smart Buffer: Always wait a bit to be greedy and let the queue fill
                // The producer (DownloadManager) might be slow (DB lookups per track).
                await Task.Delay(250, token);
                
                // Dequeue a batch of items
                var batch = new List<PlaylistTrack>();
                
                // Try to fill batch up to 50 items
                while (batch.Count < 50 && _enrichmentQueue.TryDequeue(out var item))
                {
                     batch.Add(item);
                }

                if (!batch.Any()) continue;

                _logger.LogInformation("Orchestrator processing batch of {Count} tracks", batch.Count);

                // Process the batch
                // Check if Enrichment is Globally Enabled AND User is Authenticated
                if (!_config.SpotifyUseApi || !_spotifyAuthService.IsAuthenticated)
                {
                    var reason = !_config.SpotifyUseApi ? "Enrichment Disabled in Settings" : "Not Authenticated";
                    _logger.LogDebug("Skipping metadata enrichment for {Count} tracks: {Reason}", batch.Count, reason);
                    
                    // Proceed to finalize tracks immediately without enrichment
                    // This ensures "Download Only" flow works perfectly
                    foreach (var track in batch)
                    {
                        await FinalizeEnrichmentAsync(track, false); // success=false means "no enrichment data applied"
                    }
                    continue;
                }

                // Split into "Has Spotify ID" vs "Needs Search"
                var knownTracks = batch.Where(t => !string.IsNullOrEmpty(t.SpotifyTrackId)).ToList();
                var unknownTracks = batch.Where(t => string.IsNullOrEmpty(t.SpotifyTrackId)).ToList();

                // 1. Bulk Fetch Audio Features for Known Tracks
                if (knownTracks.Any())
                {
                    try
                    {
                        // Only fetch features for tracks that don't have them yet (Caching Law)
                        var ids = knownTracks
                            .Where(t => !t.BPM.HasValue || t.BPM <= 0)
                            .Select(t => t.SpotifyTrackId!)
                            .ToList();
                        var features = await _metadataService.GetAudioFeaturesBatchAsync(ids);

                        foreach (var track in knownTracks)
                        {
                            if (features.TryGetValue(track.SpotifyTrackId!, out var feature) && feature != null)
                            {
                                ApplyFeatures(track, feature);
                            }
                            
                            await FinalizeEnrichmentAsync(track, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Soft fail for batch enrichment - don't stop the pipeline
                        _logger.LogWarning("Batch enrichment failed (likely 403/Forbidden). Proceeding without audio features. Error: {Message}", ex.Message);
                        foreach (var track in knownTracks)
                        {
                            await FinalizeEnrichmentAsync(track, true); // Still finalize as "success" (we have the track metadata at least)
                        }
                    }
                }

                // 2. Process Unknown Tracks One-by-One (Search cannot be batched easily)
                foreach (var track in unknownTracks)
                {
                    bool success = await _metadataService.EnrichTrackAsync(track);
                    await FinalizeEnrichmentAsync(track, success);
                    
                    // Add small delay only for searches to be safe
                    await Task.Delay(50, token); 
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in enrichment loop");
            }
        }
    }

    private void ApplyFeatures(PlaylistTrack track, SpotifyAPI.Web.TrackAudioFeatures feature)
    {
        track.BPM = feature.Tempo;
        track.MusicalKey = $"{feature.Key}{(feature.Mode == 1 ? "B" : "A")}";
        track.AnalysisOffset = 0;
    }

    private async Task FinalizeEnrichmentAsync(PlaylistTrack track, bool success)
    {
        try 
        {
            // Phase 8: Sonic Integrity
            if (!string.IsNullOrEmpty(track.ResolvedFilePath))
            {
                try
                {
                    var sonicResult = await _sonicIntegrityService.AnalyzeTrackAsync(track.ResolvedFilePath);
                    track.SpectralHash = sonicResult.SpectralHash;
                    track.QualityConfidence = sonicResult.QualityConfidence;
                    track.FrequencyCutoff = sonicResult.FrequencyCutoff;
                    track.IsTrustworthy = sonicResult.IsTrustworthy;
                    track.QualityDetails = sonicResult.Details;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Sonic analysis failed during enrichment for {Artist} - {Title}: {Msg}", track.Artist, track.Title, ex.Message);
                }
            }

            if (success && !string.IsNullOrEmpty(track.SpotifyTrackId))
            {
               _eventBus.Publish(new TrackMetadataUpdatedEvent(track.TrackUniqueHash));

                await _databaseService.SaveTrackAsync(new Data.TrackEntity
                {
                   GlobalId = track.TrackUniqueHash,
                   Artist = track.Artist,
                   Title = track.Title,
                   State = track.Status == TrackStatus.Downloaded ? "Completed" : "Missing",
                   Filename = track.ResolvedFilePath ?? "",
                   CoverArtUrl = track.AlbumArtUrl,
                   SpotifyTrackId = track.SpotifyTrackId,
                   MusicalKey = track.MusicalKey,
                   BPM = track.BPM,
                   AnalysisOffset = track.AnalysisOffset,
                   BitrateScore = track.BitrateScore,
                   AudioFingerprint = track.AudioFingerprint,
                   CuePointsJson = track.CuePointsJson,
                   SpectralHash = track.SpectralHash,
                   QualityConfidence = track.QualityConfidence,
                   FrequencyCutoff = track.FrequencyCutoff,
                   IsTrustworthy = track.IsTrustworthy
                });
            }
            else
            {
                 _logger.LogDebug("Metadata enrichment failed to find match for {Artist} - {Title}", track.Artist, track.Title);
            }

            await _databaseService.RemovePendingOrchestrationAsync(track.TrackUniqueHash);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to finalize enrichment for {Artist} - {Title}", track.Artist, track.Title);
        }
    }

    // Deprecated single method
    private Task EnrichTrackAsync(PlaylistTrack track)
    {
        // Re-routed to new flow, but mostly unused now by loop
        return Task.CompletedTask; 
    }

    public void Dispose()
    {
        _cts.Cancel();
        _signal.Dispose();
        _cts.Dispose();
    }
}
