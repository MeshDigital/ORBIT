using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
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
    private readonly ArtworkCacheService _artworkCache;
    private readonly ILibraryService _libraryService;
    private readonly CompositeDisposable _disposables = new();

    // Core Data
    public PlaylistTrack Model { get; }
    
    // New: Raw Speed for Aggregation
    private long _downloadSpeed;
    public long DownloadSpeed 
    { 
        get => _downloadSpeed; 
        set => this.RaiseAndSetIfChanged(ref _downloadSpeed, value); 
    }
    
    public UnifiedTrackViewModel(
        PlaylistTrack model, 
        DownloadManager downloadManager, 
        IEventBus eventBus,
        ArtworkCacheService artworkCache,
        ILibraryService libraryService)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _downloadManager = downloadManager ?? throw new ArgumentNullException(nameof(downloadManager));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _artworkCache = artworkCache ?? throw new ArgumentNullException(nameof(artworkCache));
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));

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
            if (!string.IsNullOrEmpty(Model.ResolvedFilePath))
            {
                 _eventBus.Publish(new RevealFileRequestEvent(Model.ResolvedFilePath));
            }
        }, this.WhenAnyValue(x => x.IsCompleted));

        AddToProjectCommand = ReactiveCommand.Create(() => 
        {
            _eventBus.Publish(new AddToProjectRequestEvent(new[] { Model }));
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
            
        CleanCommand = ReactiveCommand.CreateFromTask(async () =>
        {
             // Handled by parent collection usually, but could arguably be here if we had a Delete service method
             // For now, this command might just be a placeholder or call a service to remove self
             await _downloadManager.DeleteTrackFromDiskAndHistoryAsync(GlobalId);
        }, this.WhenAnyValue(x => x.IsCompleted, x => x.IsFailed, (c, f) => c || f));

        // Subscribe to Events with Rx Scheduler for Thread Safety
        _eventBus.GetEvent<TrackStateChangedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnStateChanged)
            .DisposeWith(_disposables);

        _eventBus.GetEvent<TrackProgressChangedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnProgressChanged)
            .DisposeWith(_disposables);

        _eventBus.GetEvent<TrackMetadataUpdatedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnMetadataUpdated)
            .DisposeWith(_disposables);
            
         // Initialize sliding window for speed
         _lastProgressTime = DateTime.MinValue;

         // Phase 0: Load artwork via Proxy
         _artwork = new ArtworkProxy(_artworkCache, Model.AlbumArtUrl);
         
         FindSimilarCommand = ReactiveCommand.Create(FindSimilar);
         FindSimilarAiCommand = ReactiveCommand.Create(FindSimilarAi);
         FilterByVibeCommand = ReactiveCommand.Create(() => 
         {
             if (!string.IsNullOrEmpty(DetectedSubGenre))
             {
                 _eventBus.Publish(new SearchRequestedEvent(DetectedSubGenre));
             }
         });
    }
    
    private void FindSimilar()
    {
        if (Model == null) return;
        _eventBus.Publish(new FindSimilarRequestEvent(Model, useAi: false));
    }

    private void FindSimilarAi()
    {
        if (Model == null) return;
        _eventBus.Publish(new FindSimilarRequestEvent(Model, useAi: true));
    }



    // IDisplayableTrack Implementation
    public string GlobalId => Model.TrackUniqueHash;
    public string ArtistName => !string.IsNullOrWhiteSpace(Model.Artist) ? Model.Artist : "Unknown Artist";
    public string TrackTitle => !string.IsNullOrWhiteSpace(Model.Title) ? Model.Title : "Unknown Title";
    public string AlbumName => !string.IsNullOrWhiteSpace(Model.Album) ? Model.Album : "Unknown Album";
    public string? AlbumArtUrl => Model.AlbumArtUrl;

    private ArtworkProxy _artwork;
    public ArtworkProxy Artwork => _artwork;
    
    // Legacy support: redirects to Proxy.Image (which triggers load)
    public Avalonia.Media.Imaging.Bitmap? ArtworkBitmap => _artwork?.Image;

    private PlaylistTrackState _state;
    public PlaylistTrackState State
    {
        get => _state;
        set { 
            if (_state != value)
            {
                this.RaiseAndSetIfChanged(ref _state, value);
                this.RaisePropertyChanged(nameof(StatusText));
                this.RaisePropertyChanged(nameof(StatusColor)); // Added
                this.RaisePropertyChanged(nameof(DetailedStatusText)); // Added
                this.RaisePropertyChanged(nameof(IsIndeterminate));
                this.RaisePropertyChanged(nameof(IsFailed));
                this.RaisePropertyChanged(nameof(IsPaused));
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
    
    // Fix: Added StatusColor property for UI binding
    public Avalonia.Media.IBrush StatusColor => State switch
    {
        PlaylistTrackState.Completed => Avalonia.Media.Brushes.LimeGreen,
        PlaylistTrackState.Failed => Avalonia.Media.Brushes.OrangeRed,
        PlaylistTrackState.Cancelled => Avalonia.Media.Brushes.Gray,
        PlaylistTrackState.Downloading => Avalonia.Media.Brushes.Cyan,
        PlaylistTrackState.Searching => Avalonia.Media.Brushes.Yellow,
        _ => Avalonia.Media.Brushes.LightGray
    };

    // Fix: Detailed tooltip text
    public string DetailedStatusText => IsFailed 
        ? $"Failed: {FailureReason ?? "Unknown Error"}\n(Click Retry to search for a new peer)" 
        : StatusText;

    private double _progress;
    public double Progress
    {
        get => _progress;
        set => this.RaiseAndSetIfChanged(ref _progress, value);
    }

    public bool IsIndeterminate => State == PlaylistTrackState.Searching || State == PlaylistTrackState.Queued;
    public bool IsFailed => State == PlaylistTrackState.Failed || State == PlaylistTrackState.Cancelled;
    public bool IsPaused => State == PlaylistTrackState.Paused;
    // Fix: Include Pending and Queued in IsActive so they appear in the Active tab
    public bool IsActive => State == PlaylistTrackState.Downloading || State == PlaylistTrackState.Searching || State == PlaylistTrackState.Queued || State == PlaylistTrackState.Pending;
    public bool IsCompleted => State == PlaylistTrackState.Completed;

    private string? _failureReason;
    public string? FailureReason
    {
        get => _failureReason;
        set 
        {
            this.RaiseAndSetIfChanged(ref _failureReason, value);
            this.RaisePropertyChanged(nameof(StatusText));
            this.RaisePropertyChanged(nameof(DetailedStatusText));
        }
    }

    private DownloadFailureReason _failureEnum;
    public DownloadFailureReason FailureEnum
    {
        get => _failureEnum;
        set
        {
            this.RaiseAndSetIfChanged(ref _failureEnum, value);
            this.RaisePropertyChanged(nameof(FailureDisplayMessage));
            this.RaisePropertyChanged(nameof(FailureActionSuggestion));
        }
    }

    public string FailureDisplayMessage 
    {
        get
        {
            // Fix: If we have rejection details but no specific FailureEnum, it means the search found things but rejected them all.
            if (_hasRejectionDetails && FailureEnum == DownloadFailureReason.None)
            {
                return "Search Rejected";
            }
            return FailureEnum.ToDisplayMessage();
        }
    }
    
    public string FailureActionSuggestion => FailureEnum.ToActionableSuggestion();

    // Phase 0.5: Search Diagnostics
    private System.Collections.ObjectModel.ObservableCollection<RejectedResult>? _rejectionDetails;
    public System.Collections.ObjectModel.ObservableCollection<RejectedResult>? RejectionDetails
    {
        get => _rejectionDetails;
        set => this.RaiseAndSetIfChanged(ref _rejectionDetails, value);
    }

    private bool _hasRejectionDetails;
    public bool HasRejectionDetails
    {
        get => _hasRejectionDetails;
        set => this.RaiseAndSetIfChanged(ref _hasRejectionDetails, value);
    }

    public string CompletedAtDisplay => Model.CompletedAt?.ToString("g") ?? Model.AddedAt.ToString("g");

    public string TechnicalSummary
    {
        get
        {
            // "Soulseek • 320kbps • 12MB • [Time]"
            var parts = new System.Collections.Generic.List<string>();
            parts.Add("Soulseek"); // Source (Static for now)
            
            if (Model.Bitrate.HasValue) 
            {
                 // Phase 0.6: Truth in UI
                 string prefix = IsCompleted ? "" : "Est. ";
                 parts.Add($"{prefix}{Model.Bitrate}kbps");
            }
            if (!string.IsNullOrEmpty(Model.Format)) parts.Add(Model.Format.ToUpper());
            
            if (_totalBytes > 0) 
                parts.Add($"{_totalBytes / 1024.0 / 1024.0:F1} MB");
                
            if (IsCompleted || IsFailed)
                parts.Add(CompletedAtDisplay); 
            else if (IsActive)
                parts.Add(SpeedDisplay);

            return string.Join(" • ", parts);
        }
    }
    
    // Phase 0.6: Truth in UI - Tech Specs are Estimates until verified
    public string TechSpecPrefix => IsCompleted ? "" : "Est. ";

    // Curation Hub Properties
    public double IntegrityScore => Model.QualityConfidence ?? 0.0;
    // Phase 0.6: Truth in UI
    public bool IsSecure => IsCompleted && IntegrityScore > 0.9 && !string.IsNullOrEmpty(Model.ResolvedFilePath);

    // Phase 19: Search 2.0 Tiers for Library
    public SLSKDONET.Models.SearchTier Tier => MetadataForensicService.CalculateTier(Model);

    public string TierBadge => MetadataForensicService.GetTierBadge(Tier);

    public Avalonia.Media.IBrush TierColor => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(MetadataForensicService.GetTierColor(Tier)));
    
    // Legacy mapping for backward compatibility if needed, otherwise replaced by Tier
    public string QualityIcon => TierBadge;
    public Avalonia.Media.IBrush QualityColor => TierColor;
    
    public string BpmDisplay => Model.BPM.HasValue ? $"{Model.BPM:0}" : "—";
    public string KeyDisplay
    {
        get
        {
            if (string.IsNullOrEmpty(Model.MusicalKey)) return "—";
            
            var camelot = Utils.KeyConverter.ToCamelot(Model.MusicalKey);
            // Show both: "G minor (6A)" or just "6A" if already in Camelot format
            if (camelot == Model.MusicalKey)
                return camelot; // Already Camelot
            
            return $"{Model.MusicalKey} ({camelot})";
        }
    }
    public string YearDisplay => Model.ReleaseDate.HasValue ? Model.ReleaseDate.Value.Year.ToString() : "";
    
    // Technical Audio Display
    public string LoudnessDisplay => Model.Loudness.HasValue ? $"{Model.Loudness:F1} LUFS" : "—";
    public string TruePeakDisplay => Model.TruePeak.HasValue ? $"{Model.TruePeak:F1} dBTP" : "—";
    public string DynamicRangeDisplay => Model.DynamicRange.HasValue ? $"{Model.DynamicRange:F1} LU" : "—";

    public bool IsEnriched => Model.IsEnriched;
    public bool IsPrepared => Model.IsPrepared;
    public string? PrimaryGenre => Model.PrimaryGenre;
    public string? DetectedSubGenre => Model.DetectedSubGenre;
    public float? SubGenreConfidence => Model.SubGenreConfidence;

    // Phase 12.7: Vibe Color Mapping
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Avalonia.Media.IBrush> _vibeColorCache = new();
    public Avalonia.Media.IBrush VibeColor => GetVibeColor(DetectedSubGenre);

    private Avalonia.Media.IBrush GetVibeColor(string? genre)
    {
        if (string.IsNullOrEmpty(genre)) return Avalonia.Media.Brushes.Transparent;
        if (_vibeColorCache.TryGetValue(genre, out var brush)) return brush;

        // On-demand load from Style Lab (Phase 15 integration)
        Task.Run(async () => 
        {
            var styles = await _libraryService.GetStyleDefinitionsAsync();
            foreach (var style in styles)
            {
                if (Avalonia.Media.Color.TryParse(style.ColorHex, out var color))
                {
                    _vibeColorCache[style.Name] = new Avalonia.Media.SolidColorBrush(color);
                }
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(VibeColor)));
        });

        return Avalonia.Media.Brushes.Gray;
    }

    public string PreparationStatus => IsPrepared ? "Prepared" : "Raw";
    public Avalonia.Media.IBrush PreparationColor => IsPrepared ? Avalonia.Media.Brushes.DodgerBlue : Avalonia.Media.Brushes.Gray;

    // Phase 13C: Vibe Pills
    public record VibePill(string Icon, string Label, Avalonia.Media.IBrush Color, string Description);
    
    public System.Collections.Generic.IEnumerable<VibePill> VibePills
    {
        get
        {
            var pills = new System.Collections.Generic.List<VibePill>();
            
            // 💃 Dance Pill (High Danceability)
            if (Model.Danceability > 0.75)
            {
                pills.Add(new VibePill("💃", "Dance", Avalonia.Media.Brushes.DeepPink, "High Danceability detected by AI"));
            }
            
            // 🎻 Inst Pill (Instrumental)
            if (Model.QualityConfidence > 0.8) // High spectral quality often correlates with clean instrumentals/stable phase
            {
                 // Actually we'll use a specific threshold based on new fields if available
                 // For now, let's use the MoodTag if it matches
                 if (Model.MoodTag == "Relaxed")
                 {
                     pills.Add(new VibePill("🎻", "Inst", Avalonia.Media.Brushes.RoyalBlue, "Instrumental / Chill Vibe"));
                 }
            }

            // 🔥 Hard Pill (Aggressive/High Energy)
            if (Model.Energy > 0.8 || Model.MoodTag == "Aggressive")
            {
                pills.Add(new VibePill("🔥", "Hard", Avalonia.Media.Brushes.OrangeRed, "High Energy / Aggressive Vibe"));
            }

            // ✨ Vibe Pill (Primary Genre/Subgenre classification)
            if (!string.IsNullOrEmpty(DetectedSubGenre))
            {
                pills.Add(new VibePill("✨", DetectedSubGenre, VibeColor, $"Genre: {DetectedSubGenre} (Conf: {SubGenreConfidence:P0})"));
            }
            
            return pills;
        }
    }

    public WaveformAnalysisData WaveformData => new WaveformAnalysisData
    {
        PeakData = Model.WaveformData ?? Array.Empty<byte>(),
        RmsData = Model.RmsData ?? Array.Empty<byte>(),
        LowData = Model.LowData ?? Array.Empty<byte>(),
        MidData = Model.MidData ?? Array.Empty<byte>(),
        HighData = Model.HighData ?? Array.Empty<byte>(),
        DurationSeconds = (Model.CanonicalDuration ?? 0) / 1000.0
    };
    
    // Commands
    public ICommand PlayCommand { get; }
    public ICommand RevealFileCommand { get; }
    public ICommand AddToProjectCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand CleanCommand { get; }
    public ICommand FilterByVibeCommand { get; }
    public ICommand FindSimilarCommand { get; }
    public ICommand FindSimilarAiCommand { get; }

    // Internal State
    private long _totalBytes;
    private long _bytesReceived;
    private double _currentSpeed;
    private DateTime _lastProgressTime;
    
    public double CurrentSpeedBytes => _currentSpeed;

    public string SpeedDisplay => _currentSpeed > 1024 * 1024 
        ? $"{_currentSpeed / 1024 / 1024:F1} MB/s" 
        : $"{_currentSpeed / 1024:F0} KB/s";

    // Phase 4+: Discovery Features
    private string? _discoveryReason;
    public string? DiscoveryReason
    {
        get => _discoveryReason;
        set => this.RaiseAndSetIfChanged(ref _discoveryReason, value);
    }

    // Phase 0.6: Truth in UI - Stems
    private bool? _hasStems;
    public bool HasStems
    {
        get
        {
            if (!IsCompleted) return false;
            
            if (!_hasStems.HasValue)
            {
                _hasStems = false;
                _ = CheckStemsAsync();
            }
            return _hasStems.Value;
        }
    }
    
    private async Task CheckStemsAsync()
    {
         if (string.IsNullOrEmpty(Model.ResolvedFilePath)) return;
         try {
             await Task.Run(() => {
                 var dir = System.IO.Path.GetDirectoryName(Model.ResolvedFilePath);
                 var name = System.IO.Path.GetFileNameWithoutExtension(Model.ResolvedFilePath);
                 if (string.IsNullOrEmpty(dir)) return;
                 var path = System.IO.Path.Combine(dir, $"{name}_Stems");
                 var found = System.IO.Directory.Exists(path) && System.IO.Directory.GetFiles(path).Length > 0;
                 Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                     _hasStems = found;
                     this.RaisePropertyChanged(nameof(HasStems));
                 });
             });
         } catch {}
    }

    // Event Handlers
    private void OnStateChanged(TrackStateChangedEvent e)
    {
        if (e.TrackGlobalId != GlobalId) return;
        
        System.Diagnostics.Debug.WriteLine($"[UnifiedTrackVM] {GlobalId} State Changed: {e.State} (Error: {e.Error})");
        State = e.State;
        FailureReason = e.Error;
        FailureEnum = e.FailureReason;
        
        // Phase 0.5: Populate Search Diagnostics
        if (e.SearchLog != null && e.SearchLog.Top3RejectedResults.Any())
        {
             RejectionDetails = new System.Collections.ObjectModel.ObservableCollection<RejectedResult>(e.SearchLog.Top3RejectedResults);
             HasRejectionDetails = true;
        }
        else if (State == PlaylistTrackState.Pending || State == PlaylistTrackState.Searching)
        {
             // Clear diagnostics on retry/restart
             RejectionDetails = null;
             HasRejectionDetails = false;
        }
    }

    private void OnProgressChanged(TrackProgressChangedEvent e)
    {
        if (e.TrackGlobalId != GlobalId) return;
        
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
    }

    private void OnMetadataUpdated(TrackMetadataUpdatedEvent e)
    {
        if (e.TrackGlobalId != GlobalId) return;
        
        // Reload from DB to ensure Model has new IDs (SpotifyAlbumId etc.)
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var updatedTrack = await _libraryService.GetPlaylistTrackByHashAsync(Model.PlaylistId, GlobalId);
            
            if (updatedTrack != null)
            {
                // Sync important fields back to Model instance
                Model.Artist = updatedTrack.Artist;
                Model.Title = updatedTrack.Title;
                Model.Album = updatedTrack.Album;
                Model.AlbumArtUrl = updatedTrack.AlbumArtUrl;
                Model.SpotifyAlbumId = updatedTrack.SpotifyAlbumId;
                Model.SpotifyTrackId = updatedTrack.SpotifyTrackId;
                Model.SpotifyArtistId = updatedTrack.SpotifyArtistId;
                Model.BPM = updatedTrack.BPM;
                Model.MusicalKey = updatedTrack.MusicalKey;
                Model.IsEnriched = updatedTrack.IsEnriched;
                Model.Energy = updatedTrack.Energy;
                Model.Danceability = updatedTrack.Danceability;
                Model.Valence = updatedTrack.Valence;
                Model.Genres = updatedTrack.Genres;
                Model.Popularity = updatedTrack.Popularity;
                Model.IsPrepared = updatedTrack.IsPrepared;
                Model.PrimaryGenre = updatedTrack.PrimaryGenre;
                Model.CuePointsJson = updatedTrack.CuePointsJson;
                Model.MoodTag = updatedTrack.MoodTag;
                Model.DetectedSubGenre = updatedTrack.DetectedSubGenre;
                Model.SubGenreConfidence = updatedTrack.SubGenreConfidence;
                
                // Sync Waveform bands
                Model.LowData = updatedTrack.LowData;
                Model.MidData = updatedTrack.MidData;
                Model.HighData = updatedTrack.HighData;
                Model.WaveformData = updatedTrack.WaveformData;
                Model.RmsData = updatedTrack.RmsData;
                Model.CanonicalDuration = updatedTrack.CanonicalDuration;
                
                // Technical Audio
                Model.Loudness = updatedTrack.Loudness;
                Model.TruePeak = updatedTrack.TruePeak;
                Model.DynamicRange = updatedTrack.DynamicRange;
                Model.FrequencyCutoff = updatedTrack.FrequencyCutoff;
                Model.QualityConfidence = updatedTrack.QualityConfidence;
                Model.SpectralHash = updatedTrack.SpectralHash;
                Model.IsTrustworthy = updatedTrack.IsTrustworthy;
                Model.Integrity = updatedTrack.Integrity;
                
                this.RaisePropertyChanged(nameof(ArtistName));
                this.RaisePropertyChanged(nameof(TrackTitle));
                this.RaisePropertyChanged(nameof(AlbumName));
                this.RaisePropertyChanged(nameof(AlbumArtUrl));
                this.RaisePropertyChanged(nameof(BpmDisplay));
                this.RaisePropertyChanged(nameof(KeyDisplay));
                this.RaisePropertyChanged(nameof(LoudnessDisplay));
                this.RaisePropertyChanged(nameof(TruePeakDisplay));
                this.RaisePropertyChanged(nameof(DynamicRangeDisplay));
                this.RaisePropertyChanged(nameof(IntegrityScore));
                this.RaisePropertyChanged(nameof(TechnicalSummary));
                this.RaisePropertyChanged(nameof(IsSecure));
                this.RaisePropertyChanged(nameof(QualityIcon));
                this.RaisePropertyChanged(nameof(QualityColor));
                this.RaisePropertyChanged(nameof(IsPrepared));
                this.RaisePropertyChanged(nameof(PreparationStatus));
                this.RaisePropertyChanged(nameof(PreparationColor));
                this.RaisePropertyChanged(nameof(PrimaryGenre));
                this.RaisePropertyChanged(nameof(DetectedSubGenre));
                this.RaisePropertyChanged(nameof(VibeColor));
                this.RaisePropertyChanged(nameof(SubGenreConfidence));
                this.RaisePropertyChanged(nameof(VibePills));
                
                // Curation & Trust
                this.RaisePropertyChanged(nameof(CurationConfidence));
                this.RaisePropertyChanged(nameof(CurationIcon));
                this.RaisePropertyChanged(nameof(CurationColor));
                this.RaisePropertyChanged(nameof(ProvenanceTooltip));

                // Audio features
                this.RaisePropertyChanged(nameof(IsEnriched));
                this.RaisePropertyChanged(nameof(WaveformData));
                
                // Update Artwork Proxy
                _artwork = new ArtworkProxy(_artworkCache, Model.AlbumArtUrl);
                this.RaisePropertyChanged(nameof(Artwork));
                this.RaisePropertyChanged(nameof(ArtworkBitmap));
            }
        });
    }

    private void PlayTrack()
    {
        // Construct a lightweight VM payload for the player
        // The Player expects a PlaylistTrackViewModel, ensuring it has the Model
        var payload = new PlaylistTrackViewModel(Model);
        
        // Publish event
        _eventBus.Publish(new PlayTrackRequestEvent(payload));
    }
    
    // Phase 11.5: Library Trust Badges
    public SLSKDONET.Data.Entities.CurationConfidence CurationConfidence => Model.CurationConfidence;

    public string CurationIcon => CurationConfidence switch
    {
        SLSKDONET.Data.Entities.CurationConfidence.Manual => "🛡️",
        SLSKDONET.Data.Entities.CurationConfidence.High => "🏅",
        SLSKDONET.Data.Entities.CurationConfidence.Medium => "🥈",
        SLSKDONET.Data.Entities.CurationConfidence.Low => "📉",
        _ => string.Empty
    };
    
    public Avalonia.Media.IBrush CurationColor => CurationConfidence switch
    {
        SLSKDONET.Data.Entities.CurationConfidence.Manual => Avalonia.Media.Brushes.LimeGreen,
        SLSKDONET.Data.Entities.CurationConfidence.High => Avalonia.Media.Brushes.Gold,
        SLSKDONET.Data.Entities.CurationConfidence.Medium => Avalonia.Media.Brushes.Silver,
        SLSKDONET.Data.Entities.CurationConfidence.Low => Avalonia.Media.Brushes.OrangeRed,
        _ => Avalonia.Media.Brushes.Transparent
    };

    public string ProvenanceTooltip => $"Confidence: {CurationConfidence}\nSource: {Model.Source}";

    public void Dispose()
    {
        _disposables.Dispose();
        // Artwork is a proxy, cache manages bitmap disposal
    }
}
