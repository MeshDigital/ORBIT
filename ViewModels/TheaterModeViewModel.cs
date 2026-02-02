using ReactiveUI;
using SLSKDONET.Services;
using SLSKDONET.Models;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Tagging;
using SLSKDONET.ViewModels.Stem;
using SLSKDONET.Models.Stem;
using Avalonia.Threading;

namespace SLSKDONET.ViewModels;

public class TheaterModeViewModel : ReactiveObject
{
    private readonly PlayerViewModel _playerViewModel;
    private readonly INavigationService _navigationService;
    private readonly ILibraryService _libraryService;
    private readonly IEventBus _eventBus;
    private readonly ArtworkCacheService _artworkCache;
    private readonly StemSeparationService _separationService;
    private readonly RealTimeStemEngine _stemEngine;
    private readonly IUniversalCueService _cueService;
    private readonly WaveformAnalysisService _waveformService;
    private readonly DispatcherTimer _playbackTimer;

    public PlayerViewModel Player => _playerViewModel;

    private bool _isLibraryVisible = true;
    public bool IsLibraryVisible
    {
        get => _isLibraryVisible;
        set => this.RaiseAndSetIfChanged(ref _isLibraryVisible, value);
    }

    private bool _isTechnicalVisible = true;
    public bool IsTechnicalVisible
    {
        get => _isTechnicalVisible;
        set => this.RaiseAndSetIfChanged(ref _isTechnicalVisible, value);
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set => this.RaiseAndSetIfChanged(ref _searchText, value);
    }

    public ObservableCollection<PlaylistTrackViewModel> SearchResults { get; } = new();

    private int _libraryTabIndex;
    public int LibraryTabIndex
    {
        get => _libraryTabIndex;
        set => this.RaiseAndSetIfChanged(ref _libraryTabIndex, value);
    }

    private double _visualIntensity = 1.0;
    public double VisualIntensity
    {
        get => _visualIntensity;
        set => this.RaiseAndSetIfChanged(ref _visualIntensity, value);
    }

    private bool _isResetTriggered;
    public bool IsResetTriggered
    {
        get => _isResetTriggered;
        set => this.RaiseAndSetIfChanged(ref _isResetTriggered, value);
    }

    private bool _isStyleSelectionOpen;
    public bool IsStyleSelectionOpen
    {
        get => _isStyleSelectionOpen;
        set => this.RaiseAndSetIfChanged(ref _isStyleSelectionOpen, value);
    }

    public ObservableCollection<VisualizerStyle> AvailableStyles { get; } = new(Enum.GetValues<VisualizerStyle>());

    // --- Performance Hub Properties ---
    public ObservableCollection<OrbitCue> CurrentCues { get; } = new();

    private StemMixerViewModel? _stemMixer;
    public StemMixerViewModel? StemMixer
    {
        get => _stemMixer;
        set => this.RaiseAndSetIfChanged(ref _stemMixer, value);
    }

    private bool _isStemActive;
    public bool IsStemActive
    {
        get => _isStemActive;
        set => this.RaiseAndSetIfChanged(ref _isStemActive, value);
    }

    private bool _isSeparating;
    public bool IsSeparating
    {
        get => _isSeparating;
        set => this.RaiseAndSetIfChanged(ref _isSeparating, value);
    }

    private double _separationProgress;
    public double SeparationProgress
    {
        get => _separationProgress;
        set => this.RaiseAndSetIfChanged(ref _separationProgress, value);
    }

    private int _rightPanelTabIndex;
    public int RightPanelTabIndex
    {
        get => _rightPanelTabIndex;
        set => this.RaiseAndSetIfChanged(ref _rightPanelTabIndex, value);
    }

    // --- Unified Playback Properties ---
    private double _displayPosition;
    public double DisplayPosition
    {
        get => _displayPosition;
        set => this.RaiseAndSetIfChanged(ref _displayPosition, value);
    }

    private string _displayCurrentTime = "0:00";
    public string DisplayCurrentTime
    {
        get => _displayCurrentTime;
        set => this.RaiseAndSetIfChanged(ref _displayCurrentTime, value);
    }

    private string _displayTotalTime = "0:00";
    public string DisplayTotalTime
    {
        get => _displayTotalTime;
        set => this.RaiseAndSetIfChanged(ref _displayTotalTime, value);
    }

    private bool _isUnifiedPlaying;
    public bool IsUnifiedPlaying
    {
        get => _isUnifiedPlaying;
        set => this.RaiseAndSetIfChanged(ref _isUnifiedPlaying, value);
    }

