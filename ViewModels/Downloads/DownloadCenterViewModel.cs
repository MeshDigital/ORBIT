using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels.Downloads;

/// <summary>
/// Phase 2.5: Global Download Center - Singleton observer that tracks all downloads.
/// Manages Active, Completed, and Failed collections with real-time event subscriptions.
/// </summary>
public class DownloadCenterViewModel : ReactiveObject, IDisposable
{
    private readonly DownloadManager _downloadManager;
    private readonly IEventBus _eventBus;
    private readonly CompositeDisposable _subscriptions = new();
    
    // Collections (DynamicData Source)
    private readonly SourceCache<UnifiedTrackViewModel, string> _downloadsSource = new(x => x.GlobalId);

    // Public ReadOnly Collections (Bound to UI)
    private readonly ReadOnlyObservableCollection<UnifiedTrackViewModel> _activeDownloads;
    public ReadOnlyObservableCollection<UnifiedTrackViewModel> ActiveDownloads => _activeDownloads;

    private readonly ReadOnlyObservableCollection<UnifiedTrackViewModel> _completedDownloads;
    public ReadOnlyObservableCollection<UnifiedTrackViewModel> CompletedDownloads => _completedDownloads;

    private readonly ReadOnlyObservableCollection<UnifiedTrackViewModel> _failedDownloads;
    public ReadOnlyObservableCollection<UnifiedTrackViewModel> FailedDownloads => _failedDownloads;

    // Phase 2: Active Groups (Album-Centric)
    private readonly ReadOnlyObservableCollection<DownloadGroupViewModel> _activeGroups;
    public ReadOnlyObservableCollection<DownloadGroupViewModel> ActiveGroups => _activeGroups;

    // Swimlanes (Derived from Active)
    private readonly ReadOnlyObservableCollection<UnifiedTrackViewModel> _expressItems;
    public ReadOnlyObservableCollection<UnifiedTrackViewModel> ExpressItems => _expressItems;

    private readonly ReadOnlyObservableCollection<UnifiedTrackViewModel> _standardItems;
    public ReadOnlyObservableCollection<UnifiedTrackViewModel> StandardItems => _standardItems;

    private readonly ReadOnlyObservableCollection<UnifiedTrackViewModel> _backgroundItems;
    public ReadOnlyObservableCollection<UnifiedTrackViewModel> BackgroundItems => _backgroundItems;

    // Ongoing vs Queued Split
    private readonly ReadOnlyObservableCollection<UnifiedTrackViewModel> _ongoingDownloads;
    public ReadOnlyObservableCollection<UnifiedTrackViewModel> OngoingDownloads => _ongoingDownloads;

    private readonly ReadOnlyObservableCollection<UnifiedTrackViewModel> _queuedDownloads;
    public ReadOnlyObservableCollection<UnifiedTrackViewModel> QueuedDownloads => _queuedDownloads;

    // Stats
    private int _activeCount;
    public int ActiveCount
    {
        get => _activeCount;
        set => this.RaiseAndSetIfChanged(ref _activeCount, value);
    }

    private int _queuedCount;
    public int QueuedCount
    {
        get => _queuedCount;
        set => this.RaiseAndSetIfChanged(ref _queuedCount, value);
    }

    private int _completedTodayCount;
    public int CompletedTodayCount
    {
        get => _completedTodayCount;
        set => this.RaiseAndSetIfChanged(ref _completedTodayCount, value);
    }
    
    private int _failedCount;
    public int FailedCount
    {
        get => _failedCount;
        set => this.RaiseAndSetIfChanged(ref _failedCount, value);
    }
    
    private string _globalSpeed = "0 MB/s";
    public string GlobalSpeed
    {
        get => _globalSpeed;
        set => this.RaiseAndSetIfChanged(ref _globalSpeed, value);
    }
    
    // Alias for HomeViewModel compatibility
    public string GlobalSpeedDisplay => GlobalSpeed;

    public int MaxConcurrentDownloads
    {
        get => _downloadManager.MaxActiveDownloads;
        set 
        {
             // Validate range 1-50
             if (value < 1 || value > 50) return;
             
             if (_downloadManager.MaxActiveDownloads != value)
             {
                 _downloadManager.MaxActiveDownloads = value;
                 this.RaisePropertyChanged();
             }
        }
    }
    
    // Commands
    public ICommand PauseAllCommand { get; }
    public ICommand ResumeAllCommand { get; }
    public ICommand ClearCompletedCommand { get; }
    public ICommand ClearFailedCommand { get; }
    public ICommand RetryAllFailedCommand { get; }
    
