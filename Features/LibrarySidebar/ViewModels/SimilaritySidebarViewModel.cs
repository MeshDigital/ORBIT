using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Models.Musical;
using SLSKDONET.ViewModels;
using SLSKDONET.Services.Musical;
using SLSKDONET.Services;
using System.Reactive.Linq;
using System.Windows.Input;

namespace SLSKDONET.Features.LibrarySidebar.ViewModels;

public class SimilaritySidebarViewModel : ReactiveObject, ISidebarContent, IDisposable
{
    private PlaylistTrackViewModel? _activeTrack;
    private readonly SonicMatchService _sonicMatchService;
    private readonly DatabaseService _databaseService;
    private readonly IEventBus _eventBus;
    private readonly ILibraryService _libraryService;
    private readonly ArtworkCacheService _artworkCacheService;
    private readonly INavigationService _navigationService;
    private readonly ILogger<SimilaritySidebarViewModel> _logger;

    public ObservableCollection<SonicMatchResultViewModel> Matches { get; } = new();

    public ICommand HarmonizePlaylistCommand { get; }
    public ICommand UndoFillCommand { get; }
    public ICommand DiscoverMoreExternallyCommand { get; }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    private SonicMatchResultViewModel? _selectedMatch;
    public SonicMatchResultViewModel? SelectedMatch
    {
        get => _selectedMatch;
        set => this.RaiseAndSetIfChanged(ref _selectedMatch, value);
    }

    public SimilaritySidebarViewModel(
        SonicMatchService sonicMatchService, 
        DatabaseService databaseService,
        IEventBus eventBus,
        ILibraryService libraryService,
        ArtworkCacheService artworkCacheService,
        INavigationService navigationService,
        ILogger<SimilaritySidebarViewModel> logger)
    {
        _sonicMatchService = sonicMatchService;
        _databaseService = databaseService;
        _eventBus = eventBus;
        _libraryService = libraryService;
        _artworkCacheService = artworkCacheService;
        _navigationService = navigationService;
        _logger = logger;

        HarmonizePlaylistCommand = ReactiveCommand.CreateFromTask(HarmonizePlaylistAsync, this.WhenAnyValue(x => x.IsLoading).Select(l => !l));
        UndoFillCommand = ReactiveCommand.CreateFromTask(UndoFillAsync, this.WhenAnyValue(x => x.IsLoading).Select(l => !l));
        DiscoverMoreExternallyCommand = ReactiveCommand.CreateFromTask(DiscoverMoreExternallyAsync, this.WhenAnyValue(x => x.IsLoading).Select(l => !l));
    }

    public async Task ActivateAsync(PlaylistTrackViewModel track)
    {
        if (_activeTrack?.GlobalId == track.GlobalId) return;

        _activeTrack = track;
        await LoadMatchesAsync();
    }

