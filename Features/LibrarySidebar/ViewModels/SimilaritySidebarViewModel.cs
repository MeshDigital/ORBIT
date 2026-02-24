using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Models.Musical;
using SLSKDONET.ViewModels;
using SLSKDONET.Services.Musical;
using SLSKDONET.Services;
using System.Reactive.Linq;

namespace SLSKDONET.Features.LibrarySidebar.ViewModels;

public class SimilaritySidebarViewModel : ReactiveObject, ISidebarContent
{
    private PlaylistTrackViewModel? _activeTrack;
    private readonly SonicMatchService _sonicMatchService;
    private readonly DatabaseService _databaseService;

    public ObservableCollection<SonicMatchResultViewModel> Matches { get; } = new();

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public SimilaritySidebarViewModel(SonicMatchService sonicMatchService, DatabaseService databaseService)
    {
        _sonicMatchService = sonicMatchService;
        _databaseService = databaseService;
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

                return await _sonicMatchService.GetMatchesAsync(entry);
            });

            // Update UI on MainThread
            await Observable.Start(() =>
            {
                foreach (var match in matches)
                {
                    Matches.Add(new SonicMatchResultViewModel(match));
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

    public void Deactivate()
    {
        _activeTrack = null;
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

    public SonicMatchResultViewModel(SonicMatchResult result)
    {
        Artist    = result.Track.Artist;
        Title     = result.Track.Title;
        Score     = result.Score;
        VibeMatch = result.VibeMatch;
        Key       = result.Track.AudioFeatures?.CamelotKey ?? result.Track.AudioFeatures?.Key ?? "—";
        Bpm       = result.Track.AudioFeatures?.Bpm.ToString("F0") ?? "—";

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
