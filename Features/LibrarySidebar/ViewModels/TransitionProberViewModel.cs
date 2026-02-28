using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Reactive.Linq;
using System.Reactive.Concurrency;
using ReactiveUI;
using SLSKDONET.Views;
using SLSKDONET.ViewModels;
using System.Reactive.Disposables;

namespace SLSKDONET.Features.LibrarySidebar.ViewModels;

public class TransitionProberViewModel : ReactiveObject, ISidebarContent, IDisposable
{
    private readonly Services.Audio.RealTimeStemEngine _stemEngine;
    private readonly Services.StemSeparationService _separationService;
    private readonly Services.Musical.VocalIntelligenceService _vocalService;
    private readonly Services.IAudioPlayerService _playerService;
    private readonly INotificationService _notificationService;
    private readonly Services.Audio.StemPreferenceService _preferenceService;
    private readonly CompositeDisposable _disposables = new();

    private PlaylistTrackViewModel? _primaryTrack;
    private PlaylistTrackViewModel? _secondaryTrack;
    private bool _isAuditioning;
    private bool _isSyncing;
    private double _hazardScore;
    private string _mashupStatus = "IDLE";

    public PlaylistTrackViewModel? PrimaryTrack
    {
        get => _primaryTrack;
        set => this.RaiseAndSetIfChanged(ref _primaryTrack, value);
    }

    public PlaylistTrackViewModel? SecondaryTrack
    {
        get => _secondaryTrack;
        set => this.RaiseAndSetIfChanged(ref _secondaryTrack, value);
    }

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

    public ICommand ToggleAuditionCommand { get; }
    public ICommand ResolveClashCommand { get; }
    public ICommand CommitSandboxCommand { get; }

    public TransitionProberViewModel(
        Services.Audio.RealTimeStemEngine stemEngine,
        Services.StemSeparationService separationService,
        Services.Musical.VocalIntelligenceService vocalService,
        Services.IAudioPlayerService playerService,
        INotificationService notificationService,
        Services.Audio.StemPreferenceService preferenceService)
    {
        _stemEngine = stemEngine;
        _separationService = separationService;
        _vocalService = vocalService;
        _playerService = playerService;
        _notificationService = notificationService;
        _preferenceService = preferenceService;

        ToggleAuditionCommand = ReactiveCommand.CreateFromTask(ToggleAuditionAsync);
        ResolveClashCommand = ReactiveCommand.Create(ResolveVocalClash);
        CommitSandboxCommand = ReactiveCommand.CreateFromTask(CommitSandboxSettingsAsync);

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

    private async Task ToggleAuditionAsync()
    {
        if (IsAuditioning)
        {
            _stemEngine.Pause();
            IsAuditioning = false;
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

    private void UpdateDynamicHazard()
    {
        if (PrimaryTrack == null || SecondaryTrack == null) return;
        
        // Phase 3: Localized Window Analysis
        double currentPos = _stemEngine.GetDeckTime(Services.Audio.Deck.A).TotalSeconds;

        var hazard = _vocalService.CalculateLocalizedHazard(
            PrimaryTrack.Model.VocalDensityCurve,
            SecondaryTrack.Model.VocalDensityCurve,
            0, // Sync Offset
            currentPos,
            30.0, // 30-second lookahead window
            PrimaryTrack.Model.Duration,
            SecondaryTrack.Model.Duration);
            
        HazardScore = hazard;
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
        
        // Strategy: Favor Deck A vocals, mute Deck B vocals if clashing
        _stemEngine.SetMute(Services.Audio.Deck.B, Models.Stem.StemType.Vocals, true);
        _notificationService.Show("Vocal Clash Resolved", "Muted secondary track vocals.", NotificationType.Success);
        HazardScore = 0;
    }

    public Task ActivateAsync(PlaylistTrackViewModel track)
    {
        PrimaryTrack = track;
        return Task.CompletedTask;
    }

    public void SetSecondaryTrack(PlaylistTrackViewModel? track)
    {
        SecondaryTrack = track;
    }

    public Task ActivateBulkAsync(IReadOnlyList<PlaylistTrackViewModel> tracks)
    {
        return Task.CompletedTask;
    }

    public void Deactivate()
    {
        _stemEngine.Pause();
        IsAuditioning = false;
        PrimaryTrack = null;
        SecondaryTrack = null;
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}