    private async Task LoadMatchesAsync()
    {
        if (_activeTrack == null) return;

        IsLoading = true;
        Matches.Clear();

        try
        {
            // Do not block the UI thread during match calculations; use Task.Run for heavy Euclidean math
            var matches = await Task.Run(async () =>
            {
                var entry = await _databaseService.GetLibraryEntryAsync(_activeTrack.GlobalId);
                if (entry == null) return new List<SonicMatchResult>();

                return await _sonicMatchService.GetMatchesAsync(entry, MatchProfile.VibeMatch);
            });

            // Update UI on MainThread
            await Observable.Start(() =>
            {
                foreach (var match in matches)
                {
                    Matches.Add(new SonicMatchResultViewModel(match, _eventBus, _libraryService, _artworkCacheService));
                }
                IsLoading = false;
            }, RxApp.MainThreadScheduler);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SimilaritySidebar] Failed to load matches: {ex.Message}");
            IsLoading = false;
        }
    }

    public async Task ActivateBulkAsync(IReadOnlyList<PlaylistTrackViewModel> tracks)
    {
        if (tracks.Count > 0)
        {
            await ActivateAsync(tracks[0]);
        }
    }

    public async Task HarmonizePlaylistAsync()
    {
        if (_activeTrack == null || IsLoading) return;

        IsLoading = true;
        try
        {
            var playlistId = _activeTrack.SourceId;
            var currentTracks = await _libraryService.LoadPlaylistTracksAsync(playlistId);
            if (!currentTracks.Any()) return;

            var addedTrackIds = new List<Guid>();
            var newTracks = new List<PlaylistTrack>();

            // To avoid duplicates and overwhelming, we'll find 1 match for each existing track
            // only if that track doesn't already have matches nearby or if it's the original set.
            foreach (var track in currentTracks)
            {
                var entry = await _databaseService.GetLibraryEntryAsync(track.TrackUniqueHash);
                if (entry == null) continue;

                var matches = await _sonicMatchService.GetMatchesAsync(entry, MatchProfile.VibeMatch, limit: 5);
                // Find first match that isn't already in the playlist
                var bestMatch = matches.FirstOrDefault(m => !currentTracks.Any(ct => ct.TrackUniqueHash == m.Track.UniqueHash));
                
                if (bestMatch != null)
                {
                    var newPt = new PlaylistTrack
                    {
                        Id = Guid.NewGuid(),
                        PlaylistId = playlistId,
                        Artist = bestMatch.Track.Artist,
                        Title = bestMatch.Track.Title,
                        TrackUniqueHash = bestMatch.Track.UniqueHash,
                        ResolvedFilePath = bestMatch.Track.FilePath,
                        Status = TrackStatus.Downloaded,
                        BPM = bestMatch.Track.AudioFeatures?.Bpm,
                        MusicalKey = bestMatch.Track.AudioFeatures?.CamelotKey ?? bestMatch.Track.AudioFeatures?.Key,
                        AddedAt = DateTime.UtcNow,
                        SortOrder = track.SortOrder + 1 // We'll need a full re-sort later maybe
                    };
                    newTracks.Add(newPt);
                    addedTrackIds.Add(newPt.Id);
                }
            }

            if (newTracks.Any())
            {
                await _libraryService.SavePlaylistTracksAsync(newTracks);
                
                // Log the activity for Undo
                var detailsJson = System.Text.Json.JsonSerializer.Serialize(addedTrackIds);
                await _libraryService.LogPlaylistActivityAsync(playlistId, "SmartFill", detailsJson);

                // Publish update event
                _eventBus.Publish(new ProjectUpdatedEvent(playlistId));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to harmonize playlist");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task UndoFillAsync()
    {
        if (_activeTrack == null || IsLoading) return;

        IsLoading = true;
        try
        {
            await _libraryService.UndoLastActivityAsync(_activeTrack.SourceId, "SmartFill");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to undo smart fill");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task DiscoverMoreExternallyAsync()
    {
        if (_activeTrack == null) return;

        _logger.LogInformation("Discovery Bridge: Switching to Discovery Hub to find more tracks externally for {Hash}", _activeTrack.GlobalId);

        // Navigate to Discovery Hub
        _navigationService.NavigateTo("DiscoveryHub");

        // We need to trigger the discovery in the DiscoveryHubViewModel.
        // Since ViewModels are resolved via DI, we can't easily get the specific instance here
        // without an EventBus or a shared state.
        // Let's use the EventBus to signal the DiscoveryHub to start discovery.
        _eventBus.Publish(new ExternalDiscoveryRequestedEvent(_activeTrack.GlobalId));
    }

    public void Deactivate()
    {
        _activeTrack = null;
        Matches.Clear();
    }

    public void Dispose()
    {
        // Disposal of matches
        Matches.Clear();
    }
}

public class SonicMatchResultViewModel : ReactiveObject
{
    public string Artist { get; }
    public string Title { get; }
    public float Score { get; }
    public bool VibeMatch { get; }
    public string Key { get; }
    public string Bpm { get; }

    // Phase 5.0: Transparent breakdown fields
    /// <summary>Human-readable tags explaining why this track matched (ready for ListBox binding).</summary>
    public IReadOnlyList<string> MatchTags { get; }
    /// <summary>Percentage string for display (e.g. "87%").</summary>
    public string ConfidenceLabel { get; }
    /// <summary>Colour hex string for the confidence badge.</summary>
    public string BadgeColor { get; }
    public double HarmonicScore { get; }
    public double RhythmScore   { get; }
    public double VibeScore     { get; }
    public string VocalLabel    { get; }

    public PlaylistTrackViewModel TrackVm { get; }

    public SonicMatchResultViewModel(
        SonicMatchResult result, 
        IEventBus? eventBus = null, 
        ILibraryService? libraryService = null, 
        ArtworkCacheService? artworkCacheService = null)
    {
        Artist    = result.Track.Artist;
        Title     = result.Track.Title;
        Score     = result.Score;
        VibeMatch = result.VibeMatch;
        Key       = SLSKDONET.Utils.KeyConverter.ToCamelot(result.Track.AudioFeatures?.CamelotKey ?? result.Track.AudioFeatures?.Key ?? "—");
        Bpm       = result.Track.AudioFeatures?.Bpm.ToString("F0") ?? "—";

        // Create the playable ViewModel wrapper
        var pt = new PlaylistTrack
        {
            Artist = Artist,
            Title = Title,
            TrackUniqueHash = result.Track.UniqueHash,
            ResolvedFilePath = result.Track.FilePath,
            Status = TrackStatus.Downloaded, // Matches from library are downloaded
            BPM = result.Track.AudioFeatures?.Bpm,
            MusicalKey = result.Track.AudioFeatures?.CamelotKey ?? result.Track.AudioFeatures?.Key
        };
        TrackVm = new PlaylistTrackViewModel(pt, eventBus, libraryService, artworkCacheService);

        var bd = result.Breakdown;
        if (bd != null)
        {
            MatchTags     = bd.MatchTags;
            HarmonicScore = bd.HarmonicScore;
            RhythmScore   = bd.RhythmScore;
            VibeScore     = bd.VibeScore;
            VocalLabel    = result.Track.AudioFeatures?.DetectedVocalType.ToDisplayLabel() ?? "—";
        }
        else
        {
            MatchTags  = new List<string>();
            VocalLabel = "—";
        }

        double conf = Score / 100.0;
        ConfidenceLabel = $"{Score:F0}%";
        BadgeColor = conf >= 0.90 ? "#00FF99" : conf >= 0.75 ? "#FFAA00" : "#888888";
    }
}
