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

public class VibeSidebarViewModel : ReactiveObject, ISidebarContent
{
    private readonly DatabaseService _databaseService;
    private readonly ILibraryService _libraryService;
    private readonly IEventBus _eventBus;
    private readonly ArtworkCacheService _artworkCacheService;

    private List<VibeTrackDto>? _libraryProjection;
    public List<VibeTrackDto>? LibraryProjection => _libraryProjection;

    private PlaylistTrackViewModel? _primaryTrack;
    public PlaylistTrackViewModel? PrimaryTrack
    {
        get => _primaryTrack;
        set => this.RaiseAndSetIfChanged(ref _primaryTrack, value);
    }

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

    public VibeSidebarViewModel(
        DatabaseService databaseService,
        ILibraryService libraryService,
        IEventBus eventBus,
        ArtworkCacheService artworkCacheService)
    {
        _databaseService = databaseService;
        _libraryService = libraryService;
        _eventBus = eventBus;
        _artworkCacheService = artworkCacheService;

        FindTracksNearCoordinateCommand = ReactiveCommand.CreateFromTask<(float Valence, float Arousal)>(async coord => 
            await FindTracksNearCoordinateAsync(coord.Valence, coord.Arousal));
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
                .Where(t => t.Valence.HasValue && t.Energy.HasValue)
                .Select(t => new VibeTrackDto
                {
                    GlobalId = t.TrackUniqueHash,
                    Title = t.Title,
                    Artist = t.Artist,
                    Valence = t.Valence ?? 0.5,
                    Arousal = t.Energy ?? 0.5
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
}
