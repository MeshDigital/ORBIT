
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input; // For ICommand
using System.Windows.Media; // For Brush
using SLSKDONET.Models;
using SLSKDONET.Views; // For RelayCommand

namespace SLSKDONET.ViewModels;

public enum PlaylistTrackState
{
    Pending,
    Searching,
    Queued,
    Downloading,
    Paused,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// ViewModel representing a track in the download queue.
/// Manages state, progress, and updates for the UI.
/// </summary>
public class PlaylistTrackViewModel : INotifyPropertyChanged
{
    private PlaylistTrackState _state;
    private double _progress;
    private string _currentSpeed = string.Empty;
    private string? _errorMessage;
    private string? _coverArtUrl;

    private int _sortOrder;
    public DateTime AddedAt { get; } = DateTime.Now;

    public int SortOrder 
    {
        get => _sortOrder;
        set
        {
             if (_sortOrder != value)
             {
                 _sortOrder = value;
                 OnPropertyChanged();
                 // Propagate to Model
                 if (Model != null) Model.TrackNumber = value;
             }
        }
    }

    public Guid SourceId { get; set; } // Project ID (PlaylistJob.Id)
    public string GlobalId { get; set; } // TrackUniqueHash
    
    // Properties linked to Model and Notification
    public string Artist 
    { 
        get => Model.Artist ?? string.Empty;
        set
        {
            if (Model.Artist != value)
            {
                Model.Artist = value;
                OnPropertyChanged();
            }
        }
    }

    public string Title 
    { 
        get => Model.Title ?? string.Empty;
        set
        {
            if (Model.Title != value)
            {
                Model.Title = value;
                OnPropertyChanged();
            }
        }
    }

    public string Album
    {
        get => Model.Album ?? string.Empty;
        set
        {
            if (Model.Album != value)
            {
                Model.Album = value;
                OnPropertyChanged();
            }
        }
    }
    
    // Reference to the underlying model if needed for persistence later
    public PlaylistTrack Model { get; private set; }

    // Cancellation token source for this specific track's operation
    public System.Threading.CancellationTokenSource? CancellationTokenSource { get; set; }

    // Commands
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand FindNewVersionCommand { get; }

    public PlaylistTrackViewModel(PlaylistTrack track)
    {
        Model = track;
        SourceId = track.PlaylistId;
        GlobalId = track.TrackUniqueHash;
        Artist = track.Artist;
        Title = track.Title;
        SortOrder = track.TrackNumber; // Initialize SortOrder
        State = PlaylistTrackState.Pending;
        
        // Map initial status from model
        if (track.Status == TrackStatus.Downloaded)
        {
            State = PlaylistTrackState.Completed;
            Progress = 1.0;
        }

        PauseCommand = new RelayCommand(Pause, () => CanPause);
        ResumeCommand = new RelayCommand(Resume, () => CanResume);
        CancelCommand = new RelayCommand(Cancel, () => CanCancel);
        FindNewVersionCommand = new RelayCommand(FindNewVersion, () => CanHardRetry);
    }

    public PlaylistTrackState State
    {
        get => _state;
        set
        {
            if (_state != value)
            {
                _state = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(StatusColor));
                
                // CommandManager.InvalidateRequerySuggested() happens automatically or via interaction
            }
        }
    }

    public double Progress
    {
        get => _progress;
        set
        {
            if (Math.Abs(_progress - value) > 0.001)
            {
                _progress = value;
                OnPropertyChanged();
            }
        }
    }

    public string CurrentSpeed
    {
        get => _currentSpeed;
        set
        {
            if (_currentSpeed != value)
            {
                _currentSpeed = value;
                OnPropertyChanged();
            }
        }
    }
    
    public string? ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (_errorMessage != value)
            {
                _errorMessage = value;
                OnPropertyChanged();
            }
        }
    }

    public string? CoverArtUrl
    {
        get => _coverArtUrl;
        set
        {
            if (_coverArtUrl != value)
            {
                _coverArtUrl = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsActive => State == PlaylistTrackState.Searching || 
                           State == PlaylistTrackState.Downloading || 
                           State == PlaylistTrackState.Queued;

    // Computed Properties for Logic
    public bool CanPause => State == PlaylistTrackState.Downloading || State == PlaylistTrackState.Queued || State == PlaylistTrackState.Searching;
    public bool CanResume => State == PlaylistTrackState.Paused;
    public bool CanCancel => State != PlaylistTrackState.Completed && State != PlaylistTrackState.Cancelled;
    public bool CanHardRetry => State == PlaylistTrackState.Failed || State == PlaylistTrackState.Cancelled; // Or Completed if we want to re-download
    public bool CanDeleteFile => State == PlaylistTrackState.Completed || State == PlaylistTrackState.Failed || State == PlaylistTrackState.Cancelled;

    // Visuals
    public System.Windows.Media.Brush StatusColor
    {
        get
        {
            return State switch
            {
                PlaylistTrackState.Completed => System.Windows.Media.Brushes.LightGreen,
                PlaylistTrackState.Downloading => System.Windows.Media.Brushes.DeepSkyBlue,
                PlaylistTrackState.Searching => System.Windows.Media.Brushes.CornflowerBlue,
                PlaylistTrackState.Queued => System.Windows.Media.Brushes.Cyan,
                PlaylistTrackState.Paused => System.Windows.Media.Brushes.Orange,
                PlaylistTrackState.Failed => System.Windows.Media.Brushes.Red,
                PlaylistTrackState.Cancelled => System.Windows.Media.Brushes.Gray,
                _ => System.Windows.Media.Brushes.LightGray
            };
        }
    }

    // Actions
    public void Pause()
    {
        if (CanPause)
        {
            // Cancel current work but set state to Paused instead of Cancelled
            CancellationTokenSource?.Cancel();
            State = PlaylistTrackState.Paused;
            CurrentSpeed = "Paused";
        }
    }

    public void Resume()
    {
        if (CanResume)
        {
            State = PlaylistTrackState.Pending; // Back to queue
        }
    }

    public void Cancel()
    {
        if (CanCancel)
        {
            CancellationTokenSource?.Cancel();
            State = PlaylistTrackState.Cancelled;
            CurrentSpeed = "Cancelled";
        }
    }

    public void FindNewVersion()
    {
        if (CanHardRetry)
        {
            // Similar to Hard Retry, we reset to Pending to allow new search
            Reset(); 
        }
    }
    
    public void Reset()
    {
        CancellationTokenSource?.Cancel();
        CancellationTokenSource?.Dispose();
        CancellationTokenSource = null;
        State = PlaylistTrackState.Pending;
        Progress = 0;
        CurrentSpeed = "";
        ErrorMessage = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
