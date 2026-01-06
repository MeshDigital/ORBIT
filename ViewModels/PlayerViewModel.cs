using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Views;
// using DraggingService; // TODO: Fix drag-drop library reference

using System.Reactive.Linq;
using System.Reactive.Disposables;
using ReactiveUI;

namespace SLSKDONET.ViewModels
{
    using System.ComponentModel;
    using System.Runtime.CompilerServices;

    public partial class PlayerViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly System.Reactive.Disposables.CompositeDisposable _disposables = new();
        private bool _isDisposed;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
        private readonly IAudioPlayerService _playerService;
        private readonly DatabaseService _databaseService;
        private readonly Services.Rekordbox.AnlzFileParser _anlzParser;
        private readonly WaveformAnalysisService _waveformService;
        private readonly ArtworkCacheService _artworkCacheService;

        
        private string _trackTitle = "No Track Playing";
        public string TrackTitle
        {
            get => _trackTitle;
            set => SetProperty(ref _trackTitle, value);
        }

        private string _trackArtist = "";
        public string TrackArtist
        {
            get => _trackArtist;
            set => SetProperty(ref _trackArtist, value);
        }

        private bool _isPlaying;
        public bool IsPlaying
        {
            get => _isPlaying;
            set => SetProperty(ref _isPlaying, value);
        }

        private float _position; // 0.0 to 1.0
        public float Position
        {
            get => _position;
            set => SetProperty(ref _position, value);
        }

        private string _currentTimeStr = "0:00";
        public string CurrentTimeStr
        {
            get => _currentTimeStr;
            set => SetProperty(ref _currentTimeStr, value);
        }

        private string _totalTimeStr = "0:00";
        public string TotalTimeStr
        {
            get => _totalTimeStr;
            set => SetProperty(ref _totalTimeStr, value);
        }
        
        private int _volume = 100;
        public int Volume
        {
            get => _volume;
            set
            {
                if (SetProperty(ref _volume, value))
                {
                    OnVolumeChanged(value);
                }
            }
        }

        private bool _isPlayerInitialized;
        public bool IsPlayerInitialized
        {
            get => _isPlayerInitialized;
            set => SetProperty(ref _isPlayerInitialized, value);
        }
        
        // Queue Management
        public ObservableCollection<PlaylistTrackViewModel> Queue { get; } = new();
        
        private int _currentQueueIndex = -1;
        public int CurrentQueueIndex
        {
            get => _currentQueueIndex;
            set => SetProperty(ref _currentQueueIndex, value);
        }
        
        private PlaylistTrackViewModel? _currentTrack;
        public PlaylistTrackViewModel? CurrentTrack
        {
            get => _currentTrack;
            set => SetProperty(ref _currentTrack, value);
        }
        
        // Shuffle & Repeat
        private bool _isShuffling;
        public bool IsShuffling
        {
            get => _isShuffling;
            set => SetProperty(ref _isShuffling, value);
        }
        
        private RepeatMode _repeatMode = RepeatMode.Off;
        public RepeatMode RepeatMode
        {
            get => _repeatMode;
            set => SetProperty(ref _repeatMode, value);
        }
        
        // Player Dock Location
        private PlayerDockLocation _currentDockLocation = PlayerDockLocation.RightSidebar;
        public PlayerDockLocation CurrentDockLocation
        {
            get => _currentDockLocation;
            set => SetProperty(ref _currentDockLocation, value);
        }
        
        // Queue Visibility
        private bool _isQueueOpen;
        public bool IsQueueOpen
        {
            get => _isQueueOpen;
            set => SetProperty(ref _isQueueOpen, value);
        }
        
        // Phase 9.2: Loading & Error States
        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        private bool _hasPlaybackError;
        public bool HasPlaybackError
        {
            get => _hasPlaybackError;
            set => SetProperty(ref _hasPlaybackError, value);
        }

        private string _playbackError = string.Empty;
        public string PlaybackError
        {
            get => _playbackError;
            set => SetProperty(ref _playbackError, value);
        }

        // Phase 9.2: Album Artwork
        private string? _albumArtUrl;
        public string? AlbumArtUrl
        {
            get => _albumArtUrl;
            set => SetProperty(ref _albumArtUrl, value);
        }

        // Phase 9.3: Like Feature
        private bool _isCurrentTrackLiked;
        public bool IsCurrentTrackLiked
        {
            get => _isCurrentTrackLiked;
            set => SetProperty(ref _isCurrentTrackLiked, value);
        }
        
