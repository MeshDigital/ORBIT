using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;
using DynamicData.Binding;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;

namespace SLSKDONET.ViewModels;

public class MissionControlViewModel : ReactiveObject, IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly BatchStemExportService _stemService;
    private readonly CompositeDisposable _disposables = new();

    private DashboardSnapshot? _latestSnapshot;
    public DashboardSnapshot? LatestSnapshot
    {
        get => _latestSnapshot;
        private set => this.RaiseAndSetIfChanged(ref _latestSnapshot, value);
    }

    private SystemHealth _health = SystemHealth.Excellent;
    public SystemHealth Health
    {
        get => _health;
        private set => this.RaiseAndSetIfChanged(ref _health, value);
    }

    private int _activeOperationsCount;
    public int ActiveOperationsCount
    {
        get => _activeOperationsCount;
        private set => this.RaiseAndSetIfChanged(ref _activeOperationsCount, value);
    }

    private string _statusSummary = "System Ready";
    public string StatusSummary
    {
        get => _statusSummary;
        private set => this.RaiseAndSetIfChanged(ref _statusSummary, value);
    }

    // --- Phase 3: Acapella Factory (Batch Stem Export) State ---
    
    private readonly ObservableAsPropertyHelper<bool> _isStemQueueActive;
    public bool IsStemQueueActive => _isStemQueueActive.Value;

    private readonly ObservableAsPropertyHelper<string> _stemQueueStatusText;
    public string StemQueueStatusText => _stemQueueStatusText.Value;

    private readonly ObservableAsPropertyHelper<double> _stemQueueProgress;
    public double StemQueueProgress => _stemQueueProgress.Value;

    public System.Windows.Input.ICommand CancelStemQueueCommand { get; }

    public MissionControlViewModel(
        IEventBus eventBus,
        BatchStemExportService stemService)
    {
        _eventBus = eventBus;
        _stemService = stemService;

        CancelStemQueueCommand = ReactiveCommand.Create(() => 
        {
            _stemService.CancelAll();
        });

        _eventBus.GetEvent<DashboardSnapshot>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnSnapshotReceived)
            .DisposeWith(_disposables);

        // Bind Acapella Factory active state: True if any jobs exist
        _stemService.ActiveJobs.WhenAnyValue(x => x.Count)
            .Select(c => c > 0)
            .ToProperty(this, x => x.IsStemQueueActive, out _isStemQueueActive)
            .DisposeWith(_disposables);

        // Track aggregate queue progress using a simple reactive poll (safe & efficient)
        Observable.Interval(TimeSpan.FromMilliseconds(500), RxApp.MainThreadScheduler)
            .Select(_ => 
            {
                var jobs = _stemService.ActiveJobs;
                var total = jobs.Count;
                if (total == 0) return 0.0;
                var sum = jobs.Sum(j => j.Progress);
                return Math.Clamp(sum / total, 0.0, 1.0);
            })
            .ToProperty(this, x => x.StemQueueProgress, out _stemQueueProgress)
            .DisposeWith(_disposables);

        // Format Status Text based on queue count
        _stemService.ActiveJobs.WhenAnyValue(x => x.Count)
            .Select(c => c > 0 ? $"Extracting Stems: {c} jobs pending" : string.Empty)
            .ToProperty(this, x => x.StemQueueStatusText, out _stemQueueStatusText)
            .DisposeWith(_disposables);
    }

    private void OnSnapshotReceived(DashboardSnapshot snapshot)
    {
        LatestSnapshot = snapshot;
        Health = snapshot.SystemHealth;
        ActiveOperationsCount = snapshot.ActiveOperations.Count;
        
        UpdateStatusSummary(snapshot);
    }

    private void UpdateStatusSummary(DashboardSnapshot snapshot)
    {
        if (snapshot.ActiveOperations.Count > 0)
        {
            var op = snapshot.ActiveOperations.First();
            if (snapshot.ActiveOperations.Count > 1)
            {
                StatusSummary = $"{op.Title} (+{snapshot.ActiveOperations.Count - 1} more)";
            }
            else
            {
                StatusSummary = op.Title;
            }
        }
        else
        {
            StatusSummary = "System Idle";
        }
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}
