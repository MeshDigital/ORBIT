using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Services.Repositories;
using SLSKDONET.Models;
using SLSKDONET.Events;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data;

namespace SLSKDONET.Services;

/// <summary>
/// "The Enricher"
/// Orchestrates persistent, robust metadata enrichment.
/// Polls the database for EnrichmentTasks and executes them via Spotify/Tagger.
/// </summary>
public class MetadataEnrichmentOrchestrator : IDisposable
{
    private readonly ILogger<MetadataEnrichmentOrchestrator> _logger;
    private readonly IEnrichmentTaskRepository _taskRepository;
    private readonly ISpotifyMetadataService _metadataService;
    private readonly ITaggerService _taggerService;
    private readonly DatabaseService _databaseService;
    private readonly SpotifyAuthService _spotifyAuthService;
    private readonly SonicIntegrityService _sonicIntegrityService;
    private readonly IEventBus _eventBus;
    private readonly Configuration.AppConfig _config;

    private readonly CancellationTokenSource _cts = new();
    private Task? _processingTask;

    public MetadataEnrichmentOrchestrator(
        ILogger<MetadataEnrichmentOrchestrator> logger,
        IEnrichmentTaskRepository taskRepository,
        ISpotifyMetadataService metadataService,
        ITaggerService taggerService,
        DatabaseService databaseService,
        SpotifyAuthService spotifyAuthService,
        SonicIntegrityService sonicIntegrityService,
        IEventBus eventBus,
        Configuration.AppConfig config)
    {
        _logger = logger;
        _taskRepository = taskRepository; // [NEW] Persistent Repository
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
        _logger.LogInformation("✅ Persistent Metadata Enrichment Orchestrator started.");
    }

    /// <summary>
    /// Queues a track for metadata enrichment (Persistent).
    /// </summary>
    public async Task QueueForEnrichmentAsync(string trackId, Guid? albumId = null)
 {
     await _taskRepository.QueueTaskAsync(trackId, albumId);
 }

