using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels;

public class StatusBarViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    
    private int _queuedCount;
    private int _processedCount;
    private string? _currentTrack;
    private bool _isPaused;
    
    public int QueuedCount
    {
        get => _queuedCount;
        set
        {
            this.RaiseAndSetIfChanged(ref _queuedCount, value);
            this.RaisePropertyChanged(nameof(StatusText));
            this.RaisePropertyChanged(nameof(IsProcessing));
        }
    }
    
    public int ProcessedCount
    {
        get => _processedCount;
        set => this.RaiseAndSetIfChanged(ref _processedCount, value);
    }
    
    public string? CurrentTrack
    {
        get => _currentTrack;
        set
        {
            this.RaiseAndSetIfChanged(ref _currentTrack, value);
            this.RaisePropertyChanged(nameof(IsProcessing));
        }
    }
    
    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            this.RaiseAndSetIfChanged(ref _isPaused, value);
            this.RaisePropertyChanged(nameof(StatusText));
        }
    }
    
    // Computed properties
    public string StatusText
    {
        get
        {
            if (IsPaused) return "⏸️ Analysis Paused";
            if (QueuedCount > 0) return $"🔬 Analyzing... {QueuedCount} pending";
            if (ProcessedCount > 0) return $"✓ All tracks analyzed ({ProcessedCount} total)";
            return "✓ Ready";
        }
    }
    
    public bool IsProcessing => QueuedCount > 0 || !string.IsNullOrEmpty(CurrentTrack);
    
    public StatusBarViewModel(IEventBus eventBus)
    {
        // Subscribe to queue status changes
        eventBus.GetEvent<AnalysisQueueStatusChangedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                QueuedCount = e.QueuedCount;
                ProcessedCount = e.ProcessedCount;
                CurrentTrack = e.CurrentTrackHash;
                IsPaused = e.IsPaused;
            })
            .DisposeWith(_disposables);
    }
    
    public void Dispose()
    {
        _disposables?.Dispose();
    }
}
