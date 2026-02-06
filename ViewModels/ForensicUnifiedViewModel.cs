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
using SLSKDONET.ViewModels.Stem;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Phase 25/MIK Parity: Unified Forensic Intelligence ViewModel.
/// Consolidates Forensic Lab, Stem Workspace, and AI Matching into a single mission control.
/// </summary>
public class ForensicUnifiedViewModel : ReactiveObject
{
    private readonly ForensicLabViewModel _forensicLab;
    private readonly StemWorkspaceViewModel _stemWorkspace;
    private readonly SonicMatchService _matchService;
    private readonly ILibraryService _libraryService;
    private readonly StemSeparationService _separationService;
    private readonly RealTimeStemEngine _audioEngine;

    public ForensicLabViewModel DeckA => _forensicLab;
    public ForensicLabViewModel DeckB { get; }
    public StemMixerViewModel StemMixer => _stemWorkspace.Mixer;
    private readonly CueGenerationEngine _cueEngine;

    public System.Windows.Input.ICommand GenerateMikCuesCommand { get; }
    public System.Windows.Input.ICommand PlayToneCommand { get; }
    public System.Windows.Input.ICommand LoadToDeckBCommand { get; }

    private ObservableCollection<SonicMatchResult> _aiMatches = new();
    public ObservableCollection<SonicMatchResult> AiMatches
    {
        get => _aiMatches;
        set => this.RaiseAndSetIfChanged(ref _aiMatches, value);
    }

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
            _audioEngine.CrossfaderValue = (float)value;
        }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    public ForensicUnifiedViewModel(
        ForensicLabViewModel forensicLab,
        StemWorkspaceViewModel stemWorkspace,
        SonicMatchService matchService,
        ILibraryService libraryService,
        StemSeparationService separationService,
        RealTimeStemEngine audioEngine)
    {
        _forensicLab = forensicLab;
        _stemWorkspace = stemWorkspace;
        _matchService = matchService;
        _libraryService = libraryService;
        _audioEngine = audioEngine;
        _cueEngine = new CueGenerationEngine(null!, null!);

        GenerateMikCuesCommand = ReactiveCommand.CreateFromTask(() => LoadTrackAsync(CurrentTrack?.UniqueHash ?? ""));
        PlayToneCommand = ReactiveCommand.Create<string>(note => _audioEngine.PlayTone(double.Parse(note)));
        LoadToDeckBCommand = ReactiveCommand.CreateFromTask<SonicMatchResult>(match => LoadDeckBAsync(match?.Track.UniqueHash ?? ""));

        // Create Deck B independently
        DeckB = new ForensicLabViewModel(null!, libraryService);
        _separationService = separationService; // Fix unassigned field

        GenerateMikCuesCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            if (CurrentTrack == null) return;
            var cues = await _cueEngine.GenerateMikStandardCues(CurrentTrack.UniqueHash);
            _forensicLab.Cues = cues;
        });

        PlayToneCommand = ReactiveCommand.Create<double>(freq => _audioEngine.PlayRootTone(freq));

        LoadToDeckBCommand = ReactiveCommand.CreateFromTask<SonicMatchResult>(async match => 
        {
            if (match == null) return;
            await LoadDeckBAsync(match.Track.UniqueHash);
        });
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

    public async Task LoadDeckBAsync(string trackHash)
    {
        if (string.IsNullOrEmpty(trackHash)) return;

        try
        {
            var entry = await _libraryService.FindLibraryEntryAsync(trackHash);
            if (entry == null) return;

            await DeckB.LoadTrackAsync(trackHash);

            // Load Stems for Deck B into engine
            var stems = await _separationService.SeparateTrackAsync(entry.FilePath!, trackHash);
            _audioEngine.LoadDeckStems(Deck.B, stems);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load Deck B: {ex.Message}");
        }
    }

    private async Task UpdateRecommendationsAsync(LibraryEntryEntity source)
    {
        var matches = await _matchService.GetMatchesAsync(source, limit: 12);
        AiMatches.Clear();
        foreach (var m in matches)
        {
            AiMatches.Add(m);
        }
    }
}
