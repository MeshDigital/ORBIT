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
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels.Downloads;

/// <summary>
/// Phase 2.5: Global Download Center - Singleton observer that tracks all downloads.
/// Manages Active, Completed, and Failed collections with real-time event subscriptions.
/// </summary>
public class DownloadCenterViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly DownloadManager _downloadManager;
    private readonly IEventBus _eventBus;
    private readonly DateTime _sessionStart = DateTime.Now;
    private readonly IDisposable _trackAddedSubscription;
    private readonly IDisposable _trackStateChangedSubscription;
    private readonly IDisposable _trackProgressChangedSubscription;
    private readonly CompositeDisposable _subscriptions = new();
    
    // Collections (DynamicData Source)
    private readonly SourceCache<DownloadItemViewModel, string> _downloadsSource = new(x => x.GlobalId);

    // Public ReadOnly Collections (Bound to UI)
    private readonly ReadOnlyObservableCollection<DownloadItemViewModel> _activeDownloads;
    public ReadOnlyObservableCollection<DownloadItemViewModel> ActiveDownloads => _activeDownloads;

    private readonly ReadOnlyObservableCollection<DownloadItemViewModel> _completedDownloads;
    public ReadOnlyObservableCollection<DownloadItemViewModel> CompletedDownloads => _completedDownloads;

    private readonly ReadOnlyObservableCollection<DownloadItemViewModel> _failedDownloads;
    public ReadOnlyObservableCollection<DownloadItemViewModel> FailedDownloads => _failedDownloads;

    // Swimlanes (Derived from Active)
    private readonly ReadOnlyObservableCollection<DownloadItemViewModel> _expressItems;
    public ReadOnlyObservableCollection<DownloadItemViewModel> ExpressItems => _expressItems;

    private readonly ReadOnlyObservableCollection<DownloadItemViewModel> _standardItems;
    public ReadOnlyObservableCollection<DownloadItemViewModel> StandardItems => _standardItems;

    private readonly ReadOnlyObservableCollection<DownloadItemViewModel> _backgroundItems;
    public ReadOnlyObservableCollection<DownloadItemViewModel> BackgroundItems => _backgroundItems;

    // Stats
    public int ActiveCount => ActiveDownloads.Count;
    public int QueuedCount => ActiveDownloads.Count(d => d.State == Models.PlaylistTrackState.Pending || d.State == Models.PlaylistTrackState.Queued);
    public int CompletedTodayCount => CompletedDownloads.Count;
    
    private string _globalSpeed = "0 MB/s";
    public string GlobalSpeed
    {
        get => _globalSpeed;
        set
        {
            if (_globalSpeed != value)
            {
                _globalSpeed = value;
                OnPropertyChanged();
            }
        }
    }
    
    // Alias for HomeViewModel compatibility
    public string GlobalSpeedDisplay => GlobalSpeed;
    
    // Commands
    public ICommand PauseAllCommand { get; }
    public ICommand ResumeAllCommand { get; }
    public ICommand ClearCompletedCommand { get; }
    public ICommand ClearFailedCommand { get; }
    
    public DownloadCenterViewModel(DownloadManager downloadManager, IEventBus eventBus)
    {
        _downloadManager = downloadManager;
        _eventBus = eventBus;
        
        // Initialize commands (ReactiveCommand)
        PauseAllCommand = ReactiveCommand.Create(PauseAll, 
            this.WhenAnyValue(x => x.ActiveDownloads.Count, count => count > 0));
        
        ResumeAllCommand = ReactiveCommand.Create(ResumeAll,
            this.WhenAnyValue(x => x.ActiveDownloads.Count, count => count > 0));
        
        ClearCompletedCommand = ReactiveCommand.Create(() => 
            _downloadsSource.Remove(_downloadsSource.Items.Where(x => x.State == PlaylistTrackState.Completed).ToList()));
        
        ClearFailedCommand = ReactiveCommand.Create(() => 
            _downloadsSource.Remove(_downloadsSource.Items.Where(x => x.State == PlaylistTrackState.Failed).ToList()));
        
        // Initialize DynamicData Pipelines
        
        // 1. Base Pipeline (Active vs Completed vs Failed)
        var sharedSource = _downloadsSource.Connect()
            .AutoRefresh(x => x.State) // Logic re-evaluates when State changes
            .AutoRefresh(x => x.Priority) // Logic re-evaluates when Priority changes (VIP Pass)
            .Publish(); // Share subscription

        // Active Pipeline
        sharedSource
            .Filter(x => x.State != PlaylistTrackState.Completed && x.State != PlaylistTrackState.Failed)
            .Sort(SortExpressionComparer<DownloadItemViewModel>.Descending(x => x.State == PlaylistTrackState.Downloading)
                .ThenByAscending(x => x.Priority)) // Sort by Priority then (implementation detail)
            .Bind(out _activeDownloads)
            .Subscribe()
            .DisposeWith(_subscriptions); // Need a CompositeDisposable ideally, but skipping for brevity

        // Completed Pipeline
        sharedSource
            .Filter(x => x.State == PlaylistTrackState.Completed)
            .Sort(SortExpressionComparer<DownloadItemViewModel>.Descending(x => x.GlobalId)) // Sort by ID/Time
            .Bind(out _completedDownloads)
            .Subscribe();

        // Failed Pipeline
        sharedSource
            .Filter(x => x.State == PlaylistTrackState.Failed)
            .Bind(out _failedDownloads)
            .Subscribe();

        // 2. Swimlane Pipelines (Derived from sharedSource filtered to Active)
        // Express: Priority 0
        sharedSource
            .Filter(x => x.State != PlaylistTrackState.Completed && x.State != PlaylistTrackState.Failed && x.Priority == 0)
            .ObserveOn(RxApp.MainThreadScheduler) // Throttle updates to UI
            .Bind(out _expressItems)
            .Subscribe();

        // Standard: Priority 1-9
        sharedSource
            .Filter(x => x.State != PlaylistTrackState.Completed && x.State != PlaylistTrackState.Failed && x.Priority >= 1 && x.Priority < 10)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _standardItems)
            .Subscribe();

        // Background: Priority >= 10
        sharedSource
            .Filter(x => x.State != PlaylistTrackState.Completed && x.State != PlaylistTrackState.Failed && x.Priority >= 10)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _backgroundItems)
            .Subscribe();

        sharedSource.Connect(); // Connect the publisher

        // Subscribe to events (Reactive pattern: GetEvent<T>().Subscribe())
        _trackAddedSubscription = _eventBus.GetEvent<TrackAddedEvent>()
            .Subscribe(OnTrackAdded);
        
        _trackStateChangedSubscription = _eventBus.GetEvent<Events.TrackStateChangedEvent>()
            .Subscribe(OnTrackStateChanged);
        
        _trackProgressChangedSubscription = _eventBus.GetEvent<Events.TrackProgressChangedEvent>()
            .Subscribe(OnTrackProgressChanged);
        
        // Start global speed calculator
        StartGlobalSpeedTimer();
        
        // CRITICAL: Hydrate from existing downloads (app restart scenario)
        InitialHydration();
    }
    
    /// <summary>
    /// Hydrates the ViewModel collections from DownloadManager's existing downloads.
    /// This handles the case where tracks were loaded from DB before this ViewModel was created.
    /// </summary>
    private void InitialHydration()
    {
        var existingDownloads = _downloadManager.GetAllDownloads();
        
        foreach (var (model, state) in existingDownloads)
        {
            // Reuse the OnTrackAdded logic
            var fakeEvent = new TrackAddedEvent(model, state);
            OnTrackAdded(fakeEvent);
        }
    }
    
    /// <summary>
    /// Phase 2.5: Track added to download queue
    /// <summary>
    /// Phase 2.5: Track added to download queue
    /// </summary>
    private void OnTrackAdded(TrackAddedEvent e)
    {
        // Marshal unnecessary for source cache update, but safe
        Dispatcher.UIThread.Post(() =>
        {
            var track = e.TrackModel;
            
            var viewModel = new DownloadItemViewModel(
                track.TrackUniqueHash,
                track.Title,
                track.Artist,
                track.Album ?? "Unknown Album",
                track.AlbumArtUrl,
                _downloadManager,
                track.PreferredFormats,
                track.MinBitrateOverride,
                track.Priority, // Phase 3C: Pass Priority
                0               // Score starts at 0, updated via event if needed
            );
            
            // Respect proper initial state (e.g. hydrate as Completed)
            viewModel.State = e.InitialState ?? Models.PlaylistTrackState.Pending;
            
            _downloadsSource.AddOrUpdate(viewModel);
            
            OnPropertyChanged(nameof(ActiveCount));
            OnPropertyChanged(nameof(QueuedCount));
        });
    }
    
    /// <summary>
    /// Phase 2.5: Track state changed
    /// </summary>
    private void OnTrackStateChanged(Events.TrackStateChangedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var item = _downloadsSource.Lookup(e.TrackGlobalId);
            
            if (item.HasValue)
            {
                item.Value.State = e.NewState;
                
                // Phase 12.6: Wire failure reason to UI
                if (e.NewState == PlaylistTrackState.Failed && !string.IsNullOrEmpty(e.ErrorMessage))
                {
                    item.Value.FailureReason = e.ErrorMessage;
                }
                else if (e.NewState != PlaylistTrackState.Failed)
                {
                    item.Value.FailureReason = null; // Clear on state change
                }
                // DynamicData AutoRefresh handles moving between collections
            }
            
            OnPropertyChanged(nameof(ActiveCount));
            OnPropertyChanged(nameof(CompletedTodayCount));
            OnPropertyChanged(nameof(QueuedCount));
        });
    }
    
    /// <summary>
    /// Phase 2.5: Track progress updated
    /// </summary>
    private void OnTrackProgressChanged(Events.TrackProgressChangedEvent e)
    {
         var item = _downloadsSource.Lookup(e.TrackGlobalId);
         
         if (item.HasValue)
         {
             item.Value.Progress = e.Progress;
             item.Value.BytesReceived = e.BytesReceived;
             item.Value.TotalBytes = e.TotalBytes;
         }
    }
    
    /// <summary>
    /// Phase 2.5: Calculate global download speed (sum of all active tracks)
    /// </summary>
    private void StartGlobalSpeedTimer()
    {
        // Update global speed every 500ms
        var timer = new System.Timers.Timer(500);
        timer.Elapsed += (s, e) =>
        {
            try
            {
                // Sum speeds from all active downloading tracks
                // Sum speeds from all active downloading tracks (robust double summation)
                var totalSpeedBytes = ActiveDownloads
                    .Where(d => d.State == Models.PlaylistTrackState.Downloading)
                    .Sum(d => d.CurrentSpeed);

                var mbps = totalSpeedBytes / 1024.0 / 1024.0;
                
                GlobalSpeed = mbps >= 1.0 
                    ? $"{mbps:F1} MB/s" 
                    : $"{mbps * 1024:F0} KB/s";
            }
            catch
            {
                // Ignore parsing errors
            }
        };
        timer.Start();
    }
    
    /// <summary>
    /// Pause all downloading tracks
    /// </summary>
    private async void PauseAll()
    {
        // Use the manager's atomic method
        await _downloadManager.PauseAllAsync();
    }
    
    /// <summary>
    /// Resume all paused tracks
    /// </summary>
    private async void ResumeAll()
    {
        foreach (var item in ActiveDownloads.Where(d => d.CanResume).ToList())
        {
            await _downloadManager.ResumeTrackAsync(item.GlobalId);
        }
    }
    
    public void Dispose()
    {
        // Unsubscribe from events (dispose subscriptions)
        _trackAddedSubscription?.Dispose();
        _trackStateChangedSubscription?.Dispose();
        _trackProgressChangedSubscription?.Dispose();
    }
    
    public event PropertyChangedEventHandler? PropertyChanged;
    
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
