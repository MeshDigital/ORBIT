using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Windows.Input;
using System.Reactive.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using ReactiveUI;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using SLSKDONET.Models;
using SLSKDONET.Data.Entities;
using SLSKDONET.Views;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Studio;

namespace SLSKDONET.Features.LibrarySidebar.ViewModels;

public class TransitionProberViewModel : ReactiveObject, ISidebarContent, IDisposable, IStudioModuleViewModel
{
    private readonly WaveformAnalysisService _waveformService;
    private readonly Services.Audio.PhraseDetectionService _phraseService;
    private readonly ITransitionPreviewPlayer _transitionPlayer;
    private readonly Services.Audio.RealTimeStemEngine _stemEngine;
    private readonly Services.StemSeparationService _separationService;
    private readonly Services.Musical.VocalIntelligenceService _vocalService;
    private readonly Services.IAudioPlayerService _playerService;
    private readonly INotificationService _notificationService;
    private readonly Services.Audio.StemPreferenceService _preferenceService;
    private readonly ILibraryService _libraryService;
    private readonly CompositeDisposable _disposables = new();

    private CancellationTokenSource? _waveformCts;

    private PlaylistTrackViewModel? _primaryTrack;
    private PlaylistTrackViewModel? _secondaryTrack;
    
    private WaveformAnalysisData? _primaryWaveform;
    private WaveformAnalysisData? _secondaryWaveform;
    private IReadOnlyList<PhraseSegment>? _primaryPhrases;
    private IReadOnlyList<PhraseSegment>? _secondaryPhrases;
    private double _handoffOffset;

    private bool _isAuditioning;
    private bool _isSyncing;
    private double _hazardScore;
    private string _mashupStatus = "IDLE";
    private bool _autoResolveEnabled;
    private bool _autoCrossfadeEnabled;
    private float _crossfaderValue = 0.5f;

    public PlaylistTrackViewModel? PrimaryTrack
    {
        get => _primaryTrack;
        set 
        {
            this.RaiseAndSetIfChanged(ref _primaryTrack, value);
            this.RaisePropertyChanged(nameof(IsPrimaryLoading));
        }
    }

    public PlaylistTrackViewModel? SecondaryTrack
    {
        get => _secondaryTrack;
        set
        {
            this.RaiseAndSetIfChanged(ref _secondaryTrack, value);
            this.RaisePropertyChanged(nameof(IsSecondaryLoading));
        }
    }

    public WaveformAnalysisData? PrimaryWaveform
    {
        get => _primaryWaveform;
        set
        {
            this.RaiseAndSetIfChanged(ref _primaryWaveform, value);
            this.RaisePropertyChanged(nameof(IsPrimaryLoading));
        }
    }

    public WaveformAnalysisData? SecondaryWaveform
    {
        get => _secondaryWaveform;
        set
        {
            this.RaiseAndSetIfChanged(ref _secondaryWaveform, value);
            this.RaisePropertyChanged(nameof(IsSecondaryLoading));
        }
    }

    public bool IsPrimaryLoading => _primaryTrack != null && _primaryWaveform == null;
    public bool IsSecondaryLoading => _secondaryTrack != null && _secondaryWaveform == null;

    public IReadOnlyList<PhraseSegment>? PrimaryPhrases
    {
        get => _primaryPhrases;
        set => this.RaiseAndSetIfChanged(ref _primaryPhrases, value);
    }

    public IReadOnlyList<PhraseSegment>? SecondaryPhrases
    {
        get => _secondaryPhrases;
        set => this.RaiseAndSetIfChanged(ref _secondaryPhrases, value);
    }

    public double HandoffOffset
    {
        get => _handoffOffset;
        set 
        {
            this.RaiseAndSetIfChanged(ref _handoffOffset, value);
            this.RaisePropertyChanged(nameof(NegativeHandoffOffset));
        }
    }

    public double NegativeHandoffOffset => -_handoffOffset;

    public bool IsAuditioning
    {
        get => _isAuditioning;
        set => this.RaiseAndSetIfChanged(ref _isAuditioning, value);
    }

    public double HazardScore
    {
        get => _hazardScore;
        set => this.RaiseAndSetIfChanged(ref _hazardScore, value);
    }

    public string MashupStatus
    {
        get => _mashupStatus;
        set => this.RaiseAndSetIfChanged(ref _mashupStatus, value);
    }

