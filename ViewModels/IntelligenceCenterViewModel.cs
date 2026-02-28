using System;
using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;
using Avalonia.Threading;
using SLSKDONET.Data.Entities;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.AI;
using SLSKDONET.Services.Analysis;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

public enum IntelligenceViewState
{
    Blade,   // Side-panel view (Library)
    Console, // Full overlay view (Mission Control)
    Closed
}

/// <summary>
/// "Operation Glass Console": The unified intelligence hub for ORBIT.
/// Consolidates Metadata, AI Analysis, and Forensic Telemetry.
/// </summary>
public class IntelligenceCenterViewModel : ReactiveObject, IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly ILibraryService _libraryService;
    private readonly TrackForensicLogger _forensicLogger;
    private readonly INotificationService _notificationService;
    private readonly ILogger<IntelligenceCenterViewModel> _logger;
    private readonly PlayerViewModel _playerViewModel;
    private readonly IAudioIntelligenceService _intelligenceService;
    private readonly StemSeparationService _separationService;
    private readonly SetlistStressTestService _stressTestService;
    private readonly CompositeDisposable _disposables = new();

    private IntelligenceViewState _viewState = IntelligenceViewState.Closed;
    public IntelligenceViewState ViewState
    {
        get => _viewState;
        set 
        {
            this.RaiseAndSetIfChanged(ref _viewState, value);
            IsVisible = value != IntelligenceViewState.Closed;
        }
    }

    private bool _isVisible;
    public bool IsVisible
    {
        get => _isVisible;
        private set => this.RaiseAndSetIfChanged(ref _isVisible, value);
    }

    private string? _selectedTrackHash;
    public string? SelectedTrackHash
    {
        get => _selectedTrackHash;
        set => this.RaiseAndSetIfChanged(ref _selectedTrackHash, value);
    }

    // High-Fidelity Sub-ViewModels
    public TrackInspectorViewModel Inspector { get; }
    public ForensicLabViewModel Lab { get; }

    // Live Terminal Logs
    public ObservableCollection<ForensicLogEntry> TerminalLogs { get; } = new();

    // Overhaul: Forensic Depth Properties
    private float _normalizedPosition;
    public float NormalizedPosition
    {
        get => _normalizedPosition;
        set => this.RaiseAndSetIfChanged(ref _normalizedPosition, value);
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

    private ObservableCollection<string> _trajectoryAdvice = new();
    public ObservableCollection<string> TrajectoryAdvice => _trajectoryAdvice;

    private string _mentorVerdict = "ANALYZING...";
    public string MentorVerdict
    {
        get => _mentorVerdict;
        set => this.RaiseAndSetIfChanged(ref _mentorVerdict, value);
    }

    public IntelligenceCenterViewModel(
        IEventBus eventBus,
        ILibraryService libraryService,
        TrackForensicLogger forensicLogger,
        TrackInspectorViewModel inspector,
        ForensicLabViewModel lab,
        PlayerViewModel playerViewModel,
        IAudioIntelligenceService intelligenceService,
        StemSeparationService separationService,
        SetlistStressTestService stressTestService,
        INotificationService notificationService,
        ILogger<IntelligenceCenterViewModel> logger)
    {
        _eventBus = eventBus;
        _libraryService = libraryService;
        _forensicLogger = forensicLogger;
        _notificationService = notificationService;
        _logger = logger;
        _playerViewModel = playerViewModel;
        _intelligenceService = intelligenceService;
        _separationService = separationService;
        _stressTestService = stressTestService;
        
        Inspector = inspector;
        Lab = lab;

        SwitchToBladeCommand = ReactiveCommand.Create(() => { ViewState = IntelligenceViewState.Blade; });
        SwitchToConsoleCommand = ReactiveCommand.Create(() => { ViewState = IntelligenceViewState.Console; });

        // Subscribe to live forensic logs
        _forensicLogger.LogGenerated += OnForensicLogGenerated;
        
        // Listen for global focus requests
        _eventBus.GetEvent<RequestForensicAnalysisEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(evt =>
            {
                _ = OpenAsync(evt.TrackHash, IntelligenceViewState.Console);
            })
            .DisposeWith(_disposables);

        // Overhaul: Playback Sync
        _playerViewModel.WhenAnyValue(x => x.Position)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(pos => NormalizedPosition = pos)
            .DisposeWith(_disposables);

        SeekToCueCommand = ReactiveCommand.Create<OrbitCue>(cue =>
        {
            _eventBus.Publish(new SeekToSecondsRequestEvent(cue.Timestamp));
        });

        CloseCommand = ReactiveCommand.Create(Close);
        CloseInspectorCommand = CloseCommand;

        CommitMetadataCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (SelectedTrackHash == null) return;

            try
            {
                _logger.LogInformation("Committing metadata for track {Hash}", SelectedTrackHash);
                
                // 1. Sync Cues
                if (Inspector.SaveCuesCommand != null)
                {
                    Inspector.SaveCuesCommand.Execute(null);
                }

                // 2. Sync Structural Data (AIP)
                await Inspector.SaveStructuralDataAsync();

                // 3. Mark as Prepared
                await _libraryService.MarkTrackAsVerifiedAsync(SelectedTrackHash);

                _notificationService.Show("Forensic Commit", "All metadata and forensic trails synchronized to library.", NotificationType.Success);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to commit metadata for track {Hash}", SelectedTrackHash);
                _notificationService.Show("Commit Failed", "Failed to sync metadata. Check logs.", NotificationType.Error);
            }
        });
    }

    public System.Windows.Input.ICommand CommitMetadataCommand { get; }

    public System.Windows.Input.ICommand SeekToCueCommand { get; }
    public System.Windows.Input.ICommand CloseCommand { get; }
    public System.Windows.Input.ICommand CloseInspectorCommand { get; }

    private void OnForensicLogGenerated(object? sender, ForensicLogEntry entry)
    {
        if (SelectedTrackHash != null && entry.TrackIdentifier == SelectedTrackHash)
        {
            Dispatcher.UIThread.Post(() =>
            {
                TerminalLogs.Insert(0, entry);
                if (TerminalLogs.Count > 100) TerminalLogs.RemoveAt(TerminalLogs.Count - 1);
            });
        }
    }

    public async Task OpenAsync(string trackHash, IntelligenceViewState state = IntelligenceViewState.Console)
    {
        if (SelectedTrackHash == trackHash && ViewState == state && IsVisible) return;

        _logger.LogInformation("Opening Intelligence Center for track {Hash} in {State} mode", trackHash, state);

        SelectedTrackHash = trackHash;
        TerminalLogs.Clear();
        
        // 1. Load historical logs from DB
        await LoadHistoricalLogsAsync(trackHash);

        // 2. Load Forensic Lab data
        await Lab.LoadTrackAsync(trackHash);

        // 3. Load Metadata Inspector data
        var tracks = await _libraryService.GetAllPlaylistTracksAsync();
        var track = tracks.FirstOrDefault(t => t.TrackUniqueHash == trackHash);
        if (track != null)
        {
            Inspector.Track = track;
        }

        ViewState = state;

        // 4. Update trajectory advice
        await UpdateTrajectoryAdviceAsync();
    }

    private async Task RunInstrumentalAnalysisAsync()
    {
        if (SelectedTrackHash == null || !AnalyzeHarmonicsInstOnly)
        {
            ShowEclipseBadge = false;
            return;
        }

        try
        {
            _forensicLogger.Log(SelectedTrackHash, "ECLIPSE", "Initiating instrumental harmonic scan...", Models.ForensicLevel.Info);
            
            // Find instrumental stem
            var stems = _separationService.GetStemPaths(SelectedTrackHash);
            var instPath = stems.TryGetValue(Models.Stem.StemType.Other, out var path) ? path : null; 

            if (instPath == null || !System.IO.File.Exists(instPath))
            {
                _notificationService.Show("Instrumental stem missing. Run separation first.", "Eclipse Mode", NotificationType.Warning);
                _forensicLogger.Log(SelectedTrackHash, "ECLIPSE", "Scan aborted: Stem missing.", Models.ForensicLevel.Warning);
                return;
            }

            var features = await _intelligenceService.AnalyzeTrackAsync(instPath, SelectedTrackHash + "_INST", tier: AnalysisTier.Tier1);
            if (features != null)
            {
                EclipseKey = features.CamelotKey;
                EclipseConfidence = features.KeyConfidence;
                ShowEclipseBadge = EclipseKey != Lab.CamelotKey;
                
                _forensicLogger.Log(SelectedTrackHash, "ECLIPSE", $"Scan complete. Detected: {EclipseKey} (Conf: {EclipseConfidence:P0})", Models.ForensicLevel.Success);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Instrumental analysis failed");
            _forensicLogger.Log(SelectedTrackHash, "ECLIPSE", "Scan failed: Internal engine error.", Models.ForensicLevel.Error);
        }
    }

    private async Task UpdateTrajectoryAdviceAsync()
    {
        if (SelectedTrackHash == null || _playerViewModel.Queue == null) return;

        TrajectoryAdvice.Clear();
        var tracks = await _libraryService.GetAllPlaylistTracksAsync();
        var currentTrackModel = tracks.FirstOrDefault(t => t.TrackUniqueHash == SelectedTrackHash);
        if (currentTrackModel == null) return;

        var currentEntity = await _libraryService.GetTrackEntityByHashAsync(SelectedTrackHash);
        if (currentEntity == null) return;

        var inQueue = _playerViewModel.Queue.FirstOrDefault(pt => pt.GlobalId == SelectedTrackHash);
        if (inQueue != null)
        {
            var currentIndex = _playerViewModel.Queue.IndexOf(inQueue);
            if (currentIndex != -1 && currentIndex < _playerViewModel.Queue.Count - 1)
            {
                var nextTrackVm = _playerViewModel.Queue[currentIndex + 1];
                var nextEntity = await _libraryService.GetTrackEntityByHashAsync(nextTrackVm.GlobalId);
                
                if (nextEntity != null)
                {
                    var advice = await _stressTestService.AnalyzeTransitionAsync(currentEntity, nextEntity);
                    TrajectoryAdvice.Add($"> NEXT: {nextEntity.Title}");
                    TrajectoryAdvice.Add($"> QUALITY: {100 - advice.SeverityScore}%");
                    
                    if (advice.SeverityScore > 60)
                    {
                        TrajectoryAdvice.Add($"> WARNING: {advice.PrimaryFailure}");
                        MentorVerdict = "CAUTION: TRANSITION RISK";
                    }
                    else
                    {
                        TrajectoryAdvice.Add("> STABLE FLOW DETECTED");
                        MentorVerdict = "STABLE FLOW";
                    }
                }
            }
            else
            {
                TrajectoryAdvice.Add("> TRAJECTORY COMPLETE");
                MentorVerdict = "END OF QUEUE";
            }
        }
    }

    private async Task LoadHistoricalLogsAsync(string trackHash)
    {
        try
        {
            using var db = new SLSKDONET.Data.AppDbContext();
            var logs = await db.ForensicLogs
                .Where(l => l.TrackIdentifier == trackHash)
                .OrderByDescending(l => l.Timestamp)
                .Take(50)
                .ToListAsync();

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var log in logs) TerminalLogs.Add(log);
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load historical forensic logs for {Hash}", trackHash);
        }
    }

    public void Close()
    {
        ViewState = IntelligenceViewState.Closed;
        SelectedTrackHash = null;
    }

    public void Dispose()
    {
        _disposables.Dispose();
        _forensicLogger.LogGenerated -= OnForensicLogGenerated;
        Inspector.Dispose();
        Lab.Dispose();
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> SwitchToBladeCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> SwitchToConsoleCommand { get; }
}
