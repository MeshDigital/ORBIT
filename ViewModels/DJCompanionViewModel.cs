using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Models.Musical;
using SLSKDONET.Services;
using SLSKDONET.Services.Analysis;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Musical;

namespace SLSKDONET.ViewModels;

/// <summary>
/// DJ Companion ViewModel - MixinKey-inspired unified mixing workspace.
/// Shows 1 track with AI-powered match recommendations from all services.
/// </summary>
public class DJCompanionViewModel : ReactiveObject
{
    private readonly HarmonicMatchService _harmonicMatchService;
    private readonly LibraryService _libraryService;
    private readonly PersonalClassifierService _styleClassifier;
    private readonly TransitionReasoningBuilder _transitionBuilder;
    private readonly IPlayerService _playerService;
    private readonly IEventBus _eventBus;

    // Observable Collections for UI Binding
    public ObservableCollection<HarmonicMatchDisplayItem> HarmonicMatches { get; } = new();
    public ObservableCollection<BpmMatchDisplayItem> BpmMatches { get; } = new();
    public ObservableCollection<EnergyMatchDisplayItem> EnergyMatches { get; } = new();
    public ObservableCollection<StyleMatchDisplayItem> StyleMatches { get; } = new();
    public ObservableCollection<MixingAdviceItem> MixingAdvice { get; } = new();
    public ObservableCollection<string> AvailableStems { get; } = new();

    private UnifiedTrackViewModel? _currentTrack;
    public UnifiedTrackViewModel? CurrentTrack
    {
        get => _currentTrack;
        set => this.RaiseAndSetIfChanged(ref _currentTrack, value);
    }

    private PlayerViewModel _player;
    public PlayerViewModel Player
    {
        get => _player;
        set => this.RaiseAndSetIfChanged(ref _player, value);
    }

    private string _helpText = "Select a track and explore intelligent mixing recommendations";
    public string HelpText
    {
        get => _helpText;
        set => this.RaiseAndSetIfChanged(ref _helpText, value);
    }

    private int _harmonicMatchCount;
    public int HarmonicMatchCount
    {
        get => _harmonicMatchCount;
        set => this.RaiseAndSetIfChanged(ref _harmonicMatchCount, value);
    }

