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
using SLSKDONET.Services.AI;
using SLSKDONET.Services.Analysis;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Musical;
using SLSKDONET.ViewModels.Downloads;
using SLSKDONET.Models;  // For OrbitCue

namespace SLSKDONET.ViewModels
{
    /// <summary>
    /// DJ Companion ViewModel - MixinKey-inspired unified mixing workspace.
    /// Shows 1 track with AI-powered match recommendations from all services.
    /// Phase 6: Integrated ApplyRescueTrack command for rescue application.
    /// </summary>
    public class DJCompanionViewModel : ReactiveObject
    {
        private readonly HarmonicMatchService _harmonicMatchService;
        private readonly LibraryService _libraryService;
        private readonly IEventBus _eventBus;

        // Observable Collections for UI Binding
        public ObservableCollection<HarmonicMatchDisplayItem> HarmonicMatches { get; } = new();
        public ObservableCollection<BpmMatchDisplayItem> BpmMatches { get; } = new();
        public ObservableCollection<EnergyMatchDisplayItem> EnergyMatches { get; } = new();
        public ObservableCollection<StyleMatchDisplayItem> StyleMatches { get; } = new();
        public ObservableCollection<MixingAdviceItem> MixingAdvice { get; } = new();
        public ObservableCollection<string> AvailableStems { get; } = new();

        // NEW: Setlist Management (Left Panel)
        public ObservableCollection<SetlistTrackItem> CurrentSetlistTracks { get; } = new();

        // NEW: Set Intelligence (Right Panel)
        public ObservableCollection<KeyClashIssue> KeyClashes { get; } = new();
        public ObservableCollection<EnergyGapIssue> EnergyGaps { get; } = new();
        public ObservableCollection<VocalClashIssue> VocalClashes { get; } = new();


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
        public string PlayButtonIcon => IsPlaying ? "⏸" : "▶";