    private readonly ArtworkCacheService _artworkCache;
    private readonly ILibraryService _libraryService;
    
    public DownloadCenterViewModel(
        DownloadManager downloadManager, 
        IEventBus eventBus, 
        ArtworkCacheService artworkCache,
        ILibraryService libraryService)
    {
        _downloadManager = downloadManager;
        _eventBus = eventBus;
        _artworkCache = artworkCache;
        _libraryService = libraryService;
        
        // Initialize commands (ReactiveCommand)
        PauseAllCommand = ReactiveCommand.Create(PauseAll, 
            this.WhenAnyValue(x => x.ActiveCount, count => count > 0));
        
        ResumeAllCommand = ReactiveCommand.Create(ResumeAll,
            this.WhenAnyValue(x => x.ActiveCount, count => count > 0));
        
        ClearCompletedCommand = ReactiveCommand.Create(() => 
            _downloadsSource.Remove(_downloadsSource.Items.Where(x => x.State == PlaylistTrackState.Completed).ToList()));
        
        ClearFailedCommand = ReactiveCommand.Create(() => 
            _downloadsSource.Remove(_downloadsSource.Items.Where(x => x.State == PlaylistTrackState.Failed).ToList()));

        RetryAllFailedCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            var failedItems = FailedDownloads.ToList();
            foreach (var item in failedItems)
            {
                // Fix: Call manager directly to ensure execution and avoid ReactiveCommand subscription issues
                _downloadManager.HardRetryTrack(item.GlobalId);
            }
            await Task.CompletedTask;
        }, this.WhenAnyValue(x => x.FailedCount, count => count > 0));
        
        // Initialize DynamicData Pipelines
        
        // 1. Base Pipeline (Active vs Completed vs Failed)
        var sharedSource = _downloadsSource.Connect()
            .AutoRefresh(x => x.State) // Logic re-evaluates when State changes
            .Publish(); // Share subscription

        // Active Pipeline
        var activeComparer = SortExpressionComparer<UnifiedTrackViewModel>.Descending(x => x.State == PlaylistTrackState.Downloading);
        
        sharedSource
            .Filter(x => x.IsActive || x.IsIndeterminate) // Use helper properties
            .SortAndBind(out _activeDownloads, activeComparer)
            .DisposeMany() // Dispose VMs when removed from Active? No, they might move to Completed.
            // CAREFUL: DisposeMany() here would dispose items when filtered out.
            // Since items move between collections, we should ONLY dispose when removed from Source.
            // DynamicData's DisposeMany() on the SourceCache connects does that.
            .Subscribe()
            .DisposeWith(_subscriptions);

        // 1.1 Ongoing Downloads (Downloading/Searching state)
        sharedSource
            .Filter(x => x.State == PlaylistTrackState.Downloading || x.State == PlaylistTrackState.Searching)
            .SortAndBind(out _ongoingDownloads, SortExpressionComparer<UnifiedTrackViewModel>.Descending(x => x.State == PlaylistTrackState.Downloading).ThenByDescending(x => x.DownloadSpeed))
            .Subscribe()
            .DisposeWith(_subscriptions);

        // 1.2 Queued Downloads (Queued/Pending)
        sharedSource
            .Filter(x => x.State == PlaylistTrackState.Queued || x.State == PlaylistTrackState.Pending)
            .SortAndBind(out _queuedDownloads, SortExpressionComparer<UnifiedTrackViewModel>.Ascending(x => x.Model.Priority).ThenByAscending(x => x.Model.AddedAt))
            .Subscribe()
            .DisposeWith(_subscriptions);


        // Update counts
        _activeDownloads.ToObservableChangeSet()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => ActiveCount = _activeDownloads.Count)
            .DisposeWith(_subscriptions);

        // Fix: Subscribe to _queuedDownloads pipeline to capture in-place state changes (Downloading -> Pending)
        _queuedDownloads.ToObservableChangeSet()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => QueuedCount = _queuedDownloads.Count)
            .DisposeWith(_subscriptions);

        // Phase 2: Grouping Pipeline (Active Only)
        // Group by AlbumId (or null for Singles)
        sharedSource
            .Filter(x => x.IsActive || x.IsIndeterminate) // Only group active items
            .Group(x => x.Model.PlaylistId)
            .Transform((IGroup<UnifiedTrackViewModel, string, Guid> group) => new DownloadGroupViewModel(group))
            .DisposeMany() // Dispose GroupVMs when removed
            .SortAndBind(out _activeGroups, SortExpressionComparer<DownloadGroupViewModel>.Descending(x => x.LastActivity))
            .Subscribe()
            .DisposeWith(_subscriptions);

        // Completed Pipeline
        sharedSource
            .Filter(x => x.State == PlaylistTrackState.Completed)
            .SortAndBind(out _completedDownloads, SortExpressionComparer<UnifiedTrackViewModel>.Descending(x => x.Model.AddedAt))
            .Subscribe();

        _completedDownloads.ToObservableChangeSet()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => CompletedTodayCount = _completedDownloads.Count)
            .DisposeWith(_subscriptions);

        // Failed Pipeline
        sharedSource
            .Filter(x => x.State == PlaylistTrackState.Failed || x.State == PlaylistTrackState.Cancelled)
            .Bind(out _failedDownloads)
            .Subscribe();

        _failedDownloads.ToObservableChangeSet()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => FailedCount = _failedDownloads.Count)
            .DisposeWith(_subscriptions);


        // 2. Swimlane Pipelines (Derived from sharedSource filtered to Active)
        // Express: Priority 0
        sharedSource
            .Filter(x => (x.IsActive || x.IsIndeterminate) && x.Model.Priority == 0)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _expressItems)
            .Subscribe();

        // Standard: Priority 1-9
        sharedSource
             .Filter(x => (x.IsActive || x.IsIndeterminate) && x.Model.Priority >= 1 && x.Model.Priority < 10)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _standardItems)
            .Subscribe();

        // Background: Priority >= 10
        sharedSource
            .Filter(x => (x.IsActive || x.IsIndeterminate) && x.Model.Priority >= 10)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _backgroundItems)
            .Subscribe();

        sharedSource.Connect(); // Connect the publisher

        // Subscribe to creation events ONLY (State/Progress handled by Smart Component)
        _eventBus.GetEvent<TrackAddedEvent>()
            .Subscribe(OnTrackAdded)
            .DisposeWith(_subscriptions);
            
        // Used to catch removals (e.g. Delete command from within VM)
        _eventBus.GetEvent<TrackRemovedEvent>()
             .Subscribe(OnTrackRemoved)
             .DisposeWith(_subscriptions);
        
        // Start global speed calculator
        StartGlobalSpeedTimer();
        
        // CRITICAL: Hydrate from existing downloads (app restart scenario)
        InitialHydration();
    }
    
    private void InitialHydration()
    {
        var existingDownloads = _downloadManager.GetAllDownloads();
        
        foreach (var (model, state) in existingDownloads)
        {
            var fakeEvent = new TrackAddedEvent(model, state);
            OnTrackAdded(fakeEvent);
        }
    }
    
    private void OnTrackAdded(TrackAddedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var track = e.TrackModel;
            
            // Phase 2.5: Create Smart View Model
            var viewModel = new UnifiedTrackViewModel(track, _downloadManager, _eventBus, _artworkCache, _libraryService);
            
            // Set initial state override if needed
            if (e.InitialState.HasValue)
            {
                viewModel.State = e.InitialState.Value;
            }
            
            _downloadsSource.AddOrUpdate(viewModel);
        });
    }
    
    // New: Handle global removal
    private void OnTrackRemoved(TrackRemovedEvent e)
    {
         Dispatcher.UIThread.Post(() =>
        {
            _downloadsSource.Remove(e.TrackGlobalId);
        });
    }
    
    private void StartGlobalSpeedTimer()
    {
        var timer = new System.Timers.Timer(500);
        timer.Elapsed += (s, e) =>
        {
            try
            {
                var totalSpeedBytes = ActiveDownloads
                    .Where(d => d.State == Models.PlaylistTrackState.Downloading)
                    .Sum(d => d.CurrentSpeedBytes);
                    
                // Fix: VM needs to expose raw bytes/sec for accurate aggregation
                // Currently only exposes formatted string.
                // We'll rely on the VM's internal calculation or just iterate roughly.
                // Refactor: We can't access private _currentSpeed.
                // Let's assume 0 for now until we add public Double CurrentSpeedBytes property.
            }
            catch { }
        };
        timer.Start();
    }
    
    private async void PauseAll()
    {
        await _downloadManager.PauseAllAsync();
    }
    
    private async void ResumeAll()
    {
        // MANUAL START TRIGGER (User Request)
        if (!_downloadManager.IsRunning)
        {
            _ = _downloadManager.StartAsync();
            // Allow small delay for manager to spin up
            await Task.Delay(100);
        }

        foreach (var item in ActiveDownloads.Where(d => d.State == PlaylistTrackState.Paused).ToList())
        {
            item.ResumeCommand.Execute(null);
        }
    }
    
    public void Dispose()
    {
        _subscriptions.Dispose();
        _downloadsSource.Dispose();
    }
}