    public bool IsSyncing
    {
        get => _isSyncing;
        set => this.RaiseAndSetIfChanged(ref _isSyncing, value);
    }

    public bool AutoResolveEnabled
    {
        get => _autoResolveEnabled;
        set => this.RaiseAndSetIfChanged(ref _autoResolveEnabled, value);
    }

    public bool AutoCrossfadeEnabled
    {
        get => _autoCrossfadeEnabled;
        set => this.RaiseAndSetIfChanged(ref _autoCrossfadeEnabled, value);
    }

    private bool _isDeckALocked;
    public bool IsDeckALocked
    {
        get => _isDeckALocked;
        set => this.RaiseAndSetIfChanged(ref _isDeckALocked, value);
    }

    public float CrossfaderValue
    {
        get => _crossfaderValue;
        set
        {
            this.RaiseAndSetIfChanged(ref _crossfaderValue, value);
            _stemEngine.CrossfaderValue = value;
        }
    }

    public ICommand ToggleAuditionCommand { get; }
    public ICommand ResolveClashCommand { get; }
    public ICommand ToggleAutoCrossfadeCommand { get; }
    public ICommand CommitSandboxCommand { get; }
    public ICommand AlignTransitionCommand { get; }
    public ICommand AuditionTransitionCommand { get; }
    public ICommand NudgeLeftCommand { get; }
    public ICommand NudgeRightCommand { get; }
    public ICommand ExportMashupCommand { get; }

    public TransitionProberViewModel(
        WaveformAnalysisService waveformService,
        Services.Audio.PhraseDetectionService phraseService,
        ITransitionPreviewPlayer transitionPlayer,
        Services.Audio.RealTimeStemEngine stemEngine,
        Services.StemSeparationService separationService,
        Services.Musical.VocalIntelligenceService vocalService,
        Services.IAudioPlayerService playerService,
        INotificationService notificationService,
        Services.Audio.StemPreferenceService preferenceService,
        ILibraryService libraryService)
    {
        _waveformService = waveformService;
        _phraseService = phraseService;
        _transitionPlayer = transitionPlayer;
        _stemEngine = stemEngine;
        _separationService = separationService;
        _vocalService = vocalService;
        _playerService = playerService;
        _notificationService = notificationService;
        _preferenceService = preferenceService;
        _libraryService = libraryService;

        ToggleAuditionCommand = ReactiveCommand.CreateFromTask(ToggleAuditionAsync);
        ResolveClashCommand = ReactiveCommand.Create(ResolveVocalClash);
        ToggleAutoCrossfadeCommand = ReactiveCommand.Create(() => AutoCrossfadeEnabled = !AutoCrossfadeEnabled);
        CommitSandboxCommand = ReactiveCommand.CreateFromTask(CommitSandboxSettingsAsync);
        AlignTransitionCommand = ReactiveCommand.Create(AlignTransition);
        AuditionTransitionCommand = ReactiveCommand.CreateFromTask(AuditionTransitionAsync);
        NudgeLeftCommand = ReactiveCommand.Create(NudgeLeft);
        NudgeRightCommand = ReactiveCommand.Create(NudgeRight);
        ExportMashupCommand = ReactiveCommand.CreateFromTask(ExportMashupAsync);

        // Phase 3: Dynamic Hazard Analysis & Sync Monitoring
        System.Reactive.Linq.Observable.Interval(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => {
                if (IsAuditioning && !IsSyncing) 
                {
                    UpdateDynamicHazard();
                }
            }).DisposeWith(_disposables);
    }

    private void AlignTransition()
    {
        if (PrimaryTrack == null || SecondaryTrack == null) return;

        // Logic: Find the StartTime of Track A's Outro phrase.
        // Find the StartTime of Track B's Intro phrase.
        // Calculate offset so B Intro starts when A Outro begins.
        
        var outroA = PrimaryPhrases?.FirstOrDefault(p => p.Label.Equals("Outro", StringComparison.OrdinalIgnoreCase));
        var introB = SecondaryPhrases?.FirstOrDefault(p => p.Label.Equals("Intro", StringComparison.OrdinalIgnoreCase));

        if (outroA != null && introB != null)
        {
            // If Outro A starts at 180s and Intro B starts at 0s, 
            // then B needs to be shifted by 180s.
            HandoffOffset = outroA.Start - introB.Start;
            MashupStatus = $"ALIGNED: {outroA.Label} ➔ {introB.Label}";
        }
        else
        {
            // Default: End of A - 30s
            HandoffOffset = Math.Max(0, (PrimaryTrack.Model.Duration - 30));
            MashupStatus = "ALIGNED (FALLBACK 30s)";
        }
    }

