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
    private readonly ILogger<IntelligenceCenterViewModel> _logger;
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

    public IntelligenceCenterViewModel(
        IEventBus eventBus,
        ILibraryService libraryService,
        TrackForensicLogger forensicLogger,
        TrackInspectorViewModel inspector,
        ForensicLabViewModel lab,
        ILogger<IntelligenceCenterViewModel> logger)
    {
        _eventBus = eventBus;
        _libraryService = libraryService;
        _forensicLogger = forensicLogger;
        _logger = logger;
        
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

        SeekToCueCommand = ReactiveCommand.Create<OrbitCue>(cue =>
        {
            _eventBus.Publish(new SeekToSecondsRequestEvent(cue.Timestamp));
        });

        CloseCommand = ReactiveCommand.Create(Close);
        CloseInspectorCommand = CloseCommand;
    }

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