    public TheaterModeViewModel(
        PlayerViewModel playerViewModel, 
        INavigationService navigationService,
        ILibraryService libraryService,
        IEventBus eventBus,
        ArtworkCacheService artworkCache,
        StemSeparationService separationService,
        RealTimeStemEngine stemEngine,
        IUniversalCueService cueService,
        WaveformAnalysisService waveformService)
    {
        _playerViewModel = playerViewModel;
        _navigationService = navigationService;
        _libraryService = libraryService;
        _eventBus = eventBus;
        _artworkCache = artworkCache;
        _separationService = separationService;
        _stemEngine = stemEngine;
        _cueService = cueService;
        _waveformService = waveformService;
        
        CloseTheaterCommand = ReactiveCommand.Create(CloseTheater);
        ToggleLibraryCommand = ReactiveCommand.Create(() => IsLibraryVisible = !IsLibraryVisible);
        ToggleTechnicalCommand = ReactiveCommand.Create(() => IsTechnicalVisible = !IsTechnicalVisible);
        PlayTrackCommand = ReactiveCommand.Create<PlaylistTrackViewModel>(PlayTrack);
        AddToQueueCommand = ReactiveCommand.Create<PlaylistTrackViewModel>(AddToQueue);
        ResetVisualizerCommand = ReactiveCommand.Create(() => IsResetTriggered = true);
        ToggleStyleSelectionCommand = ReactiveCommand.Create(() => IsStyleSelectionOpen = !IsStyleSelectionOpen);
        SelectStyleCommand = ReactiveCommand.Create<VisualizerStyle>(style => 
        {
            _playerViewModel.CurrentVisualStyle = style;
            IsStyleSelectionOpen = false;
        });

        ToggleStemsCommand = ReactiveCommand.CreateFromTask(ExecuteToggleStemsAsync);
        AddCueCommand = ReactiveCommand.Create(ExecuteAddCue);
        SyncCuesCommand = ReactiveCommand.CreateFromTask(ExecuteSyncCuesAsync);
        SeekToCueCommand = ReactiveCommand.Create<OrbitCue>(ExecuteSeekToCue);

        // Unified Commands
        ToggleUnifiedPlaybackCommand = ReactiveCommand.Create(ExecuteToggleUnifiedPlayback);
        SeekUnifiedCommand = ReactiveCommand.Create<float>(ExecuteSeekUnified);

        this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(300))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async query => await PerformSearchAsync(query));

        // Auto-load cues and stems on track change
        this.WhenAnyValue(x => x.Player.CurrentTrack)
            .Where(track => track != null)
            .Subscribe(async track => await LoadPerformanceDataAsync(track!));

        // Sync logic for Player updates when Stems are NOT active
        this.WhenAnyValue(x => x.Player.Position)
            .Where(_ => !IsStemActive)
            .Subscribe(pos => DisplayPosition = pos);
            
        this.WhenAnyValue(x => x.Player.CurrentTimeStr)
            .Where(_ => !IsStemActive)
            .Subscribe(t => DisplayCurrentTime = t);

        this.WhenAnyValue(x => x.Player.TotalTimeStr)
            .Where(_ => !IsStemActive)
            .Subscribe(t => DisplayTotalTime = t);
            
        this.WhenAnyValue(x => x.Player.IsPlaying)
            .Where(_ => !IsStemActive)
            .Subscribe(p => IsUnifiedPlaying = p);

        // Timer for Stem Playback Sync
        _playbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33) // ~30fps
        };
        _playbackTimer.Tick += OnPlaybackTimerTick;
    }

    public System.Windows.Input.ICommand CloseTheaterCommand { get; }
    public System.Windows.Input.ICommand ToggleLibraryCommand { get; }
    public System.Windows.Input.ICommand ToggleTechnicalCommand { get; }
    public System.Windows.Input.ICommand PlayTrackCommand { get; }
    public System.Windows.Input.ICommand AddToQueueCommand { get; }
    public System.Windows.Input.ICommand ResetVisualizerCommand { get; }
    public System.Windows.Input.ICommand ToggleStyleSelectionCommand { get; }
    public System.Windows.Input.ICommand SelectStyleCommand { get; }
    public System.Windows.Input.ICommand ToggleStemsCommand { get; }
    public System.Windows.Input.ICommand AddCueCommand { get; }
    public System.Windows.Input.ICommand SyncCuesCommand { get; }
    public System.Windows.Input.ICommand SeekToCueCommand { get; }
    public System.Windows.Input.ICommand ToggleUnifiedPlaybackCommand { get; }
    public System.Windows.Input.ICommand SeekUnifiedCommand { get; }

    private void OnPlaybackTimerTick(object? sender, EventArgs e)
    {
        if (IsStemActive && StemMixer != null)
        {
             var current = _stemEngine.CurrentTime;
             var total = _stemEngine.TotalTime;
             
             if (total.TotalSeconds > 0)
             {
                 DisplayPosition = current.TotalSeconds / total.TotalSeconds;
             }
             
             DisplayCurrentTime = current.ToString(@"m\:ss");
             DisplayTotalTime = total.ToString(@"m\:ss");
        }
    }

    private void ExecuteToggleUnifiedPlayback()
    {
        if (IsStemActive)
        {
            if (IsUnifiedPlaying)
            {
                _stemEngine.Pause();
                IsUnifiedPlaying = false;
                _playbackTimer.Stop();
            }
            else
            {
                _stemEngine.Play();
                IsUnifiedPlaying = true;
                _playbackTimer.Start();
            }
        }
        else
        {
            if (Player.IsPlaying) Player.StopCommand.Execute(null); // Or TogglePlayPause
            else Player.TogglePlayPauseCommand.Execute(null);
        }
    }

    private void ExecuteSeekUnified(float relativePos)
    {
        if (IsStemActive)
        {
             var totalSecs = _stemEngine.TotalTime.TotalSeconds;
             if (totalSecs > 0)
             {
                 _stemEngine.Seek(totalSecs * relativePos);
                 DisplayPosition = relativePos; // Instant feedback
             }
        }
        else
        {
            Player.SeekCommand.Execute(relativePos);
        }
    }

    private void ExecuteSeekToCue(OrbitCue cue)
    {
        if (IsStemActive)
        {
            _stemEngine.Seek(cue.Timestamp);
        }
        else
        {
            Player.Position = (float)cue.Timestamp; // Actually Player.SeekCommand accepts float 0-1 or we need seconds?
            // Player.Position is 0-1, cue.Timestamp is Seconds.
            // We need to convert seconds to 0-1 for Player.
            // Or use Player.SeekCommand if it supports seconds... Player.SeekCommand takes relative float.
            // Let's calculate:
             if (Player.LengthMs > 0)
             {
                 double totalSeconds = Player.LengthMs / 1000.0;
                 Player.SeekCommand.Execute((float)(cue.Timestamp / totalSeconds));
             }
        }
    }

    private async Task LoadPerformanceDataAsync(PlaylistTrackViewModel track)
    {
        // 1. Load Cues from DB
        CurrentCues.Clear();
        if (!string.IsNullOrEmpty(track.Model.CuePointsJson))
        {
            try
            {
                var cues = System.Text.Json.JsonSerializer.Deserialize<List<OrbitCue>>(track.Model.CuePointsJson);
                if (cues != null)
                {
                    foreach (var cue in cues) CurrentCues.Add(cue);
                }
            }
            catch { /* Skip malformed json */ }
        }

        // 2. Check if stems exist
        bool hasStems = _separationService.HasStems(track.Model.TrackUniqueHash ?? "");
        
        // Don't auto-activate stems, just let the user know if they are available?
        // Or if previously active? For now reset.
        if (IsStemActive)
        {
             await ExecuteToggleStemsAsync(); // Toggle OFF when changing track
        }
    }

    private async Task InitializeStemMixerAsync(PlaylistTrack track)
    {
        var dict = _separationService.GetStemPaths(track.TrackUniqueHash ?? "");
        if (dict.Count == 0) return;

        // Ensure engine is loaded
        _stemEngine.LoadStems(dict);
        
        // Create VM
        StemMixer = new StemMixerViewModel(_stemEngine);

        // Inject Waveforms into Channels
        // The StemMixer ctor creates channels for all StemTypes. We iterate them.
        foreach (var channel in StemMixer.Channels)
        {
            if (dict.TryGetValue(channel.Type, out var path))
            {
                 try
                {
                    var waveform = await _waveformService.GenerateWaveformAsync(path, System.Threading.CancellationToken.None);
                    channel.WaveformData = waveform;
                }
                catch { }
            }
            else
            {
                 // Channel exists in VM but no file for it? Disable/Hide?
                 channel.Volume = 0;
                 channel.IsMuted = true;
            }
        }
    }

    private async Task ExecuteToggleStemsAsync()
    {
        if (IsStemActive)
        {
            // Transition: STEMS -> PLAYER
            _playbackTimer.Stop();
            _stemEngine.Pause();
            
            // Sync Player to where we left off
            var timeMs = _stemEngine.CurrentTime.TotalMilliseconds;
            if (Player.LengthMs > 0)
            {
                float rel = (float)(timeMs / Player.LengthMs);
                Player.SeekCommand.Execute(rel);
            }
            
            IsStemActive = false;
            IsUnifiedPlaying = false; // Player starts paused on sync usually, or should we auto-play?
            
            // Resume Player if we were playing? Let's just pause for safety.
            
            StemMixer = null;
            // _stemEngine.Dispose(); // Keep it alive or dispose? Dispose to free handles.
            // _stemEngine is singleton? NO. It's injected. Lifecycle managed by DI?
            // If Transient, we can dispose. If Singleton, we stop.
            // Assuming it's Scoped/Singleton, we just stop.
            return;
        }

        var track = Player.CurrentTrack;
        if (track == null) return;

        // Transition: PLAYER -> STEMS
        Player.StopCommand.Execute(null); // Stop main player
        IsUnifiedPlaying = false;
        
        IsSeparating = true;
        SeparationProgress = 0;
        try
        {
            // Separation loop
            var dict = await _separationService.SeparateTrackAsync(track.Model.ResolvedFilePath ?? "", track.Model.TrackUniqueHash ?? "");
            await InitializeStemMixerAsync(track.Model);
            
            // Sync Stem Engine to Player position
             double playerSecs = Player.Position * (Player.LengthMs / 1000.0);
             _stemEngine.Seek(playerSecs);
             DisplayPosition = Player.Position;
             
             // Start timer
             _playbackTimer.Start();
             
             // Auto-play stems?
             _stemEngine.Play();
             IsUnifiedPlaying = true;
             
            IsStemActive = true;
        }
        finally
        {
            IsSeparating = false;
        }
    }

    private void ExecuteAddCue()
    {
        double currentSeconds = 0;
        
        if (IsStemActive)
        {
            currentSeconds = _stemEngine.CurrentTime.TotalSeconds;
        }
        else
        {
             if (Player.LengthMs > 0)
                currentSeconds = Player.Position * (Player.LengthMs / 1000.0);
        }

        var cue = new OrbitCue
        {
            Timestamp = currentSeconds,
            Name = $"CUE {CurrentCues.Count + 1}",
            Role = CueRole.Custom,
            Source = CueSource.User,
            Confidence = 1.0f
        };
        CurrentCues.Add(cue);
    }

    private async Task ExecuteSyncCuesAsync()
    {
        var track = Player.CurrentTrack;
        if (track == null || string.IsNullOrEmpty(track.Model.ResolvedFilePath)) return;

        await _cueService.SyncToTagsAsync(track.Model.ResolvedFilePath, CurrentCues.ToList());
        
        // Update model and DB
        track.Model.CuePointsJson = System.Text.Json.JsonSerializer.Serialize(CurrentCues.ToList());
        await _libraryService.UpdateTrackCuePointsAsync(track.Model.TrackUniqueHash ?? "", track.Model.CuePointsJson);
    }

    private void CloseTheater()
    {
        // Cleanup
        if (IsStemActive)
        {
             _playbackTimer.Stop();
             _stemEngine.Pause();
             IsStemActive = false;
        }
        _navigationService.GoBack();
    }

    private void PlayTrack(PlaylistTrackViewModel track)
    {
        if (track == null) return;
        
        if (string.IsNullOrEmpty(track.Model.ResolvedFilePath))
        {
            System.Diagnostics.Debug.WriteLine($"[TheaterMode] Cannot play track '{track.Title}': File path is missing.");
            return;
        }

        System.Diagnostics.Debug.WriteLine($"[TheaterMode] Requesting playback for: {track.Title} ({track.Model.ResolvedFilePath})");
        _eventBus.Publish(new SLSKDONET.Models.PlayTrackRequestEvent(track));
    }

    private void AddToQueue(PlaylistTrackViewModel track)
    {
        if (track == null) return;
        _eventBus.Publish(new SLSKDONET.Models.AddToQueueRequestEvent(track));
    }

    private async Task PerformSearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            SearchResults.Clear();
            return;
        }

        try
        {
            var results = await _libraryService.SearchLibraryEntriesWithStatusAsync(query, 20);
            SearchResults.Clear();
            
            // Auto-switch to Results tab
            LibraryTabIndex = 1;
            foreach (var entry in results)
            {
                if (string.IsNullOrEmpty(entry.FilePath)) continue;

                // Map LibraryEntry to PlaylistTrackViewModel
                var model = new PlaylistTrack
                {
                    Artist = entry.Artist,
                    Title = entry.Title,
                    Album = entry.Album,
                    TrackUniqueHash = entry.UniqueHash,
                    ResolvedFilePath = entry.FilePath,
                    Status = TrackStatus.Downloaded,
                    Format = entry.Format,
                    Bitrate = entry.Bitrate,
                    MusicalKey = entry.MusicalKey,
                    BPM = entry.BPM
                };
                
                var vm = new PlaylistTrackViewModel(model, _eventBus, _libraryService, _artworkCache);
                SearchResults.Add(vm);
            }
        }
        catch (Exception)
        {
            // Log error
        }
    }
}
