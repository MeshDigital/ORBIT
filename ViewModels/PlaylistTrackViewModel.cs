using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input; // For ICommand
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Views; // For RelayCommand

namespace SLSKDONET.ViewModels;



/// <summary>
/// ViewModel representing a track in the download queue.
/// Manages state, progress, and updates for the UI.
/// </summary>
public class PlaylistTrackViewModel : INotifyPropertyChanged, Library.ILibraryNode
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
                 if (Model != null) Model.SortOrder = value;
             }
        }
    }

    public Guid SourceId { get; set; } // Project ID (PlaylistJob.Id)
    public Guid Id => Model.Id;
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
    
    public string? Duration => DurationDisplay;
    public string? Bitrate => Model.Bitrate.HasValue ? Model.Bitrate.Value.ToString() : string.Empty;
    public string? Status => State.ToString();
    public int Popularity => Model.Popularity ?? 0;
    public string? Genres => GenresDisplay;
    // AlbumArtPath and Progress are already present in this class.

    // Reference to the underlying model if needed for persistence later
    public PlaylistTrack Model { get; private set; }

    // Cancellation token source for this specific track's operation
    public System.Threading.CancellationTokenSource? CancellationTokenSource { get; set; }

    // User engagement
    private int _rating;
    public int Rating
    {
        get => _rating;
        set
        {
            if (_rating != value)
            {
                _rating = value;
                Model.Rating = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _isLiked;
    public bool IsLiked
    {
        get => _isLiked;
        set
        {
            if (_isLiked != value)
            {
                _isLiked = value;
                Model.IsLiked = value;
                OnPropertyChanged();
            }
        }
    }

    // Commands
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand FindNewVersionCommand { get; }

    private readonly IEventBus? _eventBus;

    public PlaylistTrackViewModel(PlaylistTrack track, IEventBus? eventBus = null)
    {
        _eventBus = eventBus;
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
        
        // Smart Subscription
        if (_eventBus != null)
        {
            _eventBus.GetEvent<Events.TrackStateChangedEvent>().Subscribe(OnStateChanged);
            _eventBus.GetEvent<Events.TrackProgressChangedEvent>().Subscribe(OnProgressChanged);
            _eventBus.GetEvent<Models.TrackMetadataUpdatedEvent>().Subscribe(OnMetadataUpdated);
        }
    }
    
    private void OnMetadataUpdated(Models.TrackMetadataUpdatedEvent evt)
    {
        if (evt.TrackGlobalId != GlobalId) return;
        
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
             OnPropertyChanged(nameof(Artist));
             OnPropertyChanged(nameof(Title));
             OnPropertyChanged(nameof(Album));
             OnPropertyChanged(nameof(CoverArtUrl));
             OnPropertyChanged(nameof(SpotifyTrackId));
        });
    }

    private void OnStateChanged(Events.TrackStateChangedEvent evt)
    {
        if (evt.TrackGlobalId != GlobalId) return;
        
        // Marshal to UI Thread
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
             State = evt.NewState;
             if (evt.ErrorMessage != null) ErrorMessage = evt.ErrorMessage;
        });
    }

    private void OnProgressChanged(Events.TrackProgressChangedEvent evt)
    {
        if (evt.TrackGlobalId != GlobalId) return;
        
        // Throttling could be added here if needed, but for now we rely on simple marshaling
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
             Progress = evt.Progress;
        });
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

    // Phase 0: Album artwork from Spotify metadata
    private string? _albumArtPath;
    public string? AlbumArtPath
    {
        get => _albumArtPath;
        private set
        {
            if (_albumArtPath != value)
            {
                _albumArtPath = value;
                OnPropertyChanged();
            }
        }
    }

    public string? AlbumArtUrl => Model.AlbumArtUrl;
    
    // Phase 3.1: Expose Spotify Metadata ID
    public string? SpotifyTrackId
    {
        get => Model.SpotifyTrackId;
        set
        {
            if (Model.SpotifyTrackId != value)
            {
                Model.SpotifyTrackId = value;
                OnPropertyChanged();
            }
        }
    }

    public string? SpotifyAlbumId => Model.SpotifyAlbumId;

    // Phase 1: UI Metadata
    
    public string GenresDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Model.Genres)) return string.Empty;
            try
            {
                var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(Model.Genres);
                return list != null ? string.Join(", ", list) : string.Empty;
            }
            catch
            {
                return Model.Genres ?? string.Empty;
            }
        }
    }

    public string DurationDisplay
    {
        get
        {
            if (Model.CanonicalDuration.HasValue)
            {
                var t = TimeSpan.FromMilliseconds(Model.CanonicalDuration.Value);
                return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
            }
            return string.Empty;
        }
    }

    public string ReleaseYear => Model.ReleaseDate.HasValue ? Model.ReleaseDate.Value.Year.ToString() : string.Empty;

    /// <summary>
    /// Loads the album artwork from cache or downloads it.
    /// Should be called by the ViewModel after construction.
    /// </summary>
    public async System.Threading.Tasks.Task LoadAlbumArtworkAsync(Services.ArtworkCacheService artworkCache)
    {
        if (string.IsNullOrWhiteSpace(AlbumArtUrl) || string.IsNullOrWhiteSpace(SpotifyAlbumId))
            return;

        try
        {
            AlbumArtPath = await artworkCache.GetArtworkPathAsync(AlbumArtUrl, SpotifyAlbumId);
        }
        catch
        {
            // Silently fail - artwork is optional
            AlbumArtPath = null;
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

    // Visuals - Color codes for Avalonia (replacing WPF Brushes)
    public string StatusColor
    {
        get
        {
            return State switch
            {
                PlaylistTrackState.Completed => "#90EE90",      // Light Green
                PlaylistTrackState.Downloading => "#00BFFF",    // Deep Sky Blue
                PlaylistTrackState.Searching => "#6495ED",      // Cornflower Blue
                PlaylistTrackState.Queued => "#00FFFF",         // Cyan
                PlaylistTrackState.Paused => "#FFA500",         // Orange
                PlaylistTrackState.Failed => "#FF0000",         // Red
                PlaylistTrackState.Cancelled => "#808080",      // Gray
                _ => "#D3D3D3"                                  // LightGray
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
