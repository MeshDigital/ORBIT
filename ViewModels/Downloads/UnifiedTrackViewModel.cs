using System;
using System.Reactive.Disposables;
using System.Windows.Input;
using Avalonia.Threading;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Events;

namespace SLSKDONET.ViewModels.Downloads;

/// <summary>
/// Phase 2.5 & 12.6: Unified Track ViewModel for Download Center and Lists.
/// Implements "Smart Component" architecture - self-managing state via EventBus.
/// </summary>
public class UnifiedTrackViewModel : ReactiveObject, IDisplayableTrack, IDisposable
{
    private readonly DownloadManager _downloadManager;
    private readonly IEventBus _eventBus;
    private readonly CompositeDisposable _disposables = new();

    // Core Data
    public PlaylistTrack Model { get; }
    
    public UnifiedTrackViewModel(
        PlaylistTrack model, 
        DownloadManager downloadManager, 
        IEventBus eventBus)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _downloadManager = downloadManager ?? throw new ArgumentNullException(nameof(downloadManager));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));

        // Initialize State from Model
        _state = (PlaylistTrackState)model.Status; // Best effort mapping if simple cast works, otherwise logic needed
        // Fix: Model.Status is TrackStatus enum, State is PlaylistTrackState.
        // We'll trust the caller (DownloadCenter) to set initial state or wait for event.
        // But for display, we map roughly:
        if (model.Status == TrackStatus.Downloaded) _state = PlaylistTrackState.Completed;
        else if (model.Status == TrackStatus.Failed) _state = PlaylistTrackState.Failed;
        else _state = PlaylistTrackState.Pending;

        // Initialize Commands
        PlayCommand = ReactiveCommand.Create(PlayTrack, this.WhenAnyValue(x => x.IsCompleted));
        
        RevealFileCommand = ReactiveCommand.Create(() => 
        {
            // TODO: Implement file reveal service
        }, this.WhenAnyValue(x => x.IsCompleted));

        AddToProjectCommand = ReactiveCommand.Create(() => 
        {
            // TODO: Trigger "Add to Project" dialog
        }, this.WhenAnyValue(x => x.IsCompleted));

        PauseCommand = ReactiveCommand.CreateFromTask(async () => 
            await _downloadManager.PauseTrackAsync(GlobalId),
            this.WhenAnyValue(x => x.IsActive));

        ResumeCommand = ReactiveCommand.CreateFromTask(async () => 
            await _downloadManager.ResumeTrackAsync(GlobalId),
            this.WhenAnyValue(x => x.State, s => s == PlaylistTrackState.Paused));

        CancelCommand = ReactiveCommand.Create(() => 
            _downloadManager.CancelTrack(GlobalId),
            this.WhenAnyValue(x => x.IsActive));

        RetryCommand = ReactiveCommand.Create(() => 
            _downloadManager.HardRetryTrack(GlobalId),
            this.WhenAnyValue(x => x.IsFailed));
            
        CleanCommand = ReactiveCommand.Create(() =>
        {
             // Handled by parent collection usually, but could arguably be here if we had a Delete service method
             // For now, this command might just be a placeholder or call a service to remove self
             _downloadManager.DeleteTrackFromDiskAndHistoryAsync(GlobalId);
        }, this.WhenAnyValue(x => x.IsCompleted, x => x.IsFailed, (c, f) => c || f));

        // Subscribe to Events
        _eventBus.GetEvent<TrackStateChangedEvent>()
            .Subscribe(OnStateChanged)
            .DisposeWith(_disposables);

        _eventBus.GetEvent<TrackProgressChangedEvent>()
            .Subscribe(OnProgressChanged)
            .DisposeWith(_disposables);

        _eventBus.GetEvent<TrackMetadataUpdatedEvent>()
            .Subscribe(OnMetadataUpdated)
            .DisposeWith(_disposables);
            
         // Initialize sliding window for speed
         _lastProgressTime = DateTime.MinValue;
    }

    // IDisplayableTrack Implementation
    public string GlobalId => Model.TrackUniqueHash;
    public string ArtistName => Model.Artist ?? "Unknown Artist";
    public string TrackTitle => Model.Title ?? "Unknown Title";
    public string AlbumName => Model.Album ?? "Unknown Album";
    public string? AlbumArtUrl => Model.AlbumArtUrl;

    private PlaylistTrackState _state;
    public PlaylistTrackState State
    {
        get => _state;
        set { 
            if (_state != value)
            {
                this.RaiseAndSetIfChanged(ref _state, value);
                this.RaisePropertyChanged(nameof(StatusText));
                this.RaisePropertyChanged(nameof(IsIndeterminate));
                this.RaisePropertyChanged(nameof(IsFailed));
                this.RaisePropertyChanged(nameof(IsActive));
                this.RaisePropertyChanged(nameof(IsCompleted));
                this.RaisePropertyChanged(nameof(TechnicalSummary));
            }
        }
    }

    public string StatusText => State switch
    {
        PlaylistTrackState.Completed => "Ready",
        PlaylistTrackState.Downloading => $"{(int)(Progress)}%",
        PlaylistTrackState.Searching => "Searching...",
        PlaylistTrackState.Queued => "Queued",
        PlaylistTrackState.Failed => !string.IsNullOrEmpty(FailureReason) ? FailureReason : "Failed",
        PlaylistTrackState.Paused => "Paused",
        _ => State.ToString()
    };

    private double _progress;
    public double Progress
    {
        get => _progress;
        set => this.RaiseAndSetIfChanged(ref _progress, value);
    }

    public bool IsIndeterminate => State == PlaylistTrackState.Searching || State == PlaylistTrackState.Queued;
    public bool IsFailed => State == PlaylistTrackState.Failed || State == PlaylistTrackState.Cancelled;
    public bool IsActive => State == PlaylistTrackState.Downloading || State == PlaylistTrackState.Searching;
    public bool IsCompleted => State == PlaylistTrackState.Completed;

    private string? _failureReason;
    public string? FailureReason
    {
        get => _failureReason;
        set 
        {
            this.RaiseAndSetIfChanged(ref _failureReason, value);
            this.RaisePropertyChanged(nameof(StatusText));
        }
    }

    public string TechnicalSummary
    {
        get
        {
            // "Soulseek • 320kbps • 12MB • [Time]"
            var parts = new System.Collections.Generic.List<string>();
            parts.Add("Soulseek"); // Source (Static for now)
            
            if (Model.Bitrate.HasValue) parts.Add($"{Model.Bitrate}kbps");
            if (!string.IsNullOrEmpty(Model.Format)) parts.Add(Model.Format.ToUpper());
            
            if (_totalBytes > 0) 
                parts.Add($"{_totalBytes / 1024.0 / 1024.0:F1} MB");
                
            if (IsCompleted)
                parts.Add(DateTime.Now.ToShortTimeString()); // Placeholder for completion time if not tracked
            else if (IsActive)
                parts.Add(SpeedDisplay);

            return string.Join(" • ", parts);
        }
    }

    // Curation Hub Properties
    public double IntegrityScore => Model.QualityConfidence ?? 0.0;
    public bool IsSecure => IntegrityScore > 0.9;
    
    public string BpmDisplay => Model.BPM.HasValue ? $"{Model.BPM:0}" : "—";
    public string KeyDisplay => Model.MusicalKey ?? "—";
    
    // Commands
    public ICommand PlayCommand { get; }
    public ICommand RevealFileCommand { get; }
    public ICommand AddToProjectCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand CleanCommand { get; }

    // Internal State
    private long _totalBytes;
    private long _bytesReceived;
    private double _currentSpeed;
    private DateTime _lastProgressTime;
    
    public double CurrentSpeedBytes => _currentSpeed;

    public string SpeedDisplay => _currentSpeed > 1024 * 1024 
        ? $"{_currentSpeed / 1024 / 1024:F1} MB/s" 
        : $"{_currentSpeed / 1024:F0} KB/s";

    // Event Handlers
    private void OnStateChanged(TrackStateChangedEvent e)
    {
        if (e.TrackGlobalId != GlobalId) return;
        
        Dispatcher.UIThread.Post(() => {
            State = e.State;
        FailureReason = e.Error;
        });
    }

    private void OnProgressChanged(TrackProgressChangedEvent e)
    {
        if (e.TrackGlobalId != GlobalId) return;
        
        Dispatcher.UIThread.Post(() => {
             Progress = e.Progress;
             _totalBytes = e.TotalBytes;
             
             // Speed Calc
             var now = DateTime.UtcNow;
             if (_lastProgressTime != DateTime.MinValue)
             {
                 var seconds = (now - _lastProgressTime).TotalSeconds;
                 if (seconds > 0)
                 {
                     var bytesDiff = e.BytesReceived - _bytesReceived;
                     if (bytesDiff > 0)
                     {
                         var instantSpeed = bytesDiff / seconds;
                         // Simple smoothing
                         _currentSpeed = (_currentSpeed * 0.7) + (instantSpeed * 0.3); 
                         this.RaisePropertyChanged(nameof(TechnicalSummary));
                     }
                 }
             }
             _bytesReceived = e.BytesReceived;
             _lastProgressTime = now;
             
             this.RaisePropertyChanged(nameof(StatusText));
        });
    }

    private void OnMetadataUpdated(TrackMetadataUpdatedEvent e)
    {
        if (e.TrackGlobalId != GlobalId) return;
        
        Dispatcher.UIThread.Post(() => {
            this.RaisePropertyChanged(nameof(ArtistName));
            this.RaisePropertyChanged(nameof(TrackTitle));
            this.RaisePropertyChanged(nameof(AlbumName));
            this.RaisePropertyChanged(nameof(AlbumArtUrl));
            this.RaisePropertyChanged(nameof(BpmDisplay));
            this.RaisePropertyChanged(nameof(KeyDisplay));
            this.RaisePropertyChanged(nameof(IntegrityScore));
        });
    }
    
    private void PlayTrack()
    {
        // Construct a lightweight VM payload for the player
        // The Player expects a PlaylistTrackViewModel, ensuring it has the Model
        var payload = new PlaylistTrackViewModel(Model);
        
        // Publish event
        _eventBus.Publish(new Models.PlayTrackRequestEvent(payload));
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}