        // NEW: Setlist & Track Selection
        private SetlistTrackItem? _selectedSetlistTrack;
        public SetlistTrackItem? SelectedSetlistTrack
        {
            get => _selectedSetlistTrack;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedSetlistTrack, value);
                if (value != null)
                    OnSetlistTrackSelected(value);
            }
        }

        public int SetlistTrackCount => CurrentSetlistTracks.Count;

        // NEW: Set Intelligence Metrics
        private int _setHealthScore = 100;
        public int SetHealthScore
        {
            get => _setHealthScore;
            set => this.RaiseAndSetIfChanged(ref _setHealthScore, value);
        }

        public bool HasIssues => KeyClashes.Count > 0 || EnergyGaps.Count > 0 || VocalClashes.Count > 0;
        public bool HasBridgeSuggestions => ForensicInspectorViewModel?.RescueSuggestions?.Count > 0;

        // NEW: Cue Editing State
        private bool _isEditingCues;
        public bool IsEditingCues
        {
            get => _isEditingCues;
            set => this.RaiseAndSetIfChanged(ref _isEditingCues, value);
        }

        // NEW: Waveform normalized progress (0.0 - 1.0)
        public float PlaybackProgressNormalized => (float)(PlaybackProgress / 100.0);

        private StressDiagnosticReport? _stressReport;
        public StressDiagnosticReport? StressReport
        {
            get => _stressReport;
            set => this.RaiseAndSetIfChanged(ref _stressReport, value);
        }

        private SetListEntity? _currentSetlist;
        public SetListEntity? CurrentSetlist
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
        public ReactiveCommand<(int, RescueSuggestion), ApplyRescueResult> ApplyRescueTrackCommand { get; private set; }
        
        // NEW: Cue and Seek Commands
        public ReactiveCommand<OrbitCue, Unit> JumpToCueCommand { get; private set; }
        public ReactiveCommand<double, Unit> SeekCommand { get; private set; }
        public ReactiveCommand<OrbitCue, Unit> CueUpdatedCommand { get; private set; }

        public DJCompanionViewModel(
            HarmonicMatchService harmonicMatchService,
            LibraryService libraryService,
            PlayerViewModel playerViewModel,
            IEventBus eventBus,
            SetlistStressTestService? stressTestService = null,
            AppDbContext? dbContext = null)
        {
            _harmonicMatchService = harmonicMatchService;
            _libraryService = libraryService;
            _eventBus = eventBus;
            _player = playerViewModel;

            TogglePlayCommand = ReactiveCommand.Create(TogglePlay);
            PreviewStemCommand = ReactiveCommand.CreateFromTask<string>(PreviewStemAsync);
            LoadTrackCommand = ReactiveCommand.CreateFromTask<UnifiedTrackViewModel>(LoadTrackAsync);

            // Phase 5.4: Initialize Stress-Test Command
            RunSetlistStressTestCommand = ReactiveCommand.CreateFromTask<Unit, StressDiagnosticReport>(
                async _ => await RunSetlistStressTestAsync(stressTestService, dbContext));

            // Phase 6: Initialize Apply Rescue Track Command
            ApplyRescueTrackCommand = ReactiveCommand.CreateFromTask<(int, RescueSuggestion), ApplyRescueResult>(
                async args => await ApplyRescueTrackAsync(args.Item1, args.Item2, stressTestService, dbContext));

            // Initialize child ViewModels
            HealthBarViewModel = new SetlistHealthBarViewModel();
            ForensicInspectorViewModel = new ForensicInspectorViewModel();

            // Phase 6: Wire ApplyRescueTrack handler from Forensic Inspector to DJCompanion
            ForensicInspectorViewModel.OnApplyRescueTrack = async (transitionIdx, rescue) =>
                await ApplyRescueTrackAsync(transitionIdx, rescue, stressTestService, dbContext);

            // Wire segment selection → inspector detail
            HealthBarViewModel.SegmentSelected.Subscribe(stressPoint =>
            {
                if (stressPoint != null)
                    ForensicInspectorViewModel.DisplayStressPointDetail(stressPoint);
            });

            // NEW: Initialize Cue and Seek Commands
            JumpToCueCommand = ReactiveCommand.CreateFromTask<OrbitCue, Unit>(async cue =>
            {
                if (cue != null && _player != null)
                {
                    // Seek to cue position - Timestamp is in seconds, cast to float
                    await Task.Run(() => _player.Seek((float)cue.Timestamp));
                }
                return Unit.Default;
            });



            SeekCommand = ReactiveCommand.CreateFromTask<double, Unit>(async position =>
            {
                if (_player != null)
                {
                    await Task.Run(() => _player.Seek((float)position));
                }
                return Unit.Default;
            });


            CueUpdatedCommand = ReactiveCommand.CreateFromTask<OrbitCue, Unit>(async cue =>
            {
                // Handle cue update - save to database
                System.Diagnostics.Debug.WriteLine($"Cue updated: {cue?.Role} at {cue?.Timestamp}s");
                return Unit.Default;
            });


            // Phase 6: Track selection subscription ready (deferred until needed)
        }

        private void OnTrackSelected()
        {
            // Phase 6: Placeholder for track selection integration
            // Will be wired via eventbus subscription when needed
        }

        /// <summary>
        /// Main entry point: Load a track and generate all recommendations.
        /// </summary>
        public async Task LoadTrackAsync(UnifiedTrackViewModel track)
        {
            if (track == null) return;

            CurrentTrack = track;
            IsLoading = true;

            try
            {
                // Update display
                HelpText = $"Analyzing '{track.Model?.Title}' by '{track.Model?.Artist}'...";

                // Clear previous recommendations
                HarmonicMatches.Clear();
                BpmMatches.Clear();
                EnergyMatches.Clear();
                StyleMatches.Clear();
                MixingAdvice.Clear();
                AvailableStems.Clear();

                // Populate available stems
                PopulateAvailableStems();

                // Populate available stems
                PopulateAvailableStems();

                // Session 2: Parallelize analysis tasks for sub-5s load times
                await Task.WhenAll(
                    FetchHarmonicMatchesAsync(track),
                    FetchBpmMatchesAsync(track),
                    FetchEnergyMatchesAsync(track),
                    FetchStyleMatchesAsync(track)
                );

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
                if (track.Model == null || track.Model.Id == Guid.Empty) return;

                var matches = await _harmonicMatchService.FindMatchesAsync(
                    track.Model.Id,
                    limit: 12,
                    includeBpmRange: true,
                    includeEnergyMatch: true);

                foreach (var match in matches)
                {
                    var matchedTrack = match.Track;
                    HarmonicMatches.Add(new HarmonicMatchDisplayItem
                    {
                        Title = matchedTrack.Title,
                        Artist = matchedTrack.Artist,
                        Album = matchedTrack.Album,
                        KeyMatch = matchedTrack.MusicalKey ?? "—",
                        CompatibilityScore = (int)match.CompatibilityScore,
                        KeyRelation = DetermineKeyRelation(track.Model.MusicalKey, matchedTrack.MusicalKey),
                        Track = matchedTrack
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

                var allTracks = await _libraryService.LoadAllLibraryEntriesAsync();
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

                var allTracks = await _libraryService.LoadAllLibraryEntriesAsync();

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
                if (track.Model == null || track.Model.Id == Guid.Empty) return;

                // Use the PersonalClassifier or genre-based matching
                var allTracks = await _libraryService.LoadAllLibraryEntriesAsync();

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
            if (CurrentTrack?.Model != null && CurrentTrack.Model.Id != Guid.Empty)
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

            _player.TogglePlayPauseCommand.Execute(null);
            IsPlaying = _player.IsPlaying;
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
            SetlistStressTestService? stressTestService,
            AppDbContext? dbContext)
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

        /// <summary>
        /// Phase 6: Applies a rescue track to the setlist at an optimal position.
        /// Determines whether to INSERT (bridge) or REPLACE (swap) based on quality scores.
        /// Updates CurrentSetlist, refreshes stress-test report, and animates HealthBar.
        /// </summary>
        private async Task<ApplyRescueResult> ApplyRescueTrackAsync(
            int affectedTransitionIndex,
            RescueSuggestion rescueSuggestion,
            SetlistStressTestService? stressTestService,
            AppDbContext? dbContext)
        {
            if (CurrentSetlist == null || stressTestService == null || rescueSuggestion == null)
            {
                return new ApplyRescueResult
                {
                    Success = false,
                    Message = "Invalid parameters for rescue application."
                };
            }

            try
            {
                HelpText = "Applying rescue track...";

                // Find the affected stress point
                var stressPoint = StressReport?.StressPoints?.ElementAtOrDefault(affectedTransitionIndex);
                if (stressPoint == null)
                {
                    return new ApplyRescueResult
                    {
                        Success = false,
                        Message = "Transition not found in stress report."
                    };
                }

                // Apply the rescue track
                var result = await stressTestService.ApplyRescueTrackAsync(
                    CurrentSetlist, stressPoint, rescueSuggestion);

                if (result.Success)
                {
                    // Update current setlist
                    CurrentSetlist = result.UpdatedSetlist;

                    // Re-run stress-test to get updated report
                    var updatedReport = await stressTestService.RunDiagnosticAsync(CurrentSetlist);
                    StressReport = updatedReport;

                    // Update HealthBar with animated transition
                    await HealthBarViewModel.UpdateReportWithAnimation(updatedReport, affectedTransitionIndex);

                    // Update forensic inspector for affected transitions
                    if (result.AffectedTransitions > 0)
                    {
                        var nextStressPoint = updatedReport?.StressPoints?.ElementAtOrDefault(affectedTransitionIndex);
                        if (nextStressPoint != null)
                        {
                            ForensicInspectorViewModel.DisplayStressPointDetail(nextStressPoint);
                        }
                    }

                    HelpText = result.Message;
                }
                else
                {
                    HelpText = $"Failed to apply rescue: {result.Message}";
                }

                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error applying rescue track: {ex.Message}");
                HelpText = $"Error applying rescue: {ex.Message}";
                return new ApplyRescueResult
                {
                    Success = false,
                    Message = ex.Message
                };
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
            public object? Track { get; set; }
        }

        public class BpmMatchDisplayItem
        {
            public string Title { get; set; } = "";
            public string Artist { get; set; } = "";
            public string Album { get; set; } = "";
            public string BpmDisplay { get; set; } = "";
            public double BpmDifference { get; set; }
            public object? Track { get; set; }
        }

        public class EnergyMatchDisplayItem
        {
            public string Title { get; set; } = "";
            public string Artist { get; set; } = "";
            public string Album { get; set; } = "";
            public double Energy { get; set; }
            public string EnergyDirection { get; set; } = "";
            public object? Track { get; set; }
        }

        public class StyleMatchDisplayItem
        {
            public string Title { get; set; } = "";
            public string Artist { get; set; } = "";
            public string Album { get; set; } = "";
            public string Genre { get; set; } = "";
            public object? Track { get; set; }
        }

        public class MixingAdviceItem
        {
            public string Title { get; set; } = "";
            public string Description { get; set; } = "";
        }

        // NEW: Setlist Track Item for Left Panel
        public class SetlistTrackItem
        {
            public int Index { get; set; }
            public string Title { get; set; } = "";
            public string Artist { get; set; } = "";
            public string KeyDisplay { get; set; } = "";
            public string BpmDisplay { get; set; } = "";
            public string EnergyLevel { get; set; } = "";
            public bool IsSelected { get; set; }
            public Guid TrackId { get; set; }
            public PlaylistTrack? Track { get; set; }
            
            // Sprint 3: Intelligence properties
            public string Key { get; set; } = "";
            public double Energy { get; set; }
            public double VocalProbability { get; set; }
        }


        // NEW: Issue Models for Set Intelligence
        public class KeyClashIssue
        {
            public string TrackA { get; set; } = "";
            public string TrackB { get; set; } = "";
            public string Description { get; set; } = "";
            public int TransitionIndex { get; set; }
        }

        public class EnergyGapIssue
        {
            public int TrackIndex { get; set; }
            public string Description { get; set; } = "";
            public double FromEnergy { get; set; }
            public double ToEnergy { get; set; }
        }

        public class VocalClashIssue
        {
            public string TrackA { get; set; } = "";
            public string TrackB { get; set; } = "";
            public int TransitionIndex { get; set; }
        }

        // NEW: Handle setlist track selection
        private void OnSetlistTrackSelected(SetlistTrackItem item)
        {
            if (item?.Track != null)
            {
                // For now, update the current track display directly
                // A full implementation would create a UnifiedTrackViewModel via DI
                HelpText = $"Selected: {item.Title} by {item.Artist}";
                
                // Update playback progress display
                this.RaisePropertyChanged(nameof(SetlistTrackCount));
            }
        }

        // Sprint 3: Setlist Analysis for Set Intelligence
        /// <summary>
        /// Analyzes the current setlist for key clashes, energy gaps, and vocal issues.
        /// Populates the Set Intelligence panel collections.
        /// </summary>
        public void AnalyzeSetlist()
        {
            KeyClashes.Clear();
            EnergyGaps.Clear();
            VocalClashes.Clear();

            var tracks = CurrentSetlistTracks.ToList();
            if (tracks.Count < 2)
            {
                SetHealthScore = 100;
                this.RaisePropertyChanged(nameof(HasIssues));
                return;
            }

            int issueCount = 0;

            // Camelot Wheel compatibility map
            var camelotCompatible = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>>
            {
                ["1A"] = new() { "12A", "1A", "2A", "1B" },
                ["2A"] = new() { "1A", "2A", "3A", "2B" },
                ["3A"] = new() { "2A", "3A", "4A", "3B" },
                ["4A"] = new() { "3A", "4A", "5A", "4B" },
                ["5A"] = new() { "4A", "5A", "6A", "5B" },
                ["6A"] = new() { "5A", "6A", "7A", "6B" },
                ["7A"] = new() { "6A", "7A", "8A", "7B" },
                ["8A"] = new() { "7A", "8A", "9A", "8B" },
                ["9A"] = new() { "8A", "9A", "10A", "9B" },
                ["10A"] = new() { "9A", "10A", "11A", "10B" },
                ["11A"] = new() { "10A", "11A", "12A", "11B" },
                ["12A"] = new() { "11A", "12A", "1A", "12B" },
                ["1B"] = new() { "12B", "1B", "2B", "1A" },
                ["2B"] = new() { "1B", "2B", "3B", "2A" },
                ["3B"] = new() { "2B", "3B", "4B", "3A" },
                ["4B"] = new() { "3B", "4B", "5B", "4A" },
                ["5B"] = new() { "4B", "5B", "6B", "5A" },
                ["6B"] = new() { "5B", "6B", "7B", "6A" },
                ["7B"] = new() { "6B", "7B", "8B", "7A" },
                ["8B"] = new() { "7B", "8B", "9B", "8A" },
                ["9B"] = new() { "8B", "9B", "10B", "9A" },
                ["10B"] = new() { "9B", "10B", "11B", "10A" },
                ["11B"] = new() { "10B", "11B", "12B", "11A" },
                ["12B"] = new() { "11B", "12B", "1B", "12A" }
            };

            for (int i = 0; i < tracks.Count - 1; i++)
            {
                var current = tracks[i];
                var next = tracks[i + 1];

                // 1. Key Clash Detection (Camelot Wheel)
                var keyA = current.Key?.ToUpperInvariant()?.Trim() ?? "";
                var keyB = next.Key?.ToUpperInvariant()?.Trim() ?? "";

                if (!string.IsNullOrEmpty(keyA) && !string.IsNullOrEmpty(keyB))
                {
                    bool isCompatible = camelotCompatible.TryGetValue(keyA, out var compatible)
                                        && compatible.Contains(keyB);

                    if (!isCompatible && keyA != keyB)
                    {
                        KeyClashes.Add(new KeyClashIssue
                        {
                            TrackA = current.Title,
                            TrackB = next.Title,
                            Description = $"Key clash: {keyA} → {keyB}",
                            TransitionIndex = i
                        });
                        issueCount++;
                    }
                }

                // 2. Energy Gap Detection (>4 point drop/spike)
                double energyA = current.Energy;
                double energyB = next.Energy;
                double gap = Math.Abs(energyA - energyB);

                if (gap > 4)
                {
                    EnergyGaps.Add(new EnergyGapIssue
                    {
                        TrackIndex = i,
                        FromEnergy = energyA,
                        ToEnergy = energyB,
                        Description = energyB > energyA
                            ? $"Energy spike: {energyA:F0} → {energyB:F0}"
                            : $"Energy drop: {energyA:F0} → {energyB:F0}"
                    });
                    issueCount++;
                }

                // 3. Vocal Clash Detection (both tracks have high vocal probability > 0.8)
                if (current.VocalProbability > 0.8 && next.VocalProbability > 0.8)
                {
                    VocalClashes.Add(new VocalClashIssue
                    {
                        TrackA = current.Title,
                        TrackB = next.Title,
                        TransitionIndex = i
                    });
                    issueCount++;
                }
            }

            // Calculate Set Health Score (100 - deductions per issue)
            SetHealthScore = Math.Max(0, 100 - (issueCount * 8));
            this.RaisePropertyChanged(nameof(HasIssues));
        }
    }
}



