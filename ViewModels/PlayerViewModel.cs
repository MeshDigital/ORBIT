using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input; // For ICommand
using Avalonia.Threading;
using SLSKDONET.Services;
using SLSKDONET.Views;
using SLSKDONET.Models;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SLSKDONET.ViewModels
{
    public partial class PlayerViewModel : INotifyPropertyChanged
    {
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

        public PlayerViewModel(IAudioPlayerService playerService)
        {
            _playerService = playerService;
            
            // Check if LibVLC initialized successfully
            IsPlayerInitialized = _playerService.IsInitialized;
            if (!IsPlayerInitialized)
            {
                // Set diagnostic message if initialization failed
                TrackTitle = "Player Initialization Failed";
                TrackArtist = "Check LibVLC files in output directory";
                System.Diagnostics.Debug.WriteLine("[PlayerViewModel] WARNING: AudioPlayerService failed to initialize. LibVLC native libraries may be missing.");
            }
            
            _playerService.PausableChanged += (s, e) => IsPlaying = _playerService.IsPlaying;
            _playerService.EndReached += OnEndReached;
            
            _playerService.PositionChanged += (s, pos) => Position = pos;
            
            _playerService.TimeChanged += (s, timeMs) => CurrentTimeStr = TimeSpan.FromMilliseconds(timeMs).ToString(@"m\:ss");
            
            _playerService.LengthChanged += (s, lenMs) => TotalTimeStr = TimeSpan.FromMilliseconds(lenMs).ToString(@"m\:ss");

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
            
            if (!string.IsNullOrEmpty(filePath))
            {
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

        private void TogglePlayPause()
        {
            if (IsPlaying)
                _playerService.Pause();
            else
            {
                // If nothing loaded, maybe play current?
                // For now, Pause works as toggle if media is loaded.
                _playerService.Pause(); // LibVLC Pause toggles.
            }
            IsPlaying = _playerService.IsPlaying;
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
            TrackTitle = title;
            TrackArtist = artist;
            _playerService.Play(filePath);
            IsPlaying = true;
        }
    }
}
