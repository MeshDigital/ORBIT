using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services.Testing;

namespace SLSKDONET.ViewModels;

/// <summary>
/// ViewModel for the Diagnostics & Telemetry Panel.
/// Provides real-time stress test monitoring and results display.
/// </summary>
public class DiagnosticsViewModel : ReactiveObject
{
    private readonly CockpitStressTestService _stressTestService;
    private readonly GenreBridgeTestService _genreBridgeService;
    private CancellationTokenSource? _cts;

    public DiagnosticsViewModel(CockpitStressTestService stressTestService, GenreBridgeTestService genreBridgeService)
    {
        _stressTestService = stressTestService;
        _genreBridgeService = genreBridgeService;

        // Wire up events
        _stressTestService.StatusUpdated += s => Status = s;
        _stressTestService.PhaseChanged += p => CurrentPhase = p;
        _stressTestService.FpsUpdated += fps => CurrentFps = fps;
        _stressTestService.LogEntry += log => 
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => LogEntries.Add(log));
        };

        // Wire up Genre Bridge events
        _genreBridgeService.LogEntry += log =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => LogEntries.Add(log));
        };

        // Commands
        RunStressTestCommand = ReactiveCommand.CreateFromTask(RunStressTestAsync);
        RunCreativeTestCommand = ReactiveCommand.CreateFromTask(RunCreativeStressTestAsync);
        CancelTestCommand = ReactiveCommand.Create(CancelTest);
    }

    // Commands
    public ReactiveCommand<Unit, Unit> RunStressTestCommand { get; }
    public ReactiveCommand<Unit, Unit> RunCreativeTestCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelTestCommand { get; }

    // State
    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        set => this.RaiseAndSetIfChanged(ref _isRunning, value);
    }

    private bool _isPanelOpen;
    public bool IsPanelOpen
    {
        get => _isPanelOpen;
        set => this.RaiseAndSetIfChanged(ref _isPanelOpen, value);
    }

    private string _status = "Ready";
    public string Status
    {
        get => _status;
        set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    private string _currentPhase = "Idle";
    public string CurrentPhase
    {
        get => _currentPhase;
        set => this.RaiseAndSetIfChanged(ref _currentPhase, value);
    }

    private double _currentFps;
    public double CurrentFps
    {
        get => _currentFps;
        set => this.RaiseAndSetIfChanged(ref _currentFps, value);
    }

    private StressTestMetrics? _testResults;
    public StressTestMetrics? TestResults
    {
        get => _testResults;
        set => this.RaiseAndSetIfChanged(ref _testResults, value);
    }

    public ObservableCollection<string> LogEntries { get; } = new();

    // Computed properties for gauge coloring
    public string FpsColor => CurrentFps switch
    {
        >= 55 => "#00FF99",  // Green - healthy
        >= 40 => "#FFAA00",  // Orange - warning
        _ => "#FF4444"       // Red - critical
    };

    public double FpsGaugeAngle => Math.Min(CurrentFps / 120.0, 1.0) * 270; // Max 270 degrees

    private async Task RunStressTestAsync()
    {
        if (IsRunning) return;

        IsRunning = true;
        LogEntries.Clear();
        TestResults = null;
        _cts = new CancellationTokenSource();

        try
        {
            TestResults = await _stressTestService.RunAsync(_cts.Token);
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void CancelTest()
    {
        _cts?.Cancel();
        Status = "Test cancelled";
        CurrentPhase = "Cancelled";
    }

    private async Task RunCreativeStressTestAsync()
    {
        if (IsRunning) return;

        IsRunning = true;
        LogEntries.Clear();
        CurrentPhase = "Genre Bridge Challenge";
        Status = "Running Creative Stress Test...";
        _cts = new CancellationTokenSource();

        try
        {
            var result = await _genreBridgeService.RunSyntheticChallengeAsync(_cts.Token);
            Status = result.Success ? $"✅ {result.Verdict}" : $"❌ {result.ErrorMessage}";
            CurrentPhase = $"Confidence: {result.Confidence}%";
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void OpenPanel() => IsPanelOpen = true;
    public void ClosePanel() => IsPanelOpen = false;
}
