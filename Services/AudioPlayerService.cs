using System;
using System.IO;
using LibVLCSharp.Shared;

namespace SLSKDONET.Services
{
    public class AudioPlayerService : IAudioPlayerService
    {
        private LibVLC? _libVLC;
        private MediaPlayer? _mediaPlayer;
        private bool _isInitialized;

        public event EventHandler<long>? TimeChanged;
        public event EventHandler<float>? PositionChanged;
        public event EventHandler<long>? LengthChanged;
        public event EventHandler? EndReached;
        public event EventHandler? PausableChanged;

        public AudioPlayerService()
        {
            // Defer initialization until first use to avoid blocking app startup
            // Initialize() will be called lazily when Play() is first invoked
        }

        private void Initialize()
    {
        if (_isInitialized) return;

        try 
        {
            // Explicitly set LibVLC path to the output directory
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var libVlcPath = Path.Combine(appDir, "libvlc", Environment.Is64BitProcess ? "win-x64" : "win-x86");
            
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [AudioPlayerService] Initializing LibVLC from: {libVlcPath}");
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [AudioPlayerService] LibVLC path exists: {Directory.Exists(libVlcPath)}");
            
            if (!Directory.Exists(libVlcPath))
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [AudioPlayerService] ERROR: LibVLC directory not found!");
                return;
            }
            
            // Initialize with explicit path
            Core.Initialize(libVlcPath);
            
            _libVLC = new LibVLC();
            _mediaPlayer = new MediaPlayer(_libVLC);

            _mediaPlayer.TimeChanged += (s, e) => TimeChanged?.Invoke(this, e.Time);
            _mediaPlayer.PositionChanged += (s, e) => PositionChanged?.Invoke(this, e.Position);
            _mediaPlayer.LengthChanged += (s, e) => LengthChanged?.Invoke(this, e.Length);
            _mediaPlayer.EndReached += (s, e) => EndReached?.Invoke(this, EventArgs.Empty);
            _mediaPlayer.PausableChanged += (s, e) => PausableChanged?.Invoke(this, EventArgs.Empty);

            _isInitialized = true;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [AudioPlayerService] Initialization successful!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AudioPlayerService] Initialization Failed: {ex.Message}");
            Console.WriteLine($"[AudioPlayerService] Stack trace: {ex.StackTrace}");
        }
    }
        

        public bool IsInitialized => _isInitialized;
        
        public bool IsPlaying => _mediaPlayer?.IsPlaying ?? false;
        
        public long Length => _mediaPlayer?.Length ?? 0;
        
        public long Time => _mediaPlayer?.Time ?? 0;

        public float Position
        {
            get => _mediaPlayer?.Position ?? 0f;
            set
            {
                if (_mediaPlayer != null) 
                    _mediaPlayer.Position = value;
            }
        }

        public int Volume
        {
            get => _mediaPlayer?.Volume ?? 100;
            set
            {
                if (_mediaPlayer != null) 
                    _mediaPlayer.Volume = value;
            }
        }

        public void Play(string filePath)
        {
            if (!_isInitialized || _libVLC == null || _mediaPlayer == null) return;

            // Ensure we have a clean file path (not URL-encoded)
            // If it's a URI, decode it first
            string cleanPath = filePath;
            if (filePath.StartsWith("file:///"))
            {
                cleanPath = Uri.UnescapeDataString(new Uri(filePath).LocalPath);
            }
            else if (filePath.Contains("%20") || filePath.Contains("%"))
            {
                cleanPath = Uri.UnescapeDataString(filePath);
            }

            // Verify file exists
            if (!File.Exists(cleanPath))
            {
                Console.WriteLine($"[AudioPlayerService] File not found: {cleanPath}");
                return;
            }

            // Stop current if playing
            if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Stop();
            }
            
            // CRITICAL: Pass the RAW Windows path directly
            // LibVLC handles Windows paths correctly without URI conversion
            Console.WriteLine($"[AudioPlayerService] Creating media from path: {cleanPath}");
            
            // Create media from raw path
            var media = new Media(_libVLC, cleanPath);
            media.Parse(MediaParseOptions.ParseLocal);
            
            _mediaPlayer.Play(media);
            
            Console.WriteLine($"[AudioPlayerService] Playback started for: {Path.GetFileName(cleanPath)}");
        }

        public void Pause()
        {
            _mediaPlayer?.Pause();
        }

        public void Stop()
        {
            _mediaPlayer?.Stop();
        }

        public void Dispose()
        {
            _mediaPlayer?.Dispose();
            _libVLC?.Dispose();
        }
    }
}
