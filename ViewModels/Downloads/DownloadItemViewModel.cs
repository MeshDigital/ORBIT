using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels.Downloads;

/// <summary>
/// Phase 2.5: Represents a single download track in the Download Center UI.
/// Tracks progress, speed, and provides per-track commands.
/// </summary>
public class DownloadItemViewModel : INotifyPropertyChanged
{
    private readonly DownloadManager _downloadManager;
    
    // Core Properties
    public string GlobalId { get; }
    public string TrackTitle { get; }
    public string ArtistName { get; }
    public string AlbumName { get; }
    public string? AlbumArtUrl { get; }
    
    // Progress Tracking
    private double _progress;
    public double Progress
    {
        get => _progress;
        set
        {
            if (Math.Abs(_progress - value) > 0.01)
            {
                _progress = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressPercentText));
            }
        }
    }
    
    private long _bytesReceived;
    public long BytesReceived
    {
        get => _bytesReceived;
        set
        {
            if (_bytesReceived != value)
            {
                // Phase 2.5: Sliding window speed calculation
                RecordProgressSample(value);
                _bytesReceived = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BytesDisplay));
                OnPropertyChanged(nameof(SpeedDisplay));
            }
        }
    }
    
    private long _totalBytes;
    public long TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (_totalBytes != value)
            {
                _totalBytes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BytesDisplay));
            }
        }
    }
    
    private bool _isResuming;
    public bool IsResuming
    {
        get => _isResuming;
        set
        {
            if (_isResuming != value)
            {
                _isResuming = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }
    
    private PlaylistTrackState _state;
    public PlaylistTrackState State
    {
        get => _state;
        set
        {
            if (_state != value)
            {
                _state = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(CanPause));
                OnPropertyChanged(nameof(CanResume));
                OnPropertyChanged(nameof(CanRetry));
                OnPropertyChanged(nameof(CanCancel));
            }
        }
    }
    
    // Phase 13: Per-Track Search Filter Properties
    private string[] _allowedFormats = new[] { "mp3", "flac" };
    public string[] AllowedFormats
    {
        get => _allowedFormats;
        set
        {
            if (_allowedFormats != value)
            {
                _allowedFormats = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FilterSummary));
                SyncFiltersToManager();
            }
        }
    }
    
    private int _minBitrate = 192;
    public int MinBitrate
    {
        get => _minBitrate;
        set
        {
            if (_minBitrate != value)
            {
                _minBitrate = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FilterSummary));
                SyncFiltersToManager();
            }
        }
    }

    public string AllowedFormatsString
    {
        get => string.Join(", ", AllowedFormats);
        set 
        {
            var formats = value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                               .Select(f => f.Trim().ToLower())
                               .Distinct()
                               .ToArray();
            AllowedFormats = formats;
            OnPropertyChanged();
        }
    }
    
    private void SyncFiltersToManager()
    {
        // Fire and forget update to manager/persistence
        _ = Task.Run(async () => {
            await _downloadManager.UpdateTrackFiltersAsync(GlobalId, string.Join(",", AllowedFormats), MinBitrate);
        });
    }
    
    private DownloadMode _searchMode = DownloadMode.Normal;
    public DownloadMode SearchMode
    {
        get => _searchMode;
        set
        {
            if (_searchMode != value)
            {
                _searchMode = value;
                OnPropertyChanged();
            }
        }
    }
    
    private bool _isFilterExpanded = false;
    public bool IsFilterExpanded
    {
        get => _isFilterExpanded;
        set
        {
            if (_isFilterExpanded != value)
            {
                _isFilterExpanded = value;
                OnPropertyChanged();
            }
        }
    }
    
    public string FilterSummary => $"{string.Join(", ", AllowedFormats)} • {MinBitrate}+ kbps";
    
    // Phase 2.5: Sliding Window Speed Calculation (Last 2 seconds, 5 samples)
    private readonly Queue<(DateTime Time, long Bytes)> _progressSamples = new();
    private const int MaxSamples = 5;
    private const double SlidingWindowSeconds = 2.0;
    
    private void RecordProgressSample(long bytes)
    {
        var now = DateTime.Now;
        _progressSamples.Enqueue((now, bytes));
        
        // Remove samples older than 2 seconds
        while (_progressSamples.Count > 0)
        {
            var oldest = _progressSamples.Peek();
            if ((now - oldest.Time).TotalSeconds > SlidingWindowSeconds)
                _progressSamples.Dequeue();
            else
                break;
        }
        
        // Keep max 5 samples
        while (_progressSamples.Count > MaxSamples)
            _progressSamples.Dequeue();
            
        CalculateSpeed();
    }
    
    // Computed Properties
    public string BytesDisplay
    {
        get
        {
            if (TotalBytes == 0) return "Unknown size";
            var receivedMB = BytesReceived / 1024.0 / 1024.0;
            var totalMB = TotalBytes / 1024.0 / 1024.0;
            return $"{receivedMB:F1} MB / {totalMB:F1} MB";
        }
    }
    
    public string ProgressPercentText => $"{Progress:F0}%";
    
    public double CurrentSpeed { get; private set; } // Bytes per second

    public string SpeedDisplay
    {
        get
        {
            var speed = CurrentSpeed;
            var mbPerSecond = speed / 1024.0 / 1024.0;
            
            return mbPerSecond >= 1.0 
                ? $"{mbPerSecond:F1} MB/s" 
                : $"{mbPerSecond * 1024:F0} KB/s";
        }
    }

    private void CalculateSpeed()
    {
        if (_progressSamples.Count < 2) 
        {
            CurrentSpeed = 0;
            return;
        }
        
        var samples = _progressSamples.ToArray();
        var oldest = samples[0];
        var newest = samples[^1];
        
        var timeDelta = (newest.Time - oldest.Time).TotalSeconds;
        if (timeDelta < 0.1) 
        {
            CurrentSpeed = 0;
            return;
        }
        
        var bytesDelta = newest.Bytes - oldest.Bytes;
        // Prevent negative speed if bytes jumped back (e.g. retry)
        if (bytesDelta < 0) bytesDelta = 0; 
        
        CurrentSpeed = bytesDelta / timeDelta;
    }
    
    public string StatusText
    {
        get
        {
            if (IsResuming) return "Resuming...";
            return State switch
            {
                PlaylistTrackState.Searching => "Searching...",
                PlaylistTrackState.Downloading => "Downloading",
                PlaylistTrackState.Queued => "Queued",
                PlaylistTrackState.Paused => "Paused",
                PlaylistTrackState.Failed => "Failed",
                PlaylistTrackState.Completed => "Completed",
                _ => State.ToString()
            };
        }
    }
    
    // Commands
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand ToggleFilterCommand { get; }
    public ICommand RetryWithFiltersCommand { get; }
    public ICommand ApplyPresetCommand { get; }
    
    // Command CanExecute
    public bool CanPause => State == PlaylistTrackState.Downloading || State == PlaylistTrackState.Searching;
    public bool CanResume => State == PlaylistTrackState.Paused;
    public bool CanRetry => State == PlaylistTrackState.Failed;
    public bool CanCancel => State != PlaylistTrackState.Completed && State != PlaylistTrackState.Cancelled;
    
    public DownloadItemViewModel(
        string globalId,
        string trackTitle,
        string artistName,
        string albumName,
        string? albumArtUrl,
        DownloadManager downloadManager,
        string? preferredFormats = null,
        int? minBitrate = null)
    {
        GlobalId = globalId;
        TrackTitle = trackTitle;
        ArtistName = artistName;
        AlbumName = albumName;
        AlbumArtUrl = albumArtUrl;
        _downloadManager = downloadManager;

        if (!string.IsNullOrWhiteSpace(preferredFormats))
        {
            _allowedFormats = preferredFormats.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                             .Select(f => f.Trim().ToLower())
                                             .ToArray();
        }

        if (minBitrate.HasValue)
        {
            _minBitrate = minBitrate.Value;
        }
        
        // Initialize commands (ReactiveCommand)
        PauseCommand = ReactiveCommand.CreateFromTask(
            async () => await _downloadManager.PauseTrackAsync(GlobalId),
            this.WhenAnyValue(x => x.CanPause));
        
        ResumeCommand = ReactiveCommand.CreateFromTask(
            async () => await _downloadManager.ResumeTrackAsync(GlobalId),
            this.WhenAnyValue(x => x.CanResume));
        
        CancelCommand = ReactiveCommand.Create(
            () => _downloadManager.CancelTrack(GlobalId),
            this.WhenAnyValue(x => x.CanCancel));
            
        RetryCommand = ReactiveCommand.Create(
            () => _downloadManager.HardRetryTrack(GlobalId),
            this.WhenAnyValue(x => x.CanRetry));
        
        // Phase 13: Filter Commands
        ToggleFilterCommand = ReactiveCommand.Create(() => IsFilterExpanded = !IsFilterExpanded);
        RetryWithFiltersCommand = ReactiveCommand.Create(() => {
            SyncFiltersToManager(); // Ensure filters are synced
            _downloadManager.HardRetryTrack(GlobalId);
        });
        ApplyPresetCommand = ReactiveCommand.Create<string>(preset => {
            // TODO: Implement preset application
            switch (preset)
            {
                case "Strict":
                    AllowedFormats = new[] { "flac" };
                    MinBitrate = 320;
                    break;
                case "Balanced":
                    AllowedFormats = new[] { "mp3", "flac" };
                    MinBitrate = 192;
                    break;
                case "Relaxed":
                    AllowedFormats = new[] { "mp3", "flac", "m4a", "ogg" };
                    MinBitrate = 128;
                    break;
            }
        });
    }
    
    public event PropertyChangedEventHandler? PropertyChanged;
    
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
