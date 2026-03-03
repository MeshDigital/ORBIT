using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ReactiveUI;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using SLSKDONET.Models;
using SLSKDONET.Services.AI;

namespace SLSKDONET.Features.LibrarySidebar.ViewModels;

/// <summary>
/// Lightweight DTO for the Vibe Radar scatter plot to keep memory usage low.
/// </summary>
public class VibeTrackDto
{
    public string GlobalId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public double Valence { get; set; }
    public double Arousal { get; set; } // Mapped from Energy (0.0 - 1.0)
}

public class VibeSidebarViewModel : ReactiveObject, ISidebarContent, IDisposable
{
    private readonly DatabaseService _databaseService;
    private readonly ILibraryService _libraryService;
    private readonly IEventBus _eventBus;
    private readonly ArtworkCacheService _artworkCacheService;
    private readonly ISonicMatchService _sonicMatchService;

    private List<VibeTrackDto>? _libraryProjection;
    public List<VibeTrackDto>? LibraryProjection => _libraryProjection;

    private PlaylistTrackViewModel? _primaryTrack;
    public PlaylistTrackViewModel? PrimaryTrack
    {
        get => _primaryTrack;
        set {
            this.RaiseAndSetIfChanged(ref _primaryTrack, value);
            if (value != null) _ = UpdateSonicTwinsAsync(value.GlobalId);
        }
    }

    private readonly ObservableCollection<VibeTrackDto> _sonicTwins = new();
    public ObservableCollection<VibeTrackDto> SonicTwins => _sonicTwins;

    private PlaylistTrackViewModel? _secondaryTrack;
    public PlaylistTrackViewModel? SecondaryTrack
    {
        get => _secondaryTrack;
        set => this.RaiseAndSetIfChanged(ref _secondaryTrack, value);
    }

    private readonly ObservableCollection<PlaylistTrackViewModel> _nearbyTracks = new();
    public ObservableCollection<PlaylistTrackViewModel> NearbyTracks => _nearbyTracks;

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public ICommand FindTracksNearCoordinateCommand { get; }
    public ICommand ApplyVibeConsensusCommand { get; }
    public ICommand ApplyVibePresetCommand { get; }

    public VibeSidebarViewModel(
        DatabaseService databaseService,
        ILibraryService libraryService,
        IEventBus eventBus,
        ArtworkCacheService artworkCacheService,
        ISonicMatchService sonicMatchService)
    {
        _databaseService = databaseService;
        _libraryService = libraryService;
        _eventBus = eventBus;
        _artworkCacheService = artworkCacheService;
        _sonicMatchService = sonicMatchService;

        FindTracksNearCoordinateCommand = ReactiveCommand.CreateFromTask<(float Valence, float Arousal)>(async coord => 
            await FindTracksNearCoordinateAsync(coord.Valence, coord.Arousal));
            
        ApplyVibeConsensusCommand = ReactiveCommand.CreateFromTask(ApplyVibeConsensusAsync);
        ApplyVibePresetCommand = ReactiveCommand.CreateFromTask<string>(ApplyVibePresetAsync);
    }

    public async Task ActivateAsync(PlaylistTrackViewModel track)
    {
        PrimaryTrack = track;
        if (_libraryProjection == null)
        {
            await LoadLibraryProjectionAsync();
        }
    }

    public Task ActivateBulkAsync(IReadOnlyList<PlaylistTrackViewModel> tracks)
    {
        if (tracks.Count > 0) PrimaryTrack = tracks[0];
        return Task.CompletedTask;
    }

    public void Deactivate()
    {
        // Keep the projection for performance, but we could clear results
    }

    private async Task LoadLibraryProjectionAsync()
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            // Fetch all tracks from all playlists (the effective library)
            var tracks = await _libraryService.GetAllPlaylistTracksAsync();
            
            _libraryProjection = tracks
                .Select(t => new VibeTrackDto
                {
                    GlobalId = t.TrackUniqueHash,
                    Title = t.Title,
                    Artist = t.Artist,
                    Valence = Normalize(t.Valence, 5.0),
                    Arousal = Normalize(t.Energy, 0.5) // Prefer Energy for arousal axis in this view
                })
                .ToList();
            