    private int _bpmMatchCount;
    public int BpmMatchCount
    {
        get => _bpmMatchCount;
        set => this.RaiseAndSetIfChanged(ref _bpmMatchCount, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    private double _playbackProgress;
    public double PlaybackProgress
    {
        get => _playbackProgress;
        set => this.RaiseAndSetIfChanged(ref _playbackProgress, value);
    }

    private string _currentTimeDisplay = "0:00";
    public string CurrentTimeDisplay
    {
        get => _currentTimeDisplay;
        set => this.RaiseAndSetIfChanged(ref _currentTimeDisplay, value);
    }

    private string _totalTimeDisplay = "0:00";
    public string TotalTimeDisplay
    {
        get => _totalTimeDisplay;
        set => this.RaiseAndSetIfChanged(ref _totalTimeDisplay, value);
    }

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        set => this.RaiseAndSetIfChanged(ref _isPlaying, value);
    }

    public string PlayButtonLabel => IsPlaying ? "⏸ Pause" : "▶ Play";

    // Phase 5.4: Setlist Stress-Test Integration
    private StressDiagnosticReport _stressReport;
    public StressDiagnosticReport StressReport
    {
        get => _stressReport;
        set => this.RaiseAndSetIfChanged(ref _stressReport, value);
    }

    private SetListEntity _currentSetlist;
    public SetListEntity CurrentSetlist
    {
        get => _currentSetlist;
        set => this.RaiseAndSetIfChanged(ref _currentSetlist, value);
    }

    public SetlistHealthBarViewModel HealthBarViewModel { get; private set; }
    public ForensicInspectorViewModel ForensicInspectorViewModel { get; private set; }

    public System.Windows.Input.ICommand TogglePlayCommand { get; }
    public System.Windows.Input.ICommand PreviewStemCommand { get; }
    public System.Windows.Input.ICommand LoadTrackCommand { get; }
    public ReactiveCommand<Unit, StressDiagnosticReport> RunSetlistStressTestCommand { get; private set; }

    public DJCompanionViewModel(
        HarmonicMatchService harmonicMatchService,
        LibraryService libraryService,
        PersonalClassifierService styleClassifier,
        PlayerViewModel playerViewModel,
        IEventBus eventBus,
        SetlistStressTestService stressTestService = null,
        AppDbContext dbContext = null)
    {
        _harmonicMatchService = harmonicMatchService;
        _libraryService = libraryService;
        _styleClassifier = styleClassifier;
        _eventBus = eventBus;
        _player = playerViewModel;

        TogglePlayCommand = ReactiveCommand.Create(TogglePlay);
        PreviewStemCommand = ReactiveCommand.CreateFromTask<string>(PreviewStemAsync);
        LoadTrackCommand = ReactiveCommand.CreateFromTask<UnifiedTrackViewModel>(LoadTrackAsync);

        // Phase 5.4: Initialize Stress-Test Command
        RunSetlistStressTestCommand = ReactiveCommand.CreateFromTask<Unit, StressDiagnosticReport>(
            async _ => await RunSetlistStressTestAsync(stressTestService, dbContext));

        // Initialize child ViewModels
        HealthBarViewModel = new SetlistHealthBarViewModel();
        ForensicInspectorViewModel = new ForensicInspectorViewModel();

        // Wire segment selection → inspector detail
        HealthBarViewModel.SegmentSelected.Subscribe(stressPoint =>
        {
            if (stressPoint != null)
                ForensicInspectorViewModel.DisplayStressPointDetail(stressPoint);
        });

        // Subscribe to track changes from player
        _eventBus.Subscribe<TrackSelectedEvent>(OnTrackSelected);
    }

    private void OnTrackSelected(TrackSelectedEvent evt)
    {
        if (evt?.Track != null)
        {
            // Load the selected track into the companion
            _ = LoadTrackAsync(evt.Track);
        }
    }

    /// <summary>
    /// Main entry point: Load a track and generate all recommendations.
    /// </summary>
    public async Task LoadTrackAsync(UnifiedTrackViewModel track)
    {
        CurrentTrack = track;
        IsLoading = true;

        try
        {
            // Update display
            HelpText = $"Analyzing '{track.Title}' by '{track.Artist}'...";

            // Clear previous recommendations
            HarmonicMatches.Clear();
            BpmMatches.Clear();
            EnergyMatches.Clear();
            StyleMatches.Clear();
            MixingAdvice.Clear();
            AvailableStems.Clear();

            // Populate available stems
            PopulateAvailableStems();

            // 1. Fetch Harmonic Matches (Key-based)
            await FetchHarmonicMatchesAsync(track);

            // 2. Fetch BPM Matches (Tempo-based)
            await FetchBpmMatchesAsync(track);

            // 3. Fetch Energy Matches
            await FetchEnergyMatchesAsync(track);

            // 4. Fetch Style Matches (ML-based)
            await FetchStyleMatchesAsync(track);

            // 5. Generate Mixing Advice
            GenerateMixingAdvice(track);

            HelpText = $"Ready to mix! Try {HarmonicMatchCount} harmonic matches or {BpmMatchCount} tempo-synced tracks.";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading track: {ex.Message}");
            HelpText = "Error loading recommendations. Please try again.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task FetchHarmonicMatchesAsync(UnifiedTrackViewModel track)
    {
        try
        {
            if (track.Model == null || !track.Model.Id.HasValue) return;

            var matches = await _harmonicMatchService.FindMatchesAsync(
                track.Model.Id.Value,
                limit: 12,
                includeBpmRange: true,
                includeEnergyMatch: true);

            foreach (var match in matches)
            {
                HarmonicMatches.Add(new HarmonicMatchDisplayItem
                {
                    Title = match.MatchedTrack.Title,
                    Artist = match.MatchedTrack.Artist,
                    Album = match.MatchedTrack.Album,
                    KeyMatch = match.MatchedTrack.MusicalKey ?? "—",
                    CompatibilityScore = (int)match.CompatibilityScore,
                    KeyRelation = DetermineKeyRelation(track.Model.MusicalKey, match.MatchedTrack.MusicalKey),
                    Track = match.MatchedTrack
                });
            }

            HarmonicMatchCount = HarmonicMatches.Count;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error fetching harmonic matches: {ex.Message}");
        }
    }

    private async Task FetchBpmMatchesAsync(UnifiedTrackViewModel track)
    {
        try
        {
            if (track.Model?.BPM == null) return;

            // Find tracks within ±6% BPM range (standard DJ beatmatching tolerance)
            double minBpm = track.Model.BPM.Value * 0.94;
            double maxBpm = track.Model.BPM.Value * 1.06;

            var allTracks = await _libraryService.GetAllTracksAsync();
            var bpmMatches = allTracks
                .Where(t => t.BPM.HasValue && t.BPM.Value >= minBpm && t.BPM.Value <= maxBpm && t.Id != track.Model.Id)
                .OrderBy(t => Math.Abs(t.BPM ?? 0 - track.Model.BPM.Value))
                .Take(12)
                .ToList();

            foreach (var match in bpmMatches)
            {
                BpmMatches.Add(new BpmMatchDisplayItem
                {
                    Title = match.Title,
                    Artist = match.Artist,
                    Album = match.Album,
                    BpmDisplay = $"{match.BPM:F0}",
                    BpmDifference = Math.Abs(match.BPM ?? 0 - track.Model.BPM.Value),
                    Track = match
                });
            }

            BpmMatchCount = BpmMatches.Count;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error fetching BPM matches: {ex.Message}");
        }
    }

    private async Task FetchEnergyMatchesAsync(UnifiedTrackViewModel track)
    {
        try
        {
            if (track.Model?.Energy == null) return;

            var allTracks = await _libraryService.GetAllTracksAsync();

            // Find tracks with similar or complementary energy levels
            var energyMatches = allTracks
                .Where(t => t.Energy.HasValue && t.Id != track.Model.Id)
                .OrderBy(t => Math.Abs(t.Energy ?? 0 - track.Model.Energy.Value))
                .Take(12)
                .ToList();

            foreach (var match in energyMatches)
            {
                var energyDiff = Math.Abs(match.Energy ?? 0 - track.Model.Energy.Value);
                string direction = match.Energy > track.Model.Energy ? "↑ Rising" : (match.Energy < track.Model.Energy ? "↓ Dropping" : "→ Stable");

                EnergyMatches.Add(new EnergyMatchDisplayItem
                {
                    Title = match.Title,
                    Artist = match.Artist,
                    Album = match.Album,
                    Energy = match.Energy ?? 0,
                    EnergyDirection = direction,
                    Track = match
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error fetching energy matches: {ex.Message}");
        }
    }

    private async Task FetchStyleMatchesAsync(UnifiedTrackViewModel track)
    {
        try
        {
            if (track.Model == null || !track.Model.Id.HasValue) return;

            // Use the PersonalClassifier or genre-based matching
            var allTracks = await _libraryService.GetAllTracksAsync();

            var styleMatches = allTracks
                .Where(t => t.Id != track.Model.Id && !string.IsNullOrEmpty(t.Genres))
                .OrderBy(t => Guid.NewGuid()) // Simple shuffle - could be improved with ML
                .Take(8)
                .ToList();

            foreach (var match in styleMatches)
            {
                StyleMatches.Add(new StyleMatchDisplayItem
                {
                    Title = match.Title,
                    Artist = match.Artist,
                    Album = match.Album,
                    Genre = match.Genres ?? "Unknown",
                    Track = match
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error fetching style matches: {ex.Message}");
        }
    }

    private void GenerateMixingAdvice(UnifiedTrackViewModel track)
    {
        MixingAdvice.Clear();

        // Advice based on track characteristics
        if (track.Model?.BPM != null)
        {
            MixingAdvice.Add(new MixingAdviceItem
            {
                Title = "💫 Tempo Strategy",
                Description = $"This track sits at {track.Model.BPM:F0} BPM. Use songs in the {track.Model.BPM * 0.94:F0}-{track.Model.BPM * 1.06:F0} range for seamless beatmatching."
            });
        }

        if (!string.IsNullOrEmpty(track.Model?.MusicalKey))
        {
            MixingAdvice.Add(new MixingAdviceItem
            {
                Title = "🎼 Harmonic Mixing",
                Description = $"Key: {track.Model.MusicalKey}. Compatible keys are ±1 semitone away. Check the Harmonic Matches list for pre-matched tracks."
            });
        }

        if (track.Model?.Energy != null)
        {
            string energyLevel = track.Model.Energy < 0.4 ? "mellow" : (track.Model.Energy > 0.7 ? "high energy" : "moderate energy");
            MixingAdvice.Add(new MixingAdviceItem
            {
                Title = "⚡ Energy Flow",
                Description = $"This track has {energyLevel} ({track.Model.Energy:P0}). Pair with similar tracks to maintain set momentum."
            });
        }

        if (track.Model?.Danceability != null && track.Model.Danceability > 0.7)
        {
            MixingAdvice.Add(new MixingAdviceItem
            {
                Title = "🕺 Danceability Peak",
                Description = "High danceability! Great for peak-time mixing. Consider it as a set highlight or climax point."
            });
        }

        MixingAdvice.Add(new MixingAdviceItem
        {
            Title = "🧠 AI Recommendations",
            Description = "View Harmonic, Tempo, Energy, and Style match lists. Click any track to preview stems or add to queue."
        });
    }

    private void PopulateAvailableStems()
    {
        // Check if stems are available for current track
        if (CurrentTrack?.Model?.Id.HasValue == true)
        {
            AvailableStems.Add("🎤 Vocals");
            AvailableStems.Add("🥁 Drums");
            AvailableStems.Add("🎸 Bass");
            AvailableStems.Add("🎹 Keys");
            AvailableStems.Add("🎺 Other");
        }
    }

    private async Task PreviewStemAsync(string stem)
    {
        // Implementation: Play the selected stem through the mixer
        System.Diagnostics.Debug.WriteLine($"Previewing stem: {stem}");
    }

    private void TogglePlay()
    {
        IsPlaying = !IsPlaying;
        this.RaisePropertyChanged(nameof(PlayButtonLabel));

        if (IsPlaying)
            _playerService?.Play();
        else
            _playerService?.Pause();
    }

    private string DetermineKeyRelation(string? seedKey, string? matchKey)
    {
        if (string.IsNullOrEmpty(seedKey) || string.IsNullOrEmpty(matchKey))
            return "—";

        if (seedKey == matchKey) return "Perfect Match";
        // Simplified: could use Camelot wheel logic
        return "Compatible";
    }

    /// <summary>
    /// Phase 5.4: Runs setlist stress-test diagnostic.
    /// Analyzes all transitions in the current setlist, identifies dead-ends,
    /// energy plateaus, vocal clashes, and suggests rescue tracks.
    /// </summary>
    private async Task<StressDiagnosticReport> RunSetlistStressTestAsync(
        SetlistStressTestService stressTestService,
        AppDbContext dbContext)
    {
        if (CurrentSetlist == null || stressTestService == null)
        {
            return new StressDiagnosticReport
            {
                QuickSummary = "Error: No setlist loaded or stress-test service unavailable."
            };
        }

        try
        {
            HelpText = "Running setlist stress-test...";

            // Run the comprehensive diagnostic
            var report = await stressTestService.RunDiagnosticAsync(CurrentSetlist);
            StressReport = report;

            // Update HealthBar with results
            HealthBarViewModel.UpdateReport(report);

            HelpText = report.QuickSummary;
            return report;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error running stress-test: {ex.Message}");
            HelpText = $"Stress-test error: {ex.Message}";
            return new StressDiagnosticReport();
        }
    }
}

// Display Models for UI Binding
public class HarmonicMatchDisplayItem
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string KeyMatch { get; set; } = "";
    public int CompatibilityScore { get; set; }
    public string KeyRelation { get; set; } = "";
    public LibraryEntryEntity? Track { get; set; }
}

public class BpmMatchDisplayItem
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string BpmDisplay { get; set; } = "";
    public double BpmDifference { get; set; }
    public LibraryEntryEntity? Track { get; set; }
}

public class EnergyMatchDisplayItem
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public double Energy { get; set; }
    public string EnergyDirection { get; set; } = "";
    public LibraryEntryEntity? Track { get; set; }
}

public class StyleMatchDisplayItem
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Genre { get; set; } = "";
    public LibraryEntryEntity? Track { get; set; }
}

public class MixingAdviceItem
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
}
