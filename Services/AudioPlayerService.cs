using System;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Timers;

namespace SLSKDONET.Services
{
    public class AudioPlayerService : IAudioPlayerService, IDisposable
    {
        private IWavePlayer? _outputDevice;
        private AudioFileReader? _audioFile;
        // private WdlResamplingSampleProvider? _resampler;
        private SampleChannel? _sampleChannel;
        private MeteringSampleProvider? _meteringProvider;
        private bool _isInitialized;
        private System.Timers.Timer? _timer;

        public event EventHandler<long>? TimeChanged;
        public event EventHandler<float>? PositionChanged;
        public event EventHandler<long>? LengthChanged;
        public event EventHandler<AudioLevelsEventArgs>? AudioLevelsChanged;
        public event EventHandler? EndReached;
        public event EventHandler? PausableChanged;

        private double _pitch = 1.0;
        public double Pitch 
        { 
            get => _pitch; 
            set 
            {
                _pitch = value;
                // resampler pitch adjustment placeholder
            }
        }

        public AudioPlayerService()
        {
            _isInitialized = true;
            _timer = new System.Timers.Timer(50);
            _timer.Elapsed += OnTimerElapsed;
            _timer.Start();
        }


        private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            if (_audioFile != null && _outputDevice?.PlaybackState == PlaybackState.Playing)
            {
                TimeChanged?.Invoke(this, (long)_audioFile.CurrentTime.TotalMilliseconds);
                PositionChanged?.Invoke(this, (float)(_audioFile.Position / (double)_audioFile.Length));
            }
        }

        public bool IsInitialized => _isInitialized;
        public bool IsPlaying => _outputDevice?.PlaybackState == PlaybackState.Playing;
        public long Length => (long)(_audioFile?.TotalTime.TotalMilliseconds ?? 0);
        public long Time => (long)(_audioFile?.CurrentTime.TotalMilliseconds ?? 0);

        public float Position
        {
            get => (float)(_audioFile != null ? _audioFile.Position / (double)_audioFile.Length : 0);
            set
            {
                if (_audioFile != null)
                {
                    _audioFile.Position = (long)(value * _audioFile.Length);
                }
            }
        }

        public int Volume
        {
            get => (int)((_outputDevice?.Volume ?? 1f) * 100);
            set { if (_outputDevice != null) _outputDevice.Volume = value / 100f; }
        }

        public void Play(string filePath)
        {
            Stop();

            try
            {
                _audioFile = new AudioFileReader(filePath);
                
                // Set up channel and resampler for pitch (turntable style)
                _sampleChannel = new SampleChannel(_audioFile, true);
                
                // For true pitch sliding, we'd need a custom ResamplingProvider.
                // For now, we use the standard pipeline with Metering.
                _meteringProvider = new MeteringSampleProvider(_sampleChannel);
                _meteringProvider.StreamVolume += OnStreamVolume;
                
                _outputDevice = new WaveOutEvent { DesiredLatency = 100 };
                _outputDevice.Init(_meteringProvider);
                _outputDevice.PlaybackStopped += (s, e) => EndReached?.Invoke(this, EventArgs.Empty);
                
                _outputDevice.Play();
                LengthChanged?.Invoke(this, (long)_audioFile.TotalTime.TotalMilliseconds);
                PausableChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioPlayerService] Playback error: {ex.Message}");
                throw;
            }
        }

        private void OnStreamVolume(object? sender, StreamVolumeEventArgs e)
        {
            AudioLevelsChanged?.Invoke(this, new AudioLevelsEventArgs 
            { 
                Left = e.MaxSampleValues[0], 
                Right = e.MaxSampleValues.Length > 1 ? e.MaxSampleValues[1] : e.MaxSampleValues[0] 
            });
        }

        public void Pause()
        {
            if (_outputDevice?.PlaybackState == PlaybackState.Playing)
                _outputDevice.Pause();
            else if (_outputDevice?.PlaybackState == PlaybackState.Paused)
                _outputDevice.Play();
                
            PausableChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Stop()
        {
            _outputDevice?.Stop();
            _audioFile?.Dispose();
            _outputDevice?.Dispose();
            _audioFile = null;
            _outputDevice = null;
            PausableChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            Stop();
            _timer?.Stop();
            _timer?.Dispose();
        }
    }
}