        // Sprint B: High-Fidelity Features
        private float _vuLeft;
        public float VuLeft
        {
            get => _vuLeft;
            set => SetProperty(ref _vuLeft, value);
        }

        private float _vuRight;
        public float VuRight
        {
            get => _vuRight;
            set => SetProperty(ref _vuRight, value);
        }

        private WaveformAnalysisData _waveformData = new();
        public WaveformAnalysisData WaveformData
        {
            get => _waveformData;
            set => SetProperty(ref _waveformData, value);
        }

        private double _pitch = 1.0;
        public double Pitch
        {
            get => _pitch;
            set
            {
                if (SetProperty(ref _pitch, value))
                {
                    _playerService.Pitch = value;
                }
            }
        }
        
        // Shuffle history to prevent immediate repeats
        private readonly List<int> _shuffleHistory = new();

        public ICommand TogglePlayPauseCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand NextTrackCommand { get; }
        public ICommand PreviousTrackCommand { get; }
        public ICommand AddToQueueCommand { get; }
        public ICommand RemoveFromQueueCommand { get; }
        public ICommand ClearQueueCommand { get; }
        public ICommand ToggleShuffleCommand { get; }
        public ICommand ToggleRepeatCommand { get; }
        public ICommand TogglePlayerDockCommand { get; }
        public ICommand ToggleQueueCommand { get; }
        public ICommand ToggleLikeCommand { get; } // Phase 9.3
        public ICommand SeekCommand { get; } // Phase 12.6: Waveform Seeking

        // Phase 5C: UI Throttling
        private DateTime _lastTimeUpdate = DateTime.MinValue;

