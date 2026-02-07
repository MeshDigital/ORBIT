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
        Services.Analysis.SetlistStressTestService stressTestService)
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
        
        // Initialize Deck B
        DeckB = new ForensicLabViewModel(_eventBus, _libraryService); 
        _cueEngine = new CueGenerationEngine(null!, null!);

        GenerateMikCuesCommand = ReactiveCommand.CreateFromTask(() => LoadTrackAsync(CurrentTrack?.UniqueHash ?? ""));
        PlayToneCommand = ReactiveCommand.Create<string>(note => _audioEngine.PlayTone(double.Parse(note)));
        LoadToDeckBCommand = ReactiveCommand.CreateFromTask<SonicMatch>(match => LoadDeckBAsync(match?.TrackUniqueHash ?? ""));
        ToggleMentorCommand = ReactiveCommand.Create(ToggleMentor);
        
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
