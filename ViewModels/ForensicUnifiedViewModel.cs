using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Models.Stem;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Musical;
using SLSKDONET.Services.AI;
using SLSKDONET.ViewModels.Stem;
using Microsoft.Extensions.Logging;
using SLSKDONET.Services.Export;
using SLSKDONET.Services.IO;
using SLSKDONET.Views;
using SLSKDONET.Data.Essentia;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Phase 25/MIK Parity: Unified Forensic Intelligence ViewModel.
/// Consolidates Forensic Lab, Stem Workspace, and AI Matching into a single mission control.
/// </summary>
public class ForensicUnifiedViewModel : ReactiveObject
{
    private readonly IEventBus _eventBus;
    private readonly ForensicLabViewModel _forensicLab;
    private readonly StemWorkspaceViewModel _stemWorkspace;
    private readonly ISonicMatchService _matchService;
    private readonly ILibraryService _libraryService;
    private readonly StemSeparationService _separationService;
    private readonly RealTimeStemEngine _audioEngine;
    private readonly MultiTrackEngine _multiTrackEngine;
    private readonly PlayerViewModel _playerViewModel;
    private readonly Services.Analysis.SetlistStressTestService _stressTestService;
    private readonly ITaggerService _taggerService;
    private readonly IRekordboxExportService _exportService;
    private readonly IAudioIntelligenceService _intelligenceService;
    private readonly INotificationService _notificationService;

    public ForensicLabViewModel DeckA => _forensicLab;
    public ForensicLabViewModel DeckB { get; }
    public StemMixerViewModel StemMixer => _stemWorkspace.Mixer;
    private readonly CueGenerationEngine _cueEngine;

    public System.Windows.Input.ICommand GenerateMikCuesCommand { get; }
    public System.Windows.Input.ICommand PlayToneCommand { get; }
    public System.Windows.Input.ICommand LoadToDeckBCommand { get; }
    public System.Windows.Input.ICommand ToggleMentorCommand { get; }
    public System.Windows.Input.ICommand FindBridgeCommand { get; }
    public System.Windows.Input.ICommand NavigateToTrackCommand { get; }
    public System.Windows.Input.ICommand UpdateCueCommand { get; }
    public System.Windows.Input.ICommand UpdateSegmentCommand { get; }
    public System.Windows.Input.ICommand ToggleInstOnlyCommand { get; }
    public System.Windows.Input.ICommand CommitMetadataCommand { get; }

    public ObservableCollection<SonicMatch> AiMatches { get; } = new();

    private LibraryEntryEntity? _currentTrack;
    public LibraryEntryEntity? CurrentTrack
    {
        get => _currentTrack;
        set => this.RaiseAndSetIfChanged(ref _currentTrack, value);
    }

    private int _energyScore;
    public int EnergyScore
    {
        get => _energyScore;
        set => this.RaiseAndSetIfChanged(ref _energyScore, value);
    }

    private bool _analyzeHarmonicsInstOnly;
    public bool AnalyzeHarmonicsInstOnly
    {
        get => _analyzeHarmonicsInstOnly;
        set 
        {
            if (this.RaiseAndSetIfChanged(ref _analyzeHarmonicsInstOnly, value))
                _ = RunInstrumentalAnalysisAsync();
        }
    }

    private string? _eclipseKey;
    public string? EclipseKey
    {
        get => _eclipseKey;
        set => this.RaiseAndSetIfChanged(ref _eclipseKey, value);
    }

    private float _eclipseConfidence;
    public float EclipseConfidence
    {
        get => _eclipseConfidence;
        set => this.RaiseAndSetIfChanged(ref _eclipseConfidence, value);
    }

    private bool _showEclipseBadge;
    public bool ShowEclipseBadge
    {
        get => _showEclipseBadge;
        set => this.RaiseAndSetIfChanged(ref _showEclipseBadge, value);
    }

    private double _crossfaderPosition = 0.5;
    public double CrossfaderPosition
    {
        get => _crossfaderPosition;
        set
        {
            this.RaiseAndSetIfChanged(ref _crossfaderPosition, value);
            _multiTrackEngine.CrossfaderPosition = (float)value;
            _audioEngine.CrossfaderValue = (float)value; // Synchronize with Stem Engine
        }
    }

    private bool _isMentorOpen = true;
    public bool IsMentorOpen
    {
        get => _isMentorOpen;
        set => this.RaiseAndSetIfChanged(ref _isMentorOpen, value);
    }

    private ObservableCollection<LibraryEntryEntity> _flowSetlist = new();
    public ObservableCollection<LibraryEntryEntity> FlowSetlist
    {
        get => _flowSetlist;
        set => this.RaiseAndSetIfChanged(ref _flowSetlist, value);
    }