        public PlayerViewModel(IAudioPlayerService playerService, DatabaseService databaseService, IEventBus eventBus, Services.Rekordbox.AnlzFileParser anlzParser, WaveformAnalysisService waveformService, ArtworkCacheService artworkCacheService)
        {
            _playerService = playerService;
            _databaseService = databaseService;
            _anlzParser = anlzParser;
            _waveformService = waveformService;
            _artworkCacheService = artworkCacheService;
            
            // Phase 6B: Subscribe to playback requests
            eventBus.GetEvent<PlayTrackRequestEvent>().Subscribe(evt => 
            {
                if (evt.Track != null && !string.IsNullOrEmpty(evt.Track.Model.ResolvedFilePath))
                {
                    PlayTrack(evt.Track.Model.ResolvedFilePath, evt.Track.Title ?? "Unknown", evt.Track.Artist ?? "Unknown");
                }
            }).DisposeWith(_disposables);

            // Phase 6B: Play Album Request (Queue Management)
            eventBus.GetEvent<PlayAlbumRequestEvent>().Subscribe(evt =>
            {
                if (evt.Tracks == null || !evt.Tracks.Any()) return;
                
                Dispatcher.UIThread.Post(() =>
                {
                    // 1. Clear existing queue
                    ClearQueue();
                    
                    // 2. Add all tracks to queue
                    foreach (var track in evt.Tracks)
                    {
                        // Only add tracks with valid file paths
                        if (!string.IsNullOrEmpty(track.ResolvedFilePath))
                        {
                            var vm = new PlaylistTrackViewModel(track, eventBus, null, _artworkCacheService);
                            Queue.Add(vm);
                        }
                    }
                    
                    // 3. Play first track if any were added
                    if (Queue.Any())
                    {
                        CurrentQueueIndex = 0;
                        PlayTrackAtIndex(0);
                    }
                });
            }).DisposeWith(_disposables);
            
            // Ensure IsPlaying is synced
            IsPlaying = _playerService.IsPlaying;
            
            // Phase 9.6: Removed premature check for IsPlayerInitialized. 
            // AudioPlayerService initializes lazily, so this check was always failing on startup.
            // We now rely on Play() to trigger init and handle errors there.
            
            // Player Service Events via Reactive patterns to ensure cleanup
            Observable.FromEventPattern(h => _playerService.PausableChanged += h, h => _playerService.PausableChanged -= h)
                .Subscribe(_ => Dispatcher.UIThread.Post(() => IsPlaying = _playerService.IsPlaying))
                .DisposeWith(_disposables);

            Observable.FromEventPattern(h => _playerService.EndReached += h, h => _playerService.EndReached -= h)
                .Subscribe(e => Dispatcher.UIThread.Post(() => OnEndReached(e.Sender, EventArgs.Empty)))
                .DisposeWith(_disposables);

            Observable.FromEventPattern<float>(h => _playerService.PositionChanged += h, h => _playerService.PositionChanged -= h)
                .Subscribe(e => Dispatcher.UIThread.Post(() => Position = e.EventArgs))
                .DisposeWith(_disposables);

            Observable.FromEventPattern<long>(h => _playerService.TimeChanged += h, h => _playerService.TimeChanged -= h)
                .Subscribe(e => 
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastTimeUpdate).TotalMilliseconds > 250) // Throttle to 4fps
                    {
                        Dispatcher.UIThread.Post(() => CurrentTimeStr = TimeSpan.FromMilliseconds(e.EventArgs).ToString(@"m\:ss"));
                        _lastTimeUpdate = now;
                    }
                })
                .DisposeWith(_disposables);

            Observable.FromEventPattern<long>(h => _playerService.LengthChanged += h, h => _playerService.LengthChanged -= h)
                .Subscribe(e => Dispatcher.UIThread.Post(() => TotalTimeStr = TimeSpan.FromMilliseconds(e.EventArgs).ToString(@"m\:ss")))
                .DisposeWith(_disposables);

            Observable.FromEventPattern<AudioLevelsEventArgs>(h => _playerService.AudioLevelsChanged += h, h => _playerService.AudioLevelsChanged -= h)
                .Subscribe(e => Dispatcher.UIThread.Post(() => 
                {
                    VuLeft = e.EventArgs.Left;
                    VuRight = e.EventArgs.Right;
                }))
                .DisposeWith(_disposables);

            TogglePlayPauseCommand = new RelayCommand(TogglePlayPause);
            StopCommand = new RelayCommand(Stop);
            NextTrackCommand = new RelayCommand(PlayNextTrack, () => HasNextTrack());
            PreviousTrackCommand = new RelayCommand(PlayPreviousTrack, () => HasPreviousTrack());
            AddToQueueCommand = new RelayCommand<PlaylistTrackViewModel>(AddToQueue);
            RemoveFromQueueCommand = new RelayCommand<PlaylistTrackViewModel>(RemoveFromQueue);
            ClearQueueCommand = new RelayCommand(ClearQueue, () => Queue.Any());
            ToggleShuffleCommand = new RelayCommand(ToggleShuffle);
            ToggleRepeatCommand = new RelayCommand(ToggleRepeat);
            TogglePlayerDockCommand = new RelayCommand(TogglePlayerDock);
            ToggleQueueCommand = new RelayCommand(ToggleQueue);
            ToggleLikeCommand = new AsyncRelayCommand(ToggleLikeAsync); // Phase 9.3
            SeekCommand = new RelayCommand<float>(Seek);
            
            // Phase 0: Queue persistence - auto-save on changes
            Queue.CollectionChanged += OnQueueCollectionChanged;
            
            // Load saved queue on startup
            _ = LoadQueueAsync();
        }

        private async void OnQueueCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
             await SaveQueueAsync();
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
                Queue.CollectionChanged -= OnQueueCollectionChanged;
                
                // Dispose items in queue if they are IDisposable
                foreach (var item in Queue)
                {
                    item.Dispose();
                }
            }
            _isDisposed = true;
        }

        
        private void ToggleQueue()
        {
            IsQueueOpen = !IsQueueOpen;
        }

        // Phase 9.3: Like Feature Implementation
        private async System.Threading.Tasks.Task ToggleLikeAsync()
        {
            if (CurrentTrack == null) return;

            // Toggle state
            IsCurrentTrackLiked = !IsCurrentTrackLiked;

            // Persist to database (atomic operation)
            try
            {
                var track = CurrentTrack.Model;
                track.IsLiked = IsCurrentTrackLiked;
                
                // Manually map to Entity for simple update
                var entity = new SLSKDONET.Data.PlaylistTrackEntity
                {
                    Id = track.Id,
                    IsLiked = track.IsLiked,
                    Rating = track.Rating,
                    PlayCount = track.PlayCount,
                    LastPlayedAt = track.LastPlayedAt,
                    Status = track.Status
                };

                await _databaseService.UpdatePlaylistTrackAsync(entity);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlayerViewModel] Failed to save like status: {ex.Message}");
                // Revert on failure
                IsCurrentTrackLiked = !IsCurrentTrackLiked;
            }
        }        
        // Queue Management Methods
        private void OnEndReached(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsPlaying = false;
                
                // Auto-play next track if available
                if (HasNextTrack())
                {
                    PlayNextTrack();
                }
                else if (RepeatMode == RepeatMode.All && Queue.Any())
                {
                    // Restart queue from beginning
                    CurrentQueueIndex = 0;
                    PlayTrackAtIndex(0);
                }
            });
        }
        
        public void AddToQueue(PlaylistTrackViewModel? track)
        {
            if (track == null) return;
            
            Dispatcher.UIThread.Post(() =>
            {
                Queue.Add(track);
                
                // If nothing playing, start immediately
                if (!IsPlaying && Queue.Count == 1)
                {
                    CurrentQueueIndex = 0;
                    PlayTrackAtIndex(0);
                }
            });
        }
        
        public void RemoveFromQueue(PlaylistTrackViewModel? track)
        {
            if (track == null) return;
            
            Dispatcher.UIThread.Post(() =>
            {
                var index = Queue.IndexOf(track);
                if (index >= 0)
                {
                    Queue.RemoveAt(index);
                    
                    // Adjust current index if needed
                    if (index < CurrentQueueIndex)
                    {
                        CurrentQueueIndex--;
                    }
                    else if (index == CurrentQueueIndex)
                    {
                        // Removed currently playing track
                        if (Queue.Any())
                        {
                            PlayTrackAtIndex(Math.Min(CurrentQueueIndex, Queue.Count - 1));
                        }
                        else
                        {
                            Stop();
                        }
                    }
                }
            });
        }
        
        public void ClearQueue()
        {
            Dispatcher.UIThread.Post(() =>
            {
                Queue.Clear();
                CurrentQueueIndex = -1;
                CurrentTrack = null;
                _shuffleHistory.Clear();
                Stop();
            });
        }
        
        /// <summary>
        /// Moves a track in the queue from one position to another.
        /// Used for drag-and-drop reordering.
        /// </summary>
        public void MoveTrack(string globalId, int targetIndex)
        {
            if (string.IsNullOrEmpty(globalId) || targetIndex < 0)
                return;
                
            Dispatcher.UIThread.Post(() =>
            {
                var track = Queue.FirstOrDefault(t => t.GlobalId == globalId);
                if (track == null) return;
                    
                var oldIndex = Queue.IndexOf(track);
                if (oldIndex < 0 || oldIndex == targetIndex) return;
                    
                targetIndex = Math.Clamp(targetIndex, 0, Queue.Count - 1);
                Queue.Move(oldIndex, targetIndex);
                
                if (oldIndex == CurrentQueueIndex)
                    CurrentQueueIndex = targetIndex;
                else if (oldIndex < CurrentQueueIndex && targetIndex >= CurrentQueueIndex)
                    CurrentQueueIndex--;
                else if (oldIndex > CurrentQueueIndex && targetIndex <= CurrentQueueIndex)
                    CurrentQueueIndex++;
            });
        }
        
        private void PlayNextTrack()
        {
            if (!Queue.Any()) return;
            
            int nextIndex;
            
            if (RepeatMode == RepeatMode.One)
            {
                // Repeat current track
                nextIndex = CurrentQueueIndex;
            }
            else if (IsShuffling)
            {
                nextIndex = GetRandomTrackIndex();
            }
            else
            {
                nextIndex = CurrentQueueIndex + 1;
                if (nextIndex >= Queue.Count)
                {
                    if (RepeatMode == RepeatMode.All)
                    {
                        nextIndex = 0;
                    }
                    else
                    {
                        return; // End of queue
                    }
                }
            }
            
            PlayTrackAtIndex(nextIndex);
        }
        
        private void PlayPreviousTrack()
        {
            if (!Queue.Any()) return;
            
            // If more than 3 seconds into track, restart current track
            if (Position > 0.05f)
            {
                Seek(0);
                return;
            }
            
            int prevIndex = CurrentQueueIndex - 1;
            if (prevIndex < 0)
            {
                if (RepeatMode == RepeatMode.All)
                {
                    prevIndex = Queue.Count - 1;
                }
                else
                {
                    return; // Start of queue
                }
            }
            
            PlayTrackAtIndex(prevIndex);
        }
        
        private void PlayTrackAtIndex(int index)
        {
            if (index < 0 || index >= Queue.Count) return;
            
            CurrentQueueIndex = index;
            CurrentTrack = Queue[index];
            
            var track = Queue[index];
            var filePath = track.Model?.ResolvedFilePath;
            
            // Phase 9.2 & 9.3: Set album artwork and like status
            Dispatcher.UIThread.Post(async () =>
            {
                AlbumArtUrl = track.Model?.AlbumArtUrl;
                IsCurrentTrackLiked = track.Model?.IsLiked ?? false;
                
                // Ensure bitmap is loaded for UI
                // Phase 0: Artwork loaded via Proxy
                // if (track.ArtworkBitmap == null) await track.LoadAlbumArtworkAsync();
            });

            if (!string.IsNullOrEmpty(filePath))
            {
                // Max Ultra Waveform Logic
                // 1. Try Rekordbox ANLZ (Fastest, Pre-analyzed)
                var anlz = _anlzParser.TryFindAndParseAnlz(filePath);
                if (anlz != null && anlz.WaveformData != null && anlz.WaveformData.Length > 0)
                {
                     // Convert legacy byte[] to new high-res format (Peak only, assume RMS = Peak for simple view)
                     WaveformData = new WaveformAnalysisData 
                     { 
                         PeakData = anlz.WaveformData,
                         RmsData = anlz.WaveformData, // Fallback
                         PointsPerSecond = 100 
                     };
                }
                else
                {
                     // 2. Generate High-Fidelity Waveform with FFmpeg (Async)
                     // Fire and forget, UI will update when handy
                     WaveformData = new WaveformAnalysisData(); // Clear old
                     _ = Task.Run(async () => 
                     {
                         try 
                         {
                             var waveform = await _waveformService.GenerateWaveformAsync(filePath);
                             Dispatcher.UIThread.Post(() => WaveformData = waveform);
                         }
                         catch (Exception ex)
                         {
                             Console.WriteLine($"[PlayerViewModel] Waveform generation failed: {ex.Message}");
                         }
                     });
                }
                
                PlayTrack(filePath, track.Title ?? "Unknown", track.Artist ?? "Unknown");
            }
        }
        
        private bool HasNextTrack()
        {
            if (!Queue.Any()) return false;
            if (RepeatMode != RepeatMode.Off) return true;
            return CurrentQueueIndex < Queue.Count - 1;
        }
        
        private bool HasPreviousTrack()
        {
            if (!Queue.Any()) return false;
            if (RepeatMode == RepeatMode.All) return true;
            return CurrentQueueIndex > 0;
        }
        
        private int GetRandomTrackIndex()
        {
            if (Queue.Count <= 1) return 0;
            
            var random = new Random();
            int nextIndex;
            int attempts = 0;
            
            do
            {
                nextIndex = random.Next(Queue.Count);
                attempts++;
            }
            while (_shuffleHistory.Contains(nextIndex) && attempts < 10);
            
            // Track shuffle history (last 10 tracks)
            _shuffleHistory.Add(nextIndex);
            if (_shuffleHistory.Count > 10)
            {
                _shuffleHistory.RemoveAt(0);
            }
            
            return nextIndex;
        }
        
        private void ToggleShuffle()
        {
            IsShuffling = !IsShuffling;
            if (!IsShuffling)
            {
                _shuffleHistory.Clear();
            }
        }
        
        private void ToggleRepeat()
        {
            RepeatMode = RepeatMode switch
            {
                RepeatMode.Off => RepeatMode.All,
                RepeatMode.All => RepeatMode.One,
                RepeatMode.One => RepeatMode.Off,
                _ => RepeatMode.Off
            };
        }
        
        private void TogglePlayerDock()
        {
            CurrentDockLocation = CurrentDockLocation == PlayerDockLocation.BottomBar 
                ? PlayerDockLocation.RightSidebar 
                : PlayerDockLocation.BottomBar;
        }

        private string? _currentFilePath;

        private void TogglePlayPause()
        {
            if (IsPlaying)
            {
                _playerService.Pause();
                IsPlaying = false; // Update immediate state
            }
            else
            {
                // Case 1: Track is loaded but paused/stopped
                // Check _currentFilePath (Ad-hoc play) OR CurrentTrack (Queue play)
                string? path = _currentFilePath ?? CurrentTrack?.Model?.ResolvedFilePath;
                string title = TrackTitle;
                string artist = TrackArtist;

                if (!string.IsNullOrEmpty(path))
                {
                    // Case: Track is loaded. Check if we can resume.
                    // We check if we are significantly into the track and not at the end.
                    bool canResume = _playerService.Length > 0 && _playerService.Time < _playerService.Length - 100; // 100ms buffer
                    
                    if (canResume)
                    {
                        _playerService.Pause(); // Resume
                        IsPlaying = true; // Assume success
                    }
                    else
                    {
                       // Track finished or not loaded. Restart.
                       PlayTrack(path!, CurrentTrack?.Title ?? "Unknown", CurrentTrack?.Artist ?? "Unknown");
                    }
                }
                // Case 2: No track loaded, but Queue has items
                else if (Queue.Any())
                {
                    // Start from beginning or current index
                    if (CurrentQueueIndex < 0) CurrentQueueIndex = 0;
                    PlayTrackAtIndex(CurrentQueueIndex);
                }
                
                IsPlaying = _playerService.IsPlaying;
            }
        }

        private void Stop()
        {
            _playerService.Stop();
            IsPlaying = false;
            Position = 0;
            CurrentTimeStr = "0:00";
        }
        
        // Volume Change
        private void OnVolumeChanged(int value)
        {
            _playerService.Volume = value;
        }

        // Seek (User Drag)
        public void Seek(float position)
        {
            _playerService.Position = position;
        }
        
        // Helper to load track
        public void PlayTrack(string filePath, string title, string artist)
        {
            Console.WriteLine($"[PlayerViewModel] PlayTrack called with: {filePath}");

            // Phase 9.2: Show loading state
            Dispatcher.UIThread.Post(() =>
            {
                IsLoading = true;
                HasPlaybackError = false;
                PlaybackError = string.Empty;
            });

            try
            {
                _currentFilePath = filePath;
                TrackTitle = title;
                TrackArtist = artist;
                
                _playerService.Play(filePath);
                IsPlaying = true;

                // Hide loading state
                Dispatcher.UIThread.Post(() => IsLoading = false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlayerViewModel] Playback error: {ex.Message}");

                // Phase 9.2: Show error state with thread-safe updates
                Dispatcher.UIThread.Post(() =>
                {
                    IsLoading = false;
                    HasPlaybackError = true;
                    PlaybackError = $"Playback failed: {ex.Message}";

                    // Auto-dismiss error after 7 seconds
                    var dismissTimer = new System.Timers.Timer(7000);
                    dismissTimer.Elapsed += (s, args) =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            HasPlaybackError = false;
                            PlaybackError = string.Empty;
                        });
                        dismissTimer.Dispose();
                    };
                    dismissTimer.AutoReset = false;
                    dismissTimer.Start();
                });

                IsPlaying = false;
            }
        }

        // Phase 0: Queue Persistence Methods

        /// <summary>
        /// Saves the current queue to the database.
        /// </summary>
        private async System.Threading.Tasks.Task SaveQueueAsync()
        {
            try
            {
                var queueItems = Queue.Select((track, index) => (
                    trackId: track.Id,
                    position: index,
                    isCurrent: index == CurrentQueueIndex
                )).ToList();

                await _databaseService.SaveQueueAsync(queueItems);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlayerViewModel] Failed to save queue: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads the saved queue from the database on startup.
        /// </summary>
        private async System.Threading.Tasks.Task LoadQueueAsync()
        {
            try
            {
                var savedQueue = await _databaseService.LoadQueueAsync();
                
                if (!savedQueue.Any())
                    return;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Queue.Clear();
                    
                    int currentIndex = -1;
                    for (int i = 0; i < savedQueue.Count; i++)
                    {
                        var (track, isCurrent) = savedQueue[i];
                        var vm = new PlaylistTrackViewModel(track);
                        Queue.Add(vm);
                        
                        if (isCurrent)
                            currentIndex = i;
                    }
                    
                    // Restore current track position
                    if (currentIndex >= 0 && currentIndex < Queue.Count)
                    {
                        CurrentQueueIndex = currentIndex;
                        CurrentTrack = Queue[currentIndex];
                    }
                    
                    Console.WriteLine($"[PlayerViewModel] Loaded {savedQueue.Count} tracks from saved queue");
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlayerViewModel] Failed to load queue: {ex.Message}");
            }
        }

        // Drag & Drop
        // TODO: Fix drag-drop library reference
        /*
        public DraggingServiceDropEvent OnDropQueue => (DraggingServiceDropEventsArgs args) => {
            var droppedTracks = DragContext.Current as List<PlaylistTrackViewModel>;
            if (droppedTracks != null && droppedTracks.Any())
            {
                Dispatcher.UIThread.Post(() => {
                    foreach (var track in droppedTracks)
                    {
                        AddToQueue(track);
                    }
                });
            }
        };
        */
    }
}