            this.RaisePropertyChanged(nameof(LibraryProjection));
        }
        catch (Exception)
        {
            // Fail silently or log
        }
        finally
        {
            IsLoading = false;
        }
    }

    private double Normalize(double? value, double fallback)
    {
        // If it's the 1-9 scale, normalize to 0-1
        var val = value ?? fallback;
        if (val > 1.0) return (val - 1.0) / 8.0;
        return val;
    }

    private async Task UpdateSonicTwinsAsync(string trackHash)
    {
        try 
        {
            var matches = await _sonicMatchService.FindSonicMatchesAsync(trackHash, 5);
            _sonicTwins.Clear();
            foreach (var m in matches)
            {
                _sonicTwins.Add(new VibeTrackDto
                {
                    GlobalId = m.TrackUniqueHash,
                    Title = m.Title,
                    Artist = m.Artist,
                    Valence = Normalize(m.Valence, 5.0),
                    Arousal = Normalize(m.Arousal, 5.0)
                });
            }
            this.RaisePropertyChanged(nameof(SonicTwins));
        }
        catch { /* Ignore */ }
    }

    public async Task FindTracksNearCoordinateAsync(float targetValence, float targetArousal)
    {
        if (_libraryProjection == null || _libraryProjection.Count == 0) return;

        // Euclidean Distance Engine: sqrt((v1-v2)^2 + (a1-a2)^2)
        var nearbyDtos = _libraryProjection
            .OrderBy(t => Math.Sqrt(Math.Pow(t.Valence - targetValence, 2) + Math.Pow(t.Arousal - targetArousal, 2)))
            .Take(5)
            .ToList();

        NearbyTracks.Clear();
        var allTracks = await _libraryService.GetAllPlaylistTracksAsync();
        
        foreach (var dto in nearbyDtos)
        {
            var instance = allTracks.FirstOrDefault(t => t.TrackUniqueHash == dto.GlobalId);
            if (instance != null)
            {
                NearbyTracks.Add(new PlaylistTrackViewModel(instance, _eventBus, _libraryService, _artworkCacheService));
            }
        }
    }

    public void SetSecondaryTrack(PlaylistTrackViewModel? track)
    {
        SecondaryTrack = track;
    }

    public async Task ApplyVibeConsensusAsync()
    {
        if (PrimaryTrack == null) return;
        
        var matches = await _sonicMatchService.FindSonicMatchesAsync(PrimaryTrack.GlobalId, 10);
        var tags = matches
            .Where(m => !string.IsNullOrEmpty(m.MoodTag))
            .GroupBy(m => m.MoodTag)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
            
        if (tags != null && tags.Count() >= 3)
        {
            PrimaryTrack.MoodTag = tags.Key;
            // Offer to save to DB (assuming VM has access or publishes event)
            _eventBus.Publish(new TrackUpdatedEvent(PrimaryTrack));
        }
    }

    public async Task ApplyVibePresetAsync(string presetName)
    {
        if (PrimaryTrack == null) return;
        
        // Map preset name to coordinates and tags
        switch (presetName.ToLower())
        {
            case "peak hour":
                PrimaryTrack.Energy = 0.9f;
                PrimaryTrack.Valence = 0.8f;
                PrimaryTrack.MoodTag = "Euphoric";
                break;
            case "sunset":
                PrimaryTrack.Energy = 0.4f;
                PrimaryTrack.Valence = 0.7f;
                PrimaryTrack.MoodTag = "Chill";
                break;
            case "dark room":
                PrimaryTrack.Energy = 0.7f;
                PrimaryTrack.Valence = 0.2f;
                PrimaryTrack.MoodTag = "Dark";
                break;
        }
        
        _eventBus.Publish(new TrackUpdatedEvent(PrimaryTrack));
    }

    public void Dispose()
    {
        _libraryProjection?.Clear();
        _nearbyTracks.Clear();
    }
}