    private ObservableCollection<ForensicVerdictEntry> _verdictEntries = new();
    public ObservableCollection<ForensicVerdictEntry> VerdictEntries
    {
        get => _verdictEntries;
        set => this.RaiseAndSetIfChanged(ref _verdictEntries, value);
    }

    private string _mentorVerdict = "ANALYZING...";
    public string MentorVerdict
    {
        get => _mentorVerdict;
        set => this.RaiseAndSetIfChanged(ref _mentorVerdict, value);
    }

    public void ToggleMentor() => IsMentorOpen = !IsMentorOpen;

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    public ForensicUnifiedViewModel(
        IEventBus eventBus,
        ForensicLabViewModel forensicLab,
        StemWorkspaceViewModel stemWorkspace,
        ISonicMatchService matchService,
        ILibraryService libraryService,
        StemSeparationService separationService,
        RealTimeStemEngine audioEngine,
        MultiTrackEngine multiTrackEngine,
        PlayerViewModel playerViewModel,
        Services.Analysis.SetlistStressTestService stressTestService,
        ITaggerService taggerService,
        IRekordboxExportService exportService,
        IAudioIntelligenceService intelligenceService,
        INotificationService notificationService,
        ILoggerFactory loggerFactory)
    {
        _eventBus = eventBus;
        _forensicLab = forensicLab;
        _stemWorkspace = stemWorkspace;
        _matchService = matchService;
        _libraryService = libraryService;
        _separationService = separationService;
        _audioEngine = audioEngine;
        _multiTrackEngine = multiTrackEngine;
        _playerViewModel = playerViewModel;
        _stressTestService = stressTestService;
        _taggerService = taggerService;
        _exportService = exportService;
        _intelligenceService = intelligenceService;
        _notificationService = notificationService;
        
        // Initialize Deck B
        DeckB = new ForensicLabViewModel(_eventBus, _libraryService); 
        _cueEngine = new CueGenerationEngine(loggerFactory.CreateLogger<CueGenerationEngine>(), null);

        GenerateMikCuesCommand = ReactiveCommand.CreateFromTask(() => LoadTrackAsync(CurrentTrack?.UniqueHash ?? ""));
        PlayToneCommand = ReactiveCommand.Create<string>(note => _audioEngine.PlayTone(double.Parse(note)));
        LoadToDeckBCommand = ReactiveCommand.CreateFromTask<SonicMatch>(match => LoadDeckBAsync(match?.TrackUniqueHash ?? ""));
        ToggleMentorCommand = ReactiveCommand.Create(ToggleMentor);
        ToggleInstOnlyCommand = ReactiveCommand.Create(() => AnalyzeHarmonicsInstOnly = !AnalyzeHarmonicsInstOnly);
        
        UpdateCueCommand = ReactiveCommand.CreateFromTask<OrbitCue>(async cue => 
        {
            if (CurrentTrack == null) return;
            // Persistence logic (Phase 2)
            await SaveCuesToDbAsync(CurrentTrack.UniqueHash, _forensicLab.Cues?.ToList() ?? new());
        });

        UpdateSegmentCommand = ReactiveCommand.CreateFromTask<PhraseSegment>(async seg => 
        {
            // Placeholder for structural segment updates
        });

        CommitMetadataCommand = ReactiveCommand.CreateFromTask(CommitMetadataAsync);
        
        FindBridgeCommand = ReactiveCommand.CreateFromTask<LibraryEntryEntity>(async (track, ct) => 
        {
            if (track == null || _playerViewModel.Queue == null) return;
            
            // 1. Find the next track in the queue
            var currentIndex = _playerViewModel.Queue.IndexOf(_playerViewModel.Queue.FirstOrDefault(pt => pt.GlobalId == track.UniqueHash));
            if (currentIndex == -1 || currentIndex >= _playerViewModel.Queue.Count - 1) return;
            
            var trackA = track;
            var trackBViewModel = _playerViewModel.Queue[currentIndex + 1];
            var trackB = await _libraryService.GetTrackEntityByHashAsync(trackBViewModel.GlobalId);

            if (trackB == null) return;

            IsBusy = true;
            try 
            {
                var bridges = await _matchService.FindBridgeAsync(trackA, trackB);
                var bridge = bridges.FirstOrDefault();
                if (bridge != null)
                {
                    // Create PlaylistTrack manually
                    var bridgePt = new PlaylistTrack
                    {
                        TrackUniqueHash = bridge.TrackUniqueHash,
                        Artist = bridge.Artist,
                        Title = bridge.Title,
                        Status = TrackStatus.Downloaded,
                        PlaylistId = trackBViewModel.SourceId
                    };
                    
                    _playerViewModel.Queue.Insert(currentIndex + 1, new PlaylistTrackViewModel(bridgePt));
                    System.Diagnostics.Debug.WriteLine($"Inserted bridge: {bridge.Title}");
                }
            }
            finally 
            {
                IsBusy = false;
            }
        });

        NavigateToTrackCommand = ReactiveCommand.CreateFromTask<LibraryEntryEntity>(track => LoadTrackAsync(track?.UniqueHash ?? ""));


        // Synchronize FlowSetlist with Player Queue
        // In a real scenario, we'd want to map or proxy the collection. 
        // For MVP, we'll listen to changes.
        SyncFlowSetlist();
        _playerViewModel.Queue.CollectionChanged += (s, e) => SyncFlowSetlist();

        GenerateMikCuesCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            if (CurrentTrack == null) return;
            var cues = await _cueEngine.GenerateMikStandardCues(CurrentTrack.UniqueHash);
            _forensicLab.Cues = cues;
        });

        PlayToneCommand = ReactiveCommand.Create<double>(freq => _audioEngine.PlayRootTone(freq));

        LoadToDeckBCommand = ReactiveCommand.CreateFromTask<SonicMatch>(async match => 
        {
            if (match == null) return;
            await LoadDeckBAsync(match.TrackUniqueHash);
        });
    }

    private async Task SaveCuesToDbAsync(string hash, List<OrbitCue> cues)
    {
         using var db = new AppDbContext();
         var entry = await db.LibraryEntries.FirstOrDefaultAsync(e => e.UniqueHash == hash);
         if (entry != null)
         {
             entry.CuePointsJson = System.Text.Json.JsonSerializer.Serialize(cues);
             await db.SaveChangesAsync();
             _eventBus.Publish(new TrackMetadataUpdatedEvent(hash));
         }
    }

    private void SyncFlowSetlist()
    {
        FlowSetlist.Clear();
        if (_playerViewModel.Queue != null)
        {
            // This is problematic because ToEntity() doesn't exist.
            // We'll just ignore for now or implement a lazy load.
            // For UI display in Flow Strip, we might just need a thin DTO or the Model itself.
            // Let's use the Model from the ViewModel.
            foreach (var ptvm in _playerViewModel.Queue)
            {
                 // We need LibraryEntryEntity for the FlowSetlist observable collection
                 // Maybe we should change the collection type or just fetch them.
                 // For now, let's just add a placeholder or a simple mapping.
                 var entry = new LibraryEntryEntity 
                 { 
                     UniqueHash = ptvm.GlobalId, 
                     Title = ptvm.Title, 
                     Artist = ptvm.Artist,
                     MusicalKey = ptvm.Model.MusicalKey
                 };
                 FlowSetlist.Add(entry);
            }
        }
    }

    /// <summary>
    /// Loads a track into Deck A (the primary focus).
    /// </summary>
    public async Task LoadTrackAsync(string trackHash)
    {
        if (string.IsNullOrEmpty(trackHash)) return;
        
        IsBusy = true;
        try 
        {
            using var db = new SLSKDONET.Data.AppDbContext();
            CurrentTrack = await db.LibraryEntries.Include(le => le.AudioFeatures).FirstOrDefaultAsync(le => le.UniqueHash == trackHash);
            if (CurrentTrack == null) return;

            // 1. Forensic Lab (Deck A)
            await _forensicLab.LoadTrackAsync(trackHash);

            // 2. Load Stems for Deck A into engine
            var stems = await _separationService.SeparateTrackAsync(CurrentTrack.FilePath!, trackHash);
            _audioEngine.LoadDeckStems(Deck.A, stems);

            // Update Local Energy and CurrentTrack
            // 3. Update Recommendations
            await UpdateRecommendationsAsync(CurrentTrack);
            
            if (CurrentTrack.AudioFeatures != null)
            {
                EnergyScore = CurrentTrack.AudioFeatures.EnergyScore > 0 ? CurrentTrack.AudioFeatures.EnergyScore : 5;
            }

            await UpdateMentorAdviceAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load Deck A: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadDeckBAsync(string trackHash)
    {
        if (string.IsNullOrEmpty(trackHash)) return;

        try
        {
            var entry = await _libraryService.GetTrackEntityByHashAsync(trackHash);
            if (entry != null)
            {
                await DeckB.LoadTrackAsync(trackHash);

                // Load Stems for Deck B into engine
                var stems = await _separationService.SeparateTrackAsync(entry.FilePath!, trackHash);
                _audioEngine.LoadDeckStems(Deck.B, stems);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load Deck B: {ex.Message}");
        }
    }

    private async Task RunInstrumentalAnalysisAsync()
    {
        if (CurrentTrack == null || !AnalyzeHarmonicsInstOnly)
        {
            ShowEclipseBadge = false;
            return;
        }

        IsBusy = true;
        try
        {
            // Find instrumental stem
            var stems = _separationService.GetStemPaths(CurrentTrack.UniqueHash);
            var instPath = stems.TryGetValue(StemType.Other, out var path) ? path : null; 

            if (instPath == null || !File.Exists(instPath))
            {
                _notificationService.Show("Instrumental stem missing. Run separation first.", "Eclipse Mode", NotificationType.Warning);
                return;
            }

            var features = await _intelligenceService.AnalyzeTrackAsync(instPath, CurrentTrack.UniqueHash + "_INST", tier: AnalysisTier.Tier1);
            if (features != null)
            {
                EclipseKey = features.CamelotKey;
                EclipseConfidence = features.KeyConfidence;
                ShowEclipseBadge = EclipseKey != DeckA.CamelotKey;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Instrumental analysis failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CommitMetadataAsync()
    {
        if (CurrentTrack == null) return;

        IsBusy = true;
        try
        {
            // 1. Tag ID3 using TaggerService
            // Note: We need to map LibraryEntryEntity back to a model the tagger understands
            var trackModel = new Track 
            {
                Title = CurrentTrack.Title,
                Artist = CurrentTrack.Artist,
                Metadata = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["MusicalKey"] = DeckA.CamelotKey,
                    ["Comment"] = $"[Energy {DeckA.EnergyScore}] {(ShowEclipseBadge ? "[Eclipse Match]" : "")}"
                }
            };
            
            bool tagged = await _taggerService.TagFileAsync(trackModel, CurrentTrack.FilePath!);
            
            // 2. Update Rekordbox XML
            bool xmlUpdated = await _exportService.UpdateTrackInXmlAsync(CurrentTrack.UniqueHash); 

            if (tagged && xmlUpdated)
            {
                _notificationService.Show("Metadata committed to tags & Rekordbox", "Commit Success", NotificationType.Success);
                _eventBus.Publish(new TrackMetadataUpdatedEvent(CurrentTrack.UniqueHash));
            }
        }
        catch (Exception ex)
        {
             _notificationService.Show($"Commit failed: {ex.Message}", "Error", NotificationType.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UpdateRecommendationsAsync(LibraryEntryEntity source)
    {
        var matches = await _matchService.FindSonicMatchesAsync(CurrentTrack.UniqueHash);
        AiMatches.Clear();
        foreach (var m in matches)
        {
            AiMatches.Add(m);
        }
    }

    private async Task UpdateMentorAdviceAsync()
    {
        if (CurrentTrack == null) return;

        VerdictEntries.Clear();
        VerdictEntries.Add(ForensicVerdictEntry.Section("Current Trajectory"));

        if (_playerViewModel.Queue != null && _playerViewModel.Queue.Count > 0)
        {
            var currentIndex = _playerViewModel.Queue.IndexOf(_playerViewModel.Queue.FirstOrDefault(pt => pt.GlobalId == CurrentTrack.UniqueHash));
            if (currentIndex != -1 && currentIndex < _playerViewModel.Queue.Count - 1)
            {
                var nextTrackVm = _playerViewModel.Queue[currentIndex + 1];
                var nextTrack = await _libraryService.GetTrackEntityByHashAsync(nextTrackVm.GlobalId);
                
                if (nextTrack != null)
                {
                    var advice = await _stressTestService.AnalyzeTransitionAsync(CurrentTrack, nextTrack);

                    VerdictEntries.Add(ForensicVerdictEntry.Bullet($"Next: {nextTrack.Title}"));
                
                if (advice.SeverityScore > 60)
                {
                    VerdictEntries.Add(ForensicVerdictEntry.Warning($"{advice.PrimaryFailure}: {advice.FailureReasoning}"));
                    MentorVerdict = "CAUTION: TRANSITION RISK";
                }
                else
                {
                    VerdictEntries.Add(ForensicVerdictEntry.Success("Energy & Harmonic match confirmed."));
                    MentorVerdict = "STABLE FLOW DETECTED";
                }
                
                    VerdictEntries.Add(ForensicVerdictEntry.Detail($"Transition Quality: {100 - advice.SeverityScore}%"));
                }
            }
            else
            {
                VerdictEntries.Add(ForensicVerdictEntry.Bullet("End of setlist reached."));
                MentorVerdict = "TRAJECTORY COMPLETE";
            }
        }

        VerdictEntries.Add(ForensicVerdictEntry.Section("Sonic Intelligence"));
        VerdictEntries.Add(ForensicVerdictEntry.Bullet($"Energy Level: {EnergyScore}/10"));
        
        if (EnergyScore >= 8) VerdictEntries.Add(ForensicVerdictEntry.Warning("HIGH ENERGY PLATEAU RISK"));
        else if (EnergyScore <= 3) VerdictEntries.Add(ForensicVerdictEntry.Detail("Atmospheric build potential"));

        VerdictEntries.Add(ForensicVerdictEntry.Verdict(MentorVerdict));
    }
}
