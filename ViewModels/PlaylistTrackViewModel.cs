using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input; // For ICommand
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Views; // For RelayCommand
using SLSKDONET.Data; // For IntegrityLevel

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
    private ArtworkProxy _artwork; // Replaces _artworkBitmap
    private bool _isAnalyzing; // New field for analysis feedback

    private int _sortOrder;
    public DateTime AddedAt => Model?.AddedAt ?? DateTime.MinValue;

    public DateTime? ReleaseDate => Model?.ReleaseDate;
    public string ReleaseYear => Model?.ReleaseDate?.Year.ToString() ?? "";

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
    private bool _isExpanded;
    private bool _technicalDataLoaded = false;
    private Data.Entities.TrackTechnicalEntity? _technicalEntity;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                if (_isExpanded && !_technicalDataLoaded)
                {
                    _ = LoadTechnicalDataAsync();
                }
            }
        }
    }

    // Integrity Level
    public IntegrityLevel IntegrityLevel
    {
        get => Model.Integrity;
        set
        {
            if (Model.Integrity != value)
            {
                Model.Integrity = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IntegrityBadge));
                OnPropertyChanged(nameof(IntegrityColor));
                OnPropertyChanged(nameof(IntegrityTooltip));
            }
        }
    }

    public string IntegrityBadge => Model.Integrity switch
    {
        Data.IntegrityLevel.Gold => "🥇",
        Data.IntegrityLevel.Verified => "🛡️",
        Data.IntegrityLevel.Suspicious => "📉",
        _ => ""
    };

    public string IntegrityColor => Model.Integrity switch
    {
        Data.IntegrityLevel.Gold => "#FFD700",      // Gold
        Data.IntegrityLevel.Verified => "#32CD32",  // LimeGreen
        Data.IntegrityLevel.Suspicious => "#FFA500",// Orange
        _ => "Transparent"
    };

    public string IntegrityTooltip => Model.Integrity switch
    {
        Data.IntegrityLevel.Gold => "Perfect Match (Gold)",
        Data.IntegrityLevel.Verified => "Verified Log/Hash",
        Data.IntegrityLevel.Suspicious => "Suspicious (Upscale/Transcode)",
        _ => "Not Analyzed"
    };

    public double Energy
    {
        get => Model.Energy ?? 0.0;
        set
        {
            Model.Energy = value;
            OnPropertyChanged();
        }
    }

    public double Danceability
    {
        get => Model.Danceability ?? 0.0;
        set
        {
            Model.Danceability = value;
            OnPropertyChanged();
        }
    }

    public double Valence
    {
        get => Model.Valence ?? 0.0;
        set
        {
            Model.Valence = value;
            OnPropertyChanged();
        }
    }
    
    public double BPM => Model.BPM ?? 0.0;
    public string MusicalKey => Model.MusicalKey ?? "—";
    
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
    
    public string? Genres => GenresDisplay;
    public int Popularity => Model.Popularity ?? 0;
    public string? Duration => DurationDisplay;
    public string? Bitrate => Model.Bitrate?.ToString() ?? Model.BitrateScore?.ToString() ?? "—";
    public string? Status => StatusText;

    public ArtworkProxy Artwork => _artwork;
    
    // Legacy property for compatibility (if XAML binds to ArtworkBitmap directly, we can redirect or just update XAML)
    // We will update XAML to bind to Artwork.Image
    public Avalonia.Media.Imaging.Bitmap? ArtworkBitmap => _artwork?.Image;

    public WaveformAnalysisData WaveformData
    {
        get
        {
             // Use lazy loaded entity if available, checking cached array logic
             var waveData = _technicalEntity?.WaveformData ?? Array.Empty<byte>();
             
             return new WaveformAnalysisData 
             { 
                 PeakData = waveData, 
                 RmsData = _technicalEntity?.RmsData ?? Array.Empty<byte>(),
                 LowData = _technicalEntity?.LowData ?? Array.Empty<byte>(),
                 MidData = _technicalEntity?.MidData ?? Array.Empty<byte>(),
                 HighData = _technicalEntity?.HighData ?? Array.Empty<byte>(),
                 DurationSeconds = (Model.CanonicalDuration ?? 0) / 1000.0
             };
        }
    }
    
    // Technical Stats
    public int SampleRate => Model.BitrateScore ?? 0; // Or add SampleRate to Model
    // Fix: LoudnessDisplay was previously incorrectly bound to QualityConfidence
    public string ConfidenceDisplay => Model.QualityConfidence.HasValue ? $"{Model.QualityConfidence:P0} Confidence" : "—";
    
    public string LoudnessDisplay => Model.Loudness.HasValue ? $"{Model.Loudness:F1} LUFS" : "—";
    public string TruePeakDisplay => Model.TruePeak.HasValue ? $"{Model.TruePeak:F1} dBTP" : "—";
    public string DynamicRangeDisplay => Model.DynamicRange.HasValue ? $"{Model.DynamicRange:F1} LU" : "—";
    
    public string IntegritySymbol => Model.IsTrustworthy == false ? "⚠️" : "✓";
    public string IntegrityText => Model.IsTrustworthy == false || Model.Integrity == Data.IntegrityLevel.Suspicious 
        ? "Upscale Detected" 
        : "Clean";
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
    private readonly ILibraryService? _libraryService;
    private readonly ArtworkCacheService? _artworkCacheService;

    // Disposal
    private readonly System.Reactive.Disposables.CompositeDisposable _disposables = new();
    private bool _isDisposed;

    public PlaylistTrackViewModel(
        PlaylistTrack track, 
        IEventBus? eventBus = null,
        ILibraryService? libraryService = null,
        ArtworkCacheService? artworkCacheService = null)
    {
        _eventBus = eventBus;
        _libraryService = libraryService;
        _artworkCacheService = artworkCacheService;
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
                _disposables.Add(_eventBus.GetEvent<TrackStateChangedEvent>().Subscribe(OnStateChanged));
                _disposables.Add(_eventBus.GetEvent<TrackProgressChangedEvent>().Subscribe(OnProgressChanged));
                _disposables.Add(_eventBus.GetEvent<Models.TrackMetadataUpdatedEvent>().Subscribe(OnMetadataUpdated));
                _disposables.Add(_eventBus.GetEvent<Models.TrackAnalysisStartedEvent>().Subscribe(OnAnalysisStarted));
                _disposables.Add(_eventBus.GetEvent<Models.TrackAnalysisFailedEvent>().Subscribe(OnAnalysisFailed));
            }

            // Initialize ArtworkProxy
            _artwork = new ArtworkProxy(_artworkCacheService!, track.AlbumArtUrl);
        }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed) return;

        if (disposing)
        {
            _disposables.Dispose();
            CancellationTokenSource?.Cancel();
            CancellationTokenSource?.Dispose();
            
            // Shared Bitmap: Do NOT dispose. 
            // _artwork is a proxy, does not own the bitmap resource (cache does).
            // _artworkBitmap = null;
        }

        _isDisposed = true;
    }

    private void OnMetadataUpdated(Models.TrackMetadataUpdatedEvent evt)
    {
        if (evt.TrackGlobalId != GlobalId) return;
        
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
             _isAnalyzing = false; // Clear analyzing flag

             // Reload track data from database to get updated metadata
             if (_libraryService != null)
             {
                 var updatedTrack = await _libraryService.GetPlaylistTrackByHashAsync(Model.PlaylistId, GlobalId);
                 
                 if (updatedTrack != null)
                 {
                     // Update model with fresh data
                     Model.AlbumArtUrl = updatedTrack.AlbumArtUrl;
                     Model.SpotifyTrackId = updatedTrack.SpotifyTrackId;
                     Model.SpotifyAlbumId = updatedTrack.SpotifyAlbumId;
                     Model.SpotifyArtistId = updatedTrack.SpotifyArtistId;
                     Model.IsEnriched = updatedTrack.IsEnriched;
                     Model.Album = updatedTrack.Album;
                     
                     // Sync Audio Features & Extended Metadata
                     Model.BPM = updatedTrack.BPM;
                     Model.MusicalKey = updatedTrack.MusicalKey;
                     Model.Energy = updatedTrack.Energy;
                     Model.Danceability = updatedTrack.Danceability;
                     Model.Valence = updatedTrack.Valence;
                     Model.Loudness = updatedTrack.Loudness;
                     Model.TruePeak = updatedTrack.TruePeak;
                     Model.DynamicRange = updatedTrack.DynamicRange;
                     
                     // Update Analysis info if available
                     Model.Popularity = updatedTrack.Popularity;
                     Model.Genres = updatedTrack.Genres;
                     
                     // NEW: Sync Waveform and Technical Analysis results
                     Model.WaveformData = updatedTrack.WaveformData;
                     Model.RmsData = updatedTrack.RmsData;
                     Model.LowData = updatedTrack.LowData;
                     Model.MidData = updatedTrack.MidData;
                     Model.HighData = updatedTrack.HighData;
                     Model.CanonicalDuration = updatedTrack.CanonicalDuration;
                     Model.Bitrate = updatedTrack.Bitrate;
                     Model.QualityConfidence = updatedTrack.QualityConfidence;
                     Model.IsTrustworthy = updatedTrack.IsTrustworthy;
                     
                     // Technical Audio
                     Model.Loudness = updatedTrack.Loudness;
                     Model.TruePeak = updatedTrack.TruePeak;
                     Model.DynamicRange = updatedTrack.DynamicRange;
                     
                      // Load artwork if URL is available
                      if (!string.IsNullOrWhiteSpace(updatedTrack.AlbumArtUrl))
                      {
                          // Refresh proxy
                          _artwork = new ArtworkProxy(_artworkCacheService!, updatedTrack.AlbumArtUrl);
                          OnPropertyChanged(nameof(Artwork));
                          OnPropertyChanged(nameof(ArtworkBitmap));
                      }
                 }
             }
             
             OnPropertyChanged(nameof(Artist));
             OnPropertyChanged(nameof(Title));
             OnPropertyChanged(nameof(Album));
             OnPropertyChanged(nameof(CoverArtUrl));
             OnPropertyChanged(nameof(ArtworkBitmap));
             OnPropertyChanged(nameof(SpotifyTrackId));
             OnPropertyChanged(nameof(IsEnriched));
             OnPropertyChanged(nameof(MetadataStatus));
             OnPropertyChanged(nameof(MetadataStatusColor));
             OnPropertyChanged(nameof(MetadataStatusSymbol));
             
             // Notify Extended Props
             OnPropertyChanged(nameof(BPM));
             OnPropertyChanged(nameof(MusicalKey));
             OnPropertyChanged(nameof(KeyDisplay)); // Assuming KeyDisplay is a computed property that uses MusicalKey
             OnPropertyChanged(nameof(BpmDisplay)); // Assuming BpmDisplay is a computed property that uses BPM
             OnPropertyChanged(nameof(LoudnessDisplay));
             OnPropertyChanged(nameof(Energy));
             OnPropertyChanged(nameof(Danceability));
             OnPropertyChanged(nameof(Valence));
             OnPropertyChanged(nameof(Genres));
             OnPropertyChanged(nameof(Popularity));
             
             // NEW: Notify Waveform and technical props
             OnPropertyChanged(nameof(WaveformData));
             OnPropertyChanged(nameof(Bitrate));
             OnPropertyChanged(nameof(IntegritySymbol));
             OnPropertyChanged(nameof(IntegrityText));
             OnPropertyChanged(nameof(Duration));

             OnPropertyChanged(nameof(LoudnessDisplay));
             OnPropertyChanged(nameof(TruePeakDisplay));
             OnPropertyChanged(nameof(DynamicRangeDisplay));
             OnPropertyChanged(nameof(DynamicRangeDisplay));
             OnPropertyChanged(nameof(ConfidenceDisplay));

             // Dates
             OnPropertyChanged(nameof(ReleaseDate));
             OnPropertyChanged(nameof(ReleaseYear));
        });
    }

    private void OnStateChanged(TrackStateChangedEvent evt)
    {
        if (evt.TrackGlobalId != GlobalId) return;
        
        // Marshal to UI Thread
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
             State = evt.State;
             if (evt.Error != null) ErrorMessage = evt.Error;
             OnPropertyChanged(nameof(DetailedStatusText)); // Update tooltip
             
             // NEW: Load file size from disk when track completes
             if (evt.State == PlaylistTrackState.Completed && FileSizeBytes == 0)
             {
                 LoadFileSizeFromDisk();
             }
        });
    }
    
    /// <summary>
    /// Loads file size from disk for existing completed tracks (fallback when event didn't provide TotalBytes)
    /// </summary>
    private void LoadFileSizeFromDisk()
    {
        if (string.IsNullOrEmpty(Model.ResolvedFilePath))
            return;
            
        try
        {
            if (System.IO.File.Exists(Model.ResolvedFilePath))
            {
                var fileInfo = new System.IO.FileInfo(Model.ResolvedFilePath);
                FileSizeBytes = fileInfo.Length;
            }
        }
        catch { /* Fail silently */ }
    }

    private void OnProgressChanged(TrackProgressChangedEvent evt)
    {
        if (evt.TrackGlobalId != GlobalId) return;
        
        // Throttling could be added here if needed, but for now we rely on simple marshaling
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
             Progress = evt.Progress;
             
             // NEW: Capture file size during download
             if (evt.TotalBytes > 0)
             {
                 FileSizeBytes = evt.TotalBytes;
             }
        });
    }

    private void OnAnalysisStarted(Models.TrackAnalysisStartedEvent evt)
    {
        if (evt.TrackGlobalId != GlobalId) return;
        
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
             _isAnalyzing = true;
             OnPropertyChanged(nameof(MetadataStatus));
             OnPropertyChanged(nameof(MetadataStatusColor));
             OnPropertyChanged(nameof(MetadataStatusSymbol));
        });
    }

    private void OnAnalysisFailed(Models.TrackAnalysisFailedEvent evt)
    {
        if (evt.TrackGlobalId != GlobalId) return;
        
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
             _isAnalyzing = false;
             // Could update ErrorMessage here if desired
             OnPropertyChanged(nameof(MetadataStatus));
             OnPropertyChanged(nameof(MetadataStatusColor));
             OnPropertyChanged(nameof(MetadataStatusSymbol));
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
                OnPropertyChanged(nameof(StatusText));
                
                // Notify command availability
                OnPropertyChanged(nameof(CanPause));
                OnPropertyChanged(nameof(CanResume));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(CanHardRetry));
                OnPropertyChanged(nameof(CanDeleteFile));
                
                // Visual distinctions
                OnPropertyChanged(nameof(IsDownloaded));
                
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
                OnPropertyChanged(nameof(StatusText));
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
                OnPropertyChanged(nameof(DetailedStatusText));
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

    public bool IsEnriched
    {
        get => Model.IsEnriched;
        set
        {
            if (Model.IsEnriched != value)
            {
                Model.IsEnriched = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MetadataStatus));
            }
        }
    }
    
    // Fix: Add display property for consistent "—" fallback
    public string BpmDisplay => Model.BPM.HasValue && Model.BPM > 0 ? $"{Model.BPM:0}" : "—";
    public string KeyDisplay => !string.IsNullOrEmpty(Model.MusicalKey) ? Model.MusicalKey : "—";


    public string MetadataStatus
    {
        get
        {
            if (_isAnalyzing) return "Analyzing";
            if (Model.IsEnriched) return "Enriched";
            if (!string.IsNullOrEmpty(Model.SpotifyTrackId)) return "Identified"; // Partial state
            return "Pending"; // Waiting for enrichment worker
        }

    }

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



    /// <summary>
    /// Raw file size in bytes (populated during download via event or from disk for existing files)
    /// </summary>
    private long _fileSizeBytes = 0;
    public long FileSizeBytes
    {
        get => _fileSizeBytes;
        private set
        {
            if (_fileSizeBytes != value)
            {
                _fileSizeBytes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FileSizeDisplay));
            }
        }
    }

    /// <summary>
    /// Formatted file size display (e.g., "10.5 MB" or "850 KB")
    /// </summary>
    public string FileSizeDisplay
    {
        get
        {
            if (FileSizeBytes == 0) return "—";
            
            double mb = FileSizeBytes / 1024.0 / 1024.0;
            if (mb >= 1.0)
                return $"{mb:F1} MB";
            
            double kb = FileSizeBytes / 1024.0;
            return $"{kb:F0} KB";
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

    public bool IsDownloaded => State == PlaylistTrackState.Completed;

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
                PlaylistTrackState.Deferred => "#FFD700",       // Goldenrod (Preemption)
                PlaylistTrackState.Cancelled => "#808080",      // Gray
                _ => "#D3D3D3"                                  // LightGray
            };
        }
    }

    public string StatusText => State switch
    {
        PlaylistTrackState.Completed => "✓ Ready",
        PlaylistTrackState.Downloading => $"↓ {Progress:P0}",
        PlaylistTrackState.Searching => "🔍 Search",
        PlaylistTrackState.Queued => "⏳ Queued",
        PlaylistTrackState.Failed => "✗ Failed",
        PlaylistTrackState.Deferred => "⏳ Deferred",
        PlaylistTrackState.Pending => "⊙ Missing",
        _ => "?"
    };

    public string DetailedStatusText => !string.IsNullOrEmpty(ErrorMessage) 
        ? $"Status: {State}\nDetails: {ErrorMessage}" 
        : StatusText;

    public string MetadataStatusColor => MetadataStatus switch
    {
        "Analyzing" => "#00BFFF", // Deep Sky Blue
        "Enriched" => "#FFD700", // Gold
        "Identified" => "#1E90FF", // DodgerBlue
        _ => "#505050"
    };

    public string MetadataStatusSymbol => MetadataStatus switch
    {

        "Analyzing" => "⚙️",
        "Enriched" => "✨",
        "Identified" => "🆔",
        _ => "⏳"
    };

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

    // ArtworkBitmap and LoadAlbumArtworkAsync removed. 
    // Replaced by ArtworkProxy logic (see Artwork property).

    /// <summary>
    /// Lazy loads heavy technical data (Waveforms, etc.) from the database.
    /// Triggered when the track is expanded or viewed in Inspector.
    /// </summary>
    public async Task LoadTechnicalDataAsync()
    {
        if (_technicalDataLoaded || _libraryService == null) return;
        
        try
        {
            // Fetch from LibraryService (which calls DB)
             _technicalEntity = await _libraryService.GetTechnicalDetailsAsync(this.Id);
             
             _technicalDataLoaded = true;
             
             // Notify UI that waveform data is ready
             OnPropertyChanged(nameof(WaveformData));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load technical data: {ex.Message}");
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