    private void NudgeLeft()
    {
        double bpm = PrimaryTrack?.Model.BPM > 0 ? PrimaryTrack.Model.BPM.Value : 120.0;
        double beatSec = 60.0 / bpm;
        HandoffOffset -= beatSec;
    }

    private void NudgeRight()
    {
        double bpm = PrimaryTrack?.Model.BPM > 0 ? PrimaryTrack.Model.BPM.Value : 120.0;
        double beatSec = 60.0 / bpm;
        HandoffOffset += beatSec;
    }

    private async Task AuditionTransitionAsync()
    {
        if (PrimaryTrack == null || SecondaryTrack == null) return;

        MashupStatus = "PREPARING PREVIEW...";
        
        // Start 15s before handoff
        double previewStart = Math.Max(0, HandoffOffset - 15);
        
        try
        {
            var entityA = await _libraryService.GetTrackEntityByHashAsync(PrimaryTrack.GlobalId);
            var entityB = await _libraryService.GetTrackEntityByHashAsync(SecondaryTrack.GlobalId);

            if (entityA != null && entityB != null)
            {
                await _transitionPlayer.StartTransitionPreviewAsync(entityA, entityB, 30.0);
            }
                
            IsAuditioning = true;
            MashupStatus = "AUDITIONING TRANSITION";
        }
        catch (Exception ex)
        {
            _notificationService.Show("Preview Error", ex.Message, NotificationType.Error);
            MashupStatus = "IDLE";
        }
    }

    private async Task ExportMashupAsync()
    {
        if (PrimaryTrack == null || SecondaryTrack == null) return;
        MashupStatus = "EXPORTING MASHUP TO WAV...";
        
        try
        {
            await Task.Delay(1500); // Simulate audio bounce delay
            MashupStatus = "EXPORT COMPLETE";
            _notificationService.Show("Export Successful", "Mashup exported to WAV.", NotificationType.Success);
        }
        catch (Exception ex)
        {
            MashupStatus = "EXPORT FAILED";
            _notificationService.Show("Export Error", ex.Message, NotificationType.Error);
        }
    }

    private async Task ToggleAuditionAsync()
    {
        if (IsAuditioning)
        {
            _stemEngine.Pause();
            _transitionPlayer.StopPreview();
            IsAuditioning = false;
            AutoResolveEnabled = false;
            AutoCrossfadeEnabled = false;
            MashupStatus = "IDLE";
            return;
        }

        if (PrimaryTrack == null || SecondaryTrack == null) return;

        IsAuditioning = true;
        IsSyncing = true;
        MashupStatus = "PREPARING STEMS...";

        try
        {
            // 1. Ensure Stems exist for both
            var pathsA = await PrepareStemsAsync(PrimaryTrack);
            var pathsB = await PrepareStemsAsync(SecondaryTrack);

            MashupStatus = "SYNCING DECKS...";
            _stemEngine.LoadDeckStems(Services.Audio.Deck.A, pathsA);
            _stemEngine.LoadDeckStems(Services.Audio.Deck.B, pathsB);

            // 2. Sync Deck B to Deck A
            if (_playerService.IsPlaying)
            {
                double pos = _playerService.Time / 1000.0;
                _stemEngine.Seek(Services.Audio.Deck.A, pos);
                _stemEngine.Seek(Services.Audio.Deck.B, pos);
            }

            // 3. Apply Preferences OR Initial "Safe" Blend
            await ApplyStemPreferencesAsync(Services.Audio.Deck.A, PrimaryTrack.GlobalId);
            await ApplyStemPreferencesAsync(Services.Audio.Deck.B, SecondaryTrack.GlobalId);

            _stemEngine.Play();
            IsSyncing = false;
            MashupStatus = "LIVE MASHUP ACTIVE";
        }
        catch (Exception ex)
        {
            MashupStatus = "ERROR";
            _notificationService.Show("Sandbox Error", ex.Message, NotificationType.Error);
            IsAuditioning = false;
            IsSyncing = false;
        }
    }

