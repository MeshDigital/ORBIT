using System;
using System.Windows.Input; // For ICommand
using SLSKDONET.Services;
using SLSKDONET.Views;

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

        public ICommand TogglePlayPauseCommand { get; }
        public ICommand StopCommand { get; }

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
            _playerService.EndReached += (s, e) => IsPlaying = false;
            
            _playerService.PositionChanged += (s, pos) => Position = pos;
            
            _playerService.TimeChanged += (s, timeMs) => CurrentTimeStr = TimeSpan.FromMilliseconds(timeMs).ToString(@"m\:ss");
            
            _playerService.LengthChanged += (s, lenMs) => TotalTimeStr = TimeSpan.FromMilliseconds(lenMs).ToString(@"m\:ss");

            TogglePlayPauseCommand = new RelayCommand(TogglePlayPause);
            StopCommand = new RelayCommand(Stop);
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
