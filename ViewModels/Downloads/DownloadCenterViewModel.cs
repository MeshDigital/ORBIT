using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;
using ReactiveUI;
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
    
    // Collections
    public ObservableCollection<DownloadItemViewModel> ActiveDownloads { get; } = new();
    public ObservableCollection<DownloadItemViewModel> CompletedDownloads { get; } = new();
    public ObservableCollection<DownloadItemViewModel> FailedDownloads { get; } = new();
    
    // Stats
    public int ActiveCount => ActiveDownloads.Count;
    public int QueuedCount => ActiveDownloads.Count(d => d.State == Models.PlaylistTrackState.Pending || d.State == Models.PlaylistTrackState.Queued);
    public int CompletedTodayCount => CompletedDownloads.Count; // Session-only as per user feedback
    
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
        
        ClearCompletedCommand = ReactiveCommand.Create(() => CompletedDownloads.Clear());
        ClearFailedCommand = ReactiveCommand.Create(() => FailedDownloads.Clear());
        
        // Subscribe to events (Reactive pattern: GetEvent<T>().Subscribe())
        _trackAddedSubscription = _eventBus.GetEvent<Events.TrackAddedEvent>()
            .Subscribe(OnTrackAdded);
        
        _trackStateChangedSubscription = _eventBus.GetEvent<Events.TrackStateChangedEvent>()
            .Subscribe(OnTrackStateChanged);
        
        _trackProgressChangedSubscription = _eventBus.GetEvent<Events.TrackProgressChangedEvent>()
            .Subscribe(OnTrackProgressChanged);
        
        // Start global speed calculator
        StartGlobalSpeedTimer();
    }
    
    /// <summary>
    /// Phase 2.5: Track added to download queue
    /// </summary>
    private void OnTrackAdded(Events.TrackAddedEvent e)
    {
        // CRITICAL: Marshal to UI thread for ObservableCollection modifications
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
                track.MinBitrateOverride
            );
            

            
            // Respect proper initial state (e.g. hydrate as Completed)
            viewModel.State = e.InitialState ?? Models.PlaylistTrackState.Pending;
            
            // Smart Insert: Active downloads on top, pending at bottom
            // This keeps the most relevant items (currently downloading/searching) visible
            if (viewModel.State == Models.PlaylistTrackState.Downloading || 
                viewModel.State == Models.PlaylistTrackState.Searching)
            {
                ActiveDownloads.Insert(0, viewModel); // Top of list
            }
            else
            {
                ActiveDownloads.Add(viewModel); // Bottom (queued/pending)
            }
            
            OnPropertyChanged(nameof(ActiveCount));
            OnPropertyChanged(nameof(QueuedCount));
        });
    }
    
    /// <summary>
    /// Phase 2.5: Track state changed (Pending → Downloading → Completed/Failed)
    /// Pro-tip: State-based tab switching - visually move tracks between collections
    /// </summary>
    private void OnTrackStateChanged(Events.TrackStateChangedEvent e)
    {
        // CRITICAL: Marshal to UI thread for ObservableCollection modifications
        Dispatcher.UIThread.Post(() =>
        {
            var item = ActiveDownloads.FirstOrDefault(d => d.GlobalId == e.TrackGlobalId);
            
            if (item != null)
            {
                var oldState = item.State;
                item.State = e.NewState; // Use NewState property
                
                // State-based tab switching
                if (e.NewState == Models.PlaylistTrackState.Completed)
                {
                    // Move from Active → Completed
                    ActiveDownloads.Remove(item);
                    CompletedDownloads.Insert(0, item); // Insert at top (most recent first)
                    
                    OnPropertyChanged(nameof(ActiveCount));
                    OnPropertyChanged(nameof(CompletedTodayCount));
                }
                else if (e.NewState == Models.PlaylistTrackState.Failed)
                {
                    // Move from Active → Failed
                    ActiveDownloads.Remove(item);
                    FailedDownloads.Insert(0, item);
                    
                    OnPropertyChanged(nameof(ActiveCount));
                }
                else if (e.NewState == Models.PlaylistTrackState.Pending && FailedDownloads.Contains(item))
                {
                    // Retry: Move from Failed → Active
                    FailedDownloads.Remove(item);
                    ActiveDownloads.Add(item);
                    
                    OnPropertyChanged(nameof(ActiveCount));
                }
                /* 
                // REMOVED: Aggressive sorting causes UI jumps. 
                // Allow the list to maintain its natural sort order (e.g. by Added Date).
                else if ((e.NewState == Models.PlaylistTrackState.Downloading || e.NewState == Models.PlaylistTrackState.Searching) && 
                         oldState != e.NewState)
                {
                    // Move to top of Active list
                    ActiveDownloads.Remove(item);
                    ActiveDownloads.Insert(0, item);
                }
                */
            }
            else
            {
                // Check if it's in Failed/Completed (edge case: user retries from those tabs)
                var failedItem = FailedDownloads.FirstOrDefault(d => d.GlobalId == e.TrackGlobalId);
                if (failedItem != null)
                {
                    failedItem.State = e.NewState;
                }
                
                var completedItem = CompletedDownloads.FirstOrDefault(d => d.GlobalId == e.TrackGlobalId);
                if (completedItem != null)
                {
                    completedItem.State = e.NewState;
                }
            }
            
            OnPropertyChanged(nameof(QueuedCount));
        });
    }
    
    /// <summary>
    /// Phase 2.5: Track progress updated (bytes received, speed calculation happens in DownloadItemViewModel)
    /// </summary>
    private void OnTrackProgressChanged(Events.TrackProgressChangedEvent e)
    {
        var item = ActiveDownloads.FirstOrDefault(d => d.GlobalId == e.TrackGlobalId);
        
        if (item != null)
        {
            item.Progress = e.Progress;
            item.BytesReceived = e.BytesReceived;
            item.TotalBytes = e.TotalBytes;
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