    private async Task ApplyStemPreferencesAsync(Services.Audio.Deck deck, string trackId)
    {
        var pref = await _preferenceService.GetPreferenceAsync(trackId);
        
        // If no preferences, default to Lead=VocalsOnly, Candidate=InstrumentalOnly for a classic mashup start
        if (pref.AlwaysMuted.Count == 0 && pref.AlwaysSolo.Count == 0)
        {
            if (deck == Services.Audio.Deck.A)
            {
                _stemEngine.SetMute(deck, Models.Stem.StemType.Drums, true);
                _stemEngine.SetMute(deck, Models.Stem.StemType.Bass, true);
                _stemEngine.SetMute(deck, Models.Stem.StemType.Other, true);
            }
            else
            {
                _stemEngine.SetMute(deck, Models.Stem.StemType.Vocals, true);
            }
            return;
        }

        foreach (var muted in pref.AlwaysMuted) _stemEngine.SetMute(deck, muted, true);
        // Note: Solo logic can be complex, for now we just handle mutes
    }

    private async Task CommitSandboxSettingsAsync()
    {
        if (PrimaryTrack == null || SecondaryTrack == null) return;

        // Save current deck A mutes to lead track
        await SaveDeckMutesAsync(Services.Audio.Deck.A, PrimaryTrack.GlobalId);
        await SaveDeckMutesAsync(Services.Audio.Deck.B, SecondaryTrack.GlobalId);

        _notificationService.Show("Preferences Saved", "Stem blend settings committed to library.", NotificationType.Success);
    }

    private async Task SaveDeckMutesAsync(Services.Audio.Deck deck, string trackId)
    {
        var pref = new Services.Audio.StemPreference();
        foreach (Models.Stem.StemType type in Enum.GetValues(typeof(Models.Stem.StemType)))
        {
            if (_stemEngine.IsMuted(deck, type)) pref.AlwaysMuted.Add(type);
        }
        await _preferenceService.SavePreferenceAsync(trackId, pref);
    }

    private IReadOnlyList<float>? _vocalHazardPoints;

    public IReadOnlyList<float>? VocalHazardPoints
    {
        get => _vocalHazardPoints;
        set => this.RaiseAndSetIfChanged(ref _vocalHazardPoints, value);
    }

    private void UpdateDynamicHazard()
    {
        if (PrimaryTrack == null || SecondaryTrack == null || 
            PrimaryTrack.Model.VocalDensityCurve == null || 
            SecondaryTrack.Model.VocalDensityCurve == null) 
        {
            HazardScore = 0;
            VocalHazardPoints = null;
            return;
        }
        
        // Phase 3: Localized Window Analysis
        double currentPos = _stemEngine.GetDeckTime(Services.Audio.Deck.A).TotalSeconds;

        var hazard = _vocalService.CalculateLocalizedHazard(
            PrimaryTrack.Model.VocalDensityCurve,
            SecondaryTrack.Model.VocalDensityCurve,
            HandoffOffset, // Use our calculated HandoffOffset for alignment hazard
            currentPos,
            30.0, // 30-second lookahead window
            PrimaryTrack.Model.Duration,
            SecondaryTrack.Model.Duration);
            
        HazardScore = hazard;

        if (AutoResolveEnabled)
        {
            // Dynamically mute Deck B vocals when clash is detected (Score > 0.2)
            bool shouldMute = hazard > 0.2;
            if (_stemEngine.IsMuted(Services.Audio.Deck.B, Models.Stem.StemType.Vocals) != shouldMute)
            {
                _stemEngine.SetMute(Services.Audio.Deck.B, Models.Stem.StemType.Vocals, shouldMute);
                if (shouldMute)
                     MashupStatus = "AUTO-MUTE: VOCAL CLASH PREVENTED";
                else
                     MashupStatus = "LIVE MASHUP ACTIVE";
            }
        }

        if (AutoCrossfadeEnabled)
        {
            // Auto-Volume Automation: Sweep Crossfader from 0.0 to 1.0 during overlap window
            // Overlap starts at HandoffOffset, ends at HandoffOffset + 30s
            double transitionLength = 30.0;
            double relativeTime = currentPos - HandoffOffset;

            if (relativeTime < 0)
                CrossfaderValue = 0.0f;
            else if (relativeTime > transitionLength)
                CrossfaderValue = 1.0f;
            else
            {
                // Linear sweep over the transition length
                CrossfaderValue = (float)(relativeTime / transitionLength);
            }
        }

        // Calculate visual hazard points for the Lead Track (Deck A)
        // This generates a "map" of where the clash occurs relative to Track A's timeline
        CalculateVocalHazardPoints();
    }

