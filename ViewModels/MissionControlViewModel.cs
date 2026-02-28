using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels;

public class MissionControlViewModel : ReactiveObject, IDisposable
{
    private readonly IEventBus _eventBus;
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

    public MissionControlViewModel(IEventBus eventBus)
    {
        _eventBus = eventBus;

        _eventBus.GetEvent<DashboardSnapshot>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnSnapshotReceived)
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