    private async Task ProcessQueueLoop(CancellationToken token)
    {
        // Warn-up delay
        try { await Task.Delay(5000, token); } catch { return; }

        while (!token.IsCancellationRequested)
        {
            try
            {
                // 1. Poll for next task
                var task = await _taskRepository.GetNextPendingTaskAsync();
                
                if (task == null)
                {
                    // No work, sleep and continue
                    await Task.Delay(2000, token);
                    continue;
                }

                // 2. Mark as Processing (Claim it)
                await _taskRepository.MarkProcessingAsync(task.Id);
                _logger.LogDebug("Processing enrichment task for Track {TrackId}", task.TrackId);

                // 3. Execute Logic
                await ProcessTaskAsync(task);

                // Note: ProcessTaskAsync handles MarkCompleted/MarkFailed internally
                
                // Yield briefly to behave nice in loop
                await Task.Delay(100, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Critical error in enrichment loop");
                await Task.Delay(5000, token); // Backoff on critical failure
            }
        }
    }

    private async Task ProcessTaskAsync(EnrichmentTaskEntity task)
    {
        try
        {
            // Fetch Track from DB to get Artist/Title
            var trackEntity = await _databaseService.FindTrackAsync(task.TrackId);
            
            if (trackEntity == null)
            {
                await _taskRepository.MarkFailedAsync(task.Id, "Track not found in DB");
                return;
            }

            // A. Check if track already enriched to prevent infinite loops
            if (trackEntity.IsEnriched && !string.IsNullOrEmpty(trackEntity.SpotifyTrackId))
            {
                _logger.LogDebug("Track {TrackId} already enriched, skipping task {TaskId}", trackEntity.GlobalId, task.Id);
                await _taskRepository.MarkCompletedAsync(task.Id);
                return;
            }

            // B. Check Settings/Auth
            if (!_config.SpotifyUseApi || !_spotifyAuthService.IsAuthenticated)
            {
                await _taskRepository.MarkCompletedAsync(task.Id); // Skip but mark done to clear queue
                return;
            }

            // C. Construct Model
            var model = new PlaylistTrack
            {
                TrackUniqueHash = trackEntity.GlobalId,
                Artist = trackEntity.Artist,
                Title = trackEntity.Title,
                SpotifyTrackId = trackEntity.SpotifyTrackId
            };

            // C. Execute Enrichment (Search or Direct Lookup)
            bool enriched = await _metadataService.EnrichTrackAsync(model);

            // D. Save Updates to DB
            if (enriched)
            {
                trackEntity.SpotifyTrackId = model.SpotifyTrackId;
                trackEntity.SpotifyAlbumId = model.SpotifyAlbumId;
                trackEntity.SpotifyArtistId = model.SpotifyArtistId;
                trackEntity.CoverArtUrl = model.AlbumArtUrl;
                trackEntity.AlbumArtUrl = model.AlbumArtUrl;
                trackEntity.BPM = model.BPM;
                trackEntity.MusicalKey = model.MusicalKey;
                trackEntity.Genres = model.Genres;
                trackEntity.Popularity = model.Popularity;
                trackEntity.CanonicalDuration = model.CanonicalDuration;
                trackEntity.ReleaseDate = model.ReleaseDate;
                trackEntity.Energy = model.Energy;
                trackEntity.Danceability = model.Danceability;
                trackEntity.Valence = model.Valence;
                trackEntity.IsEnriched = true;
                
                // Phase 13C: Apply Tri-State Auto-Tagging after enrichment
                await ApplyAutoTaggingAsync(trackEntity);
                
                await _databaseService.SaveTrackAsync(trackEntity);
                _eventBus.Publish(new TrackMetadataUpdatedEvent(trackEntity.GlobalId));
                _logger.LogInformation("✨ Enriched: {Artist} - {Title}", trackEntity.Artist, trackEntity.Title);
            }
            else
            {
                _logger.LogDebug("No metadata match found for {Artist} - {Title}", trackEntity.Artist, trackEntity.Title);
                
                // Even if not enriched via Spotify, try auto-tagging if analysis results exist
                await ApplyAutoTaggingAsync(trackEntity);
                await _databaseService.SaveTrackAsync(trackEntity);
            }
            
            // Mark task complete (whether enriched or not, we tried)
            await _taskRepository.MarkCompletedAsync(task.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Task execution failed for {TaskId}", task.Id);
            await _taskRepository.MarkFailedAsync(task.Id, ex.Message);
            // Don't re-throw - task is marked failed, let loop continue
        }
    }

    private async Task ApplyAutoTaggingAsync(TrackEntity track)
    {
        try
        {
            var features = await _databaseService.GetAudioFeaturesByHashAsync(track.GlobalId);
            if (features == null) return;

            var tags = new List<string>();
            if (!string.IsNullOrEmpty(track.Genres))
            {
                tags.AddRange(track.Genres.Split(", ", StringSplitOptions.RemoveEmptyEntries));
            }

            // Phase 13C: Tri-State Auto-Tagging Logic
            
            // #club-ready: High Energy + High Danceability
            if (features.Energy > 0.8f && features.Danceability > 0.7f)
            {
                if (!tags.Contains("#club-ready")) tags.Add("#club-ready");
            }

            // #brain-dance: Relaxed/Electronic vibe + Moderate Energy
            if ((features.MoodTag == "Relaxed" || features.MoodTag == "Electronic") && features.Energy < 0.6f)
            {
                if (!tags.Contains("#brain-dance")) tags.Add("#brain-dance");
            }

            // #euphoric: Happy mood with high confidence
            if (features.MoodTag == "Happy" && features.MoodConfidence > 0.8f)
            {
                if (!tags.Contains("#euphoric")) tags.Add("#euphoric");
            }

            // #melancholic: Sad mood with high confidence
            if (features.MoodTag == "Sad" && features.MoodConfidence > 0.8f)
            {
                if (!tags.Contains("#melancholic")) tags.Add("#melancholic");
            }

            var updatedGenres = string.Join(", ", tags.Distinct());
            if (track.Genres != updatedGenres)
            {
                track.Genres = updatedGenres;
                _logger.LogInformation("🏷️ Auto-tagged {Artist} - {Title}: {Tags}", track.Artist, track.Title, updatedGenres);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-tagging failed for {Hash}", track.GlobalId);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