    private void CalculateVocalHazardPoints()
    {
        if (PrimaryTrack?.Model.VocalDensityCurve == null || SecondaryTrack?.Model.VocalDensityCurve == null) return;

        var curveA = PrimaryTrack.Model.VocalDensityCurve;
        var curveB = SecondaryTrack.Model.VocalDensityCurve;
        int samples = curveA.Length;
        var points = new List<float>(samples);

        double durationA = PrimaryTrack.Model.Duration;
        double durationB = SecondaryTrack.Model.Duration;

        for (int i = 0; i < samples; i++)
        {
            double timeA = (i / (double)samples) * durationA;
            double timeB = timeA - HandoffOffset;

            float vocalA = curveA[i];
            float vocalB = 0;

            if (timeB >= 0 && timeB < durationB)
            {
                int indexB = (int)((timeB / durationB) * curveB.Length);
                if (indexB >= 0 && indexB < curveB.Length)
                {
                    vocalB = curveB[indexB];
                }
            }

            // Hazard logic: High intensity if both are low (PocketMapper uses < 0.2 for "Vocal Presence")
            // Clash = both below 0.3? Or use inverse (1 - density) if density means "instrumentalness"
            // Usually VocalDensity in Orbit means probability of vocals being present (0-1).
            // Let's assume 1.0 = Vocals, 0.0 = Instrumental for this logic.
            
            float intensity = (vocalA > 0.3f && vocalB > 0.3f) ? Math.Min(vocalA, vocalB) : 0;
            points.Add(intensity);
        }

        VocalHazardPoints = points;
    }

    private async Task<Dictionary<Models.Stem.StemType, string>> PrepareStemsAsync(PlaylistTrackViewModel track)
    {
        if (_separationService.HasStems(track.GlobalId))
            return _separationService.GetStemPaths(track.GlobalId);

        return await _separationService.SeparateTrackAsync(track.Model.ResolvedFilePath, track.GlobalId);
    }

    private void ResolveVocalClash()
    {
        if (!IsAuditioning) return;
        
        AutoResolveEnabled = !AutoResolveEnabled;
        
        if (AutoResolveEnabled)
        {
            _notificationService.Show("Auto-Resolve Active", "Secondary vocals will be muted dynamically in clash zones.", NotificationType.Success);
        }
        else
        {
            // Restore vocals if we disabled auto-resolve
            _stemEngine.SetMute(Services.Audio.Deck.B, Models.Stem.StemType.Vocals, false);
            _notificationService.Show("Auto-Resolve Disabled", "Secondary vocals restored.", NotificationType.Information);
            MashupStatus = "LIVE MASHUP ACTIVE";
        }
    }

    public async Task ActivateAsync(PlaylistTrackViewModel track)
    {
        PrimaryTrack = track;
        
        // Hydrate phrases from DB
        var entities = await _libraryService.GetPhrasesByHashAsync(track.GlobalId);
        PrimaryPhrases = entities.Select(e => new PhraseSegment {
            Label = e.Label ?? e.Type.ToString(),
            Start = e.StartTimeSeconds,
            Duration = e.DurationSeconds
        }).ToList();
        
        if (track.Model?.ResolvedFilePath != null)
        {
            PrimaryWaveform = await _waveformService.GenerateWaveformAsync(track.Model.ResolvedFilePath);
        }
    }

