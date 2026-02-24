using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using ReactiveUI;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data.Essentia;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Features.LibrarySidebar.ViewModels;

/// <summary>
/// Orchestrator for the Cue & Phrase Inspection Panel.
/// Handles track hydration, structure analysis triggering, and live playback synchronization.
/// </summary>
public class CueSidebarViewModel : ReactiveObject, ISidebarContent, IDisposable
{
    private readonly ILibraryService _libraryService;
    private readonly SLSKDONET.Services.Audio.PhraseDetectionService _phraseDetectionService;
    private readonly IAudioPlayerService _playerService;
    private readonly IEventBus _eventBus;
    private readonly CompositeDisposable _disposables = new();

    private PlaylistTrackViewModel? _currentTrack;
    private float _currentBpm;
    private bool _isAnalyzing;
    private bool _hasPhrases;

    public ObservableCollection<PhraseItemViewModel> Phrases { get; } = new();
    public ObservableCollection<CueItemViewModel> Cues { get; } = new();

    /// <summary>
    /// Indicates if a structural analysis is currently in progress.
    /// </summary>
    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set => this.RaiseAndSetIfChanged(ref _isAnalyzing, value);
    }

    /// <summary>
    /// True if the track has structural mapping data available.
    /// </summary>
    public bool HasPhrases
    {
        get => _hasPhrases;
        private set => this.RaiseAndSetIfChanged(ref _hasPhrases, value);
    }

    public ICommand TriggerAnalysisCommand { get; }
    public ICommand SeekToPhraseCommand { get; }

    public CueSidebarViewModel(
        ILibraryService libraryService,
        SLSKDONET.Services.Audio.PhraseDetectionService phraseDetectionService,
        IAudioPlayerService playerService,
        IEventBus eventBus)
    {
        _libraryService = libraryService;
        _phraseDetectionService = phraseDetectionService;
        _playerService = playerService;
        _eventBus = eventBus;

        // Command to trigger structural analysis if missing
        TriggerAnalysisCommand = ReactiveCommand.CreateFromTask(
            TriggerAnalysisAsync, 
            this.WhenAnyValue(x => x.IsAnalyzing).Select(x => !x));

        // Command to seek player to a specific phrase
        SeekToPhraseCommand = ReactiveCommand.Create<PhraseItemViewModel>(phrase => 
        {
            if (_playerService.Duration > 0)
            {
                _playerService.Position = (float)(phrase.StartTimeSeconds / _playerService.Duration);
            }
        });

        // Task 2: Subscribing to PositionChanged for live "Active Phrase" tracking
        Observable.FromEventPattern<EventHandler<float>, float>(
            h => _playerService.PositionChanged += h,
            h => _playerService.PositionChanged -= h)
            .Subscribe(_ => UpdateActivePhrase())
            .DisposeWith(_disposables);

        // Task 2: Listen for AnalysisCompletedEvent to refresh UI instantly
        _eventBus.GetEvent<AnalysisCompletedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async e => {
                if (_currentTrack != null && e.TrackHash == _currentTrack.GlobalId)
                {
                    await HydrateAsync(_currentTrack);
                }
            })
            .DisposeWith(_disposables);
    }

    public async Task ActivateAsync(PlaylistTrackViewModel track)
    {
        _currentTrack = track;
        await HydrateAsync(track);
    }

    public Task ActivateBulkAsync(IReadOnlyList<PlaylistTrackViewModel> tracks) => Task.CompletedTask;

    public void Deactivate()
    {
        _currentTrack = null;
        Phrases.Clear();
        Cues.Clear();
    }

    /// <summary>
    /// Loads phrases and cues from the database for the active track.
    /// </summary>
    private async Task HydrateAsync(PlaylistTrackViewModel track)
    {
        Phrases.Clear();
        Cues.Clear();
        
        var features = await _libraryService.GetAudioFeaturesByHashAsync(track.GlobalId);
        _currentBpm = features?.Bpm ?? 0;

        // Load structural phrases
        var phraseEntities = await _libraryService.GetPhrasesByHashAsync(track.GlobalId);
        if (phraseEntities != null)
        {
            foreach (var p in phraseEntities)
            {
                Phrases.Add(new PhraseItemViewModel(p, _currentBpm));
            }
        }

        HasPhrases = Phrases.Any();

        // Load Cue Points (HotCues & Memory Cues)
        if (features != null && !string.IsNullOrEmpty(features.CuePointsJson))
        {
            try
            {
                var orbitCues = JsonSerializer.Deserialize<List<OrbitCue>>(features.CuePointsJson) ?? new();
                foreach (var c in orbitCues)
                {
                    Cues.Add(new CueItemViewModel(c));
                }
            }
            catch { /* Ignore parse errors for malformed JSON */ }
        }

        UpdateActivePhrase();
    }

    /// <summary>
    /// Synchronizes the IsActive state of phrases with the current player position.
    /// </summary>
    private void UpdateActivePhrase()
    {
        // PlayerService.Time is in milliseconds
        double currentTime = _playerService.Time / 1000.0;
        foreach (var p in Phrases)
        {
            p.IsActive = currentTime >= p.StartTimeSeconds && currentTime < p.EndTimeSeconds;
        }
    }

    /// <summary>
    /// Manually triggers structural analysis for the current track.
    /// Uses Essentia-extracted waveform data if available, otherwise requests full re-analysis.
    /// </summary>
    private async Task TriggerAnalysisAsync()
    {
        if (_currentTrack == null) return;
        
        IsAnalyzing = true;
        try
        {
            var entry = await _libraryService.GetTrackEntityByHashAsync(_currentTrack.GlobalId);

            if (entry != null && entry.WaveformData != null && _currentBpm > 0)
            {
                // Run localized phrase detection using existing waveform peaks
                bool success = await _phraseDetectionService.DetectPhrasesAsync(_currentTrack.GlobalId);

                if (success)
                {
                    // The service already saves to DB, we just need to re-hydrate the UI
                    await HydrateAsync(_currentTrack);
                }
            }
            else
            {
                // Request structural re-analysis via the background queue
                _eventBus.Publish(new TrackAnalysisRequestedEvent(_currentTrack.GlobalId, AnalysisTier.Tier2));
            }
        }
        catch (Exception)
        {
            // Error handling handled by the service layer
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}