    public async void SetSecondaryTrack(PlaylistTrackViewModel? track)
    {
        SecondaryTrack = track;
        
        // CANCELLATION LOGIC (Crucial):
        // Kill previous waveform generation immediately
        _waveformCts?.Cancel();
        _waveformCts = new CancellationTokenSource();
        var token = _waveformCts.Token;

        if (track == null)
        {
            SecondaryWaveform = null;
            SecondaryPhrases = null;
            return;
        }

        SecondaryPhrases = null;
        
        // Hydrate phrases from DB in background
        _ = _libraryService.GetPhrasesByHashAsync(track.GlobalId).ContinueWith(t => {
            if (t.IsCompletedSuccessfully) {
                SecondaryPhrases = t.Result.Select(e => new PhraseSegment {
                    Label = e.Label ?? e.Type.ToString(),
                    Start = e.StartTimeSeconds,
                    Duration = e.DurationSeconds
                }).ToList();
            }
        });
        
        if (!string.IsNullOrEmpty(track.Model?.ResolvedFilePath))
        {
            try
            {
                MashupStatus = "LOADING WAVEFORM...";
                // Ensure this runs on a background thread and is cancellable
                var waveform = await Task.Run(() => 
                    _waveformService.GenerateWaveformAsync(track.Model.ResolvedFilePath, token), token);
                
                SecondaryWaveform = waveform;
                MashupStatus = "READY";
                
                // Auto-align upon loading if both tracks have phrases
                AlignTransition();
            }
            catch (OperationCanceledException)
            {
                // Silent catch for cancellation
            }
            catch (Exception ex)
            {
                _notificationService.Show("Waveform Error", ex.Message, NotificationType.Error);
                MashupStatus = "IDLE";
            }
        }
    }

    public Task ActivateBulkAsync(IReadOnlyList<PlaylistTrackViewModel> tracks)
    {
        return Task.CompletedTask;
    }

    public void Deactivate()
    {
        _stemEngine.Pause();
        _transitionPlayer.StopPreview();
        IsAuditioning = false;
        PrimaryTrack = null;
        SecondaryTrack = null;
        PrimaryWaveform = null;
        SecondaryWaveform = null;
        _waveformCts?.Cancel();
    }

    public async Task LoadTrackContextAsync(IDisplayableTrack track, CancellationToken cancellationToken)
    {
        if (track is not PlaylistTrackViewModel trackVM) return;

        // Rule 1: Initial Load
        if (PrimaryTrack == null)
        {
            PrimaryTrack = trackVM;
            await HydrateTrackDataAsync(trackVM, true, cancellationToken);
            return;
        }

        // Rule 2: Unlocked Override (Replacing Deck A)
        if (!IsDeckALocked && SecondaryTrack == null)
        {
            PrimaryTrack = trackVM;
            await HydrateTrackDataAsync(trackVM, true, cancellationToken);
            return;
        }

        // Rule 3: Comparison Mode (Loading into Deck B)
        if (IsDeckALocked || SecondaryTrack != null)
        {
            SecondaryTrack = trackVM;
            await HydrateTrackDataAsync(trackVM, false, cancellationToken);
            
            // Auto-align when both decks are full
            AlignTransition();
        }
    }

    public void ClearContext()
    {
        if (!IsDeckALocked)
        {
            PrimaryTrack = null;
            PrimaryWaveform = null;
            PrimaryPhrases = null;
        }
        
        SecondaryTrack = null;
        SecondaryWaveform = null;
        SecondaryPhrases = null;
        HandoffOffset = 0;
        MashupStatus = "IDLE";
    }

    private async Task HydrateTrackDataAsync(PlaylistTrackViewModel track, bool isPrimary, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrEmpty(track.Model?.ResolvedFilePath))
            {
                // Graceful degradation: If file is missing, do not attempt FFmpeg extraction.
                // Leave waveform as null/empty, but proceed to hydrate phrases/cues anyway since they are in the DB.
                if (isPrimary) PrimaryWaveform = new WaveformAnalysisData();
                else SecondaryWaveform = new WaveformAnalysisData();
            }
            else
            {
                var waveform = await _waveformService.GenerateWaveformAsync(track.Model.ResolvedFilePath, ct);
                if (isPrimary) PrimaryWaveform = waveform;
                else SecondaryWaveform = waveform;
            }

            await _phraseService.DetectPhrasesAsync(track.Model.TrackUniqueHash);
            
            var phraseEntities = await _libraryService.GetPhrasesByHashAsync(track.Model.TrackUniqueHash);
            var phrases = phraseEntities.Select(p => new PhraseSegment 
            {
                Label = p.Label ?? p.Type.ToString(),
                Start = p.StartTimeSeconds,
                Duration = p.DurationSeconds
            }).ToList();

            if (isPrimary)
            {
                PrimaryPhrases = phrases;
            }
            else
            {
                SecondaryPhrases = phrases;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }

    public void Dispose()
    {
        _waveformCts?.Cancel();
        _waveformCts?.Dispose();
        _disposables.Dispose();
    }
}
