using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SLSKDONET.Models.Stem;

namespace SLSKDONET.Services.Audio;

/// <summary>
/// A specialized audio engine that loads, processes, and mixes multiple audio stems simultaneously.
/// Uses NAudio for playback and mixing.
/// </summary>
public class RealTimeStemEngine : IDisposable
{
    private readonly Dictionary<StemType, StemProcessingChain> _processors = new();
    private IWavePlayer? _outputDevice;
    private MixingSampleProvider? _mixer;
    
    // Global lock for thread safety
    private readonly object _lock = new();

    public RealTimeStemEngine()
    {
        // _processors are initialized lazily or on LoadStems to ensure fresh state
    }

    public void LoadStems(Dictionary<StemType, string> stemFilePaths)
    {
        lock (_lock)
        {
            StopAndDispose();

            try 
            {
                _processors.Clear();
                var sources = new List<ISampleProvider>();

                // 1. Create sources for each file
                foreach (var input in stemFilePaths)
                {
                    // Robust checking if file exists
                    if (!System.IO.File.Exists(input.Value)) continue;

                    var reader = new AudioFileReader(input.Value);
                    
                    // Create Chain: Source -> Volume -> Pan
                    var volumeProvider = new VolumeSampleProvider(reader) { Volume = 1.0f };
                    // Note: NAudio's PanningSampleProvider is mono-to-stereo or stereo-balance. 
                    // AudioFileReader is usually stereo. Use PanningSampleProvider for stereo balance.
                    var panProvider = new PanningSampleProvider(volumeProvider) { Pan = 0.0f };

                    var chain = new StemProcessingChain(input.Key)
                    {
                        Reader = reader,
                        VolumeProvider = volumeProvider,
                        PanProvider = panProvider,
                        FinalProvider = panProvider
                    };
                    
                    _processors[input.Key] = chain;
                    sources.Add(panProvider);
                }

                if (sources.Count == 0) return;

                // 2. Mix them
                // We assume all stems have same format (usually 44.1kHz stereo). 
                // Ideally we'd check/convert, but typically separated stems match.
                _mixer = new MixingSampleProvider(sources);

                // 3. Initialize Output
                _outputDevice = new WaveOutEvent(); // or WasapiOut
                _outputDevice.Init(_mixer);
                
                Console.WriteLine($"[RealTimeStemEngine] Loaded {sources.Count} stems.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RealTimeStemEngine] Error loading stems: {ex.Message}");
                StopAndDispose();
            }
        }
    }

    public void Play()
    {
        lock (_lock)
        {
            if (_outputDevice != null && _outputDevice.PlaybackState != PlaybackState.Playing)
            {
                _outputDevice.Play();
            }
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_outputDevice != null && _outputDevice.PlaybackState == PlaybackState.Playing)
            {
                _outputDevice.Pause();
            }
        }
    }

    public void SetVolume(StemType type, float volume)
    {
        lock (_lock)
        {
            if (_processors.TryGetValue(type, out var chain))
            {
                chain.UserVolume = volume;
                UpdateEffectiveVolume(chain);
            }
        }
    }

    public void SetMute(StemType type, bool isMuted)
    {
        lock (_lock)
        {
            if (_processors.TryGetValue(type, out var chain))
            {
                chain.IsMuted = isMuted;
                UpdateEffectiveVolumes();
            }
        }
    }

    public void SetSolo(StemType type, bool isSolo)
    {
        lock (_lock)
        {
            if (_processors.TryGetValue(type, out var chain))
            {
                chain.IsSolo = isSolo;
                UpdateEffectiveVolumes();
            }
        }
    }
    
    public void SetPan(StemType type, float pan)
    {
        lock (_lock)
        {
             if (_processors.TryGetValue(type, out var chain))
             {
                 if (chain.PanProvider != null) chain.PanProvider.Pan = Math.Clamp(pan, -1.0f, 1.0f);
             }
        }
    }
    
    public TimeSpan CurrentTime
    {
        get
        {
            lock (_lock)
            {
                // Return position of the first active reader
                foreach (var chain in _processors.Values)
                {
                    if (chain.Reader != null) return chain.Reader.CurrentTime;
                }
                return TimeSpan.Zero;
            }
        }
    }

    public TimeSpan TotalTime
    {
        get
        {
            lock (_lock)
            {
                foreach (var chain in _processors.Values)
                {
                    if (chain.Reader != null) return chain.Reader.TotalTime;
                }
                return TimeSpan.Zero;
            }
        }
    }

    public void Seek(double seconds)
    {
         lock (_lock)
         {
             var time = TimeSpan.FromSeconds(seconds);
             foreach(var chain in _processors.Values)
             {
                 if (chain.Reader != null)
                 {
                     // Clamp to total time to prevent exceptions
                     if (time > chain.Reader.TotalTime) time = chain.Reader.TotalTime;
                     if (time < TimeSpan.Zero) time = TimeSpan.Zero;
                     
                     chain.Reader.CurrentTime = time;
                 }
             }
         }
    }

    private void UpdateEffectiveVolumes()
    {
        bool anySolo = _processors.Values.Any(p => p.IsSolo);

        foreach (var chain in _processors.Values)
        {
            if (chain.VolumeProvider == null) continue;

            float targetVol = chain.UserVolume;

            if (chain.IsMuted)
            {
                targetVol = 0.0f;
            }
            else if (anySolo && !chain.IsSolo)
            {
                targetVol = 0.0f; // Muted by other Solos
            }

            chain.VolumeProvider.Volume = targetVol;
        }
    }
    
    private void UpdateEffectiveVolume(StemProcessingChain chain)
    {
         // Optimized single update if Solos didn't change (simplification)
         // But for correctness with Solo, we should check global state.
         UpdateEffectiveVolumes();
    }

    private void StopAndDispose()
    {
        _outputDevice?.Stop();
        _outputDevice?.Dispose();
        _outputDevice = null;
        
        foreach(var chain in _processors.Values)
        {
            chain.Reader?.Dispose();
        }
        _processors.Clear();
        _mixer = null;
    }

    public void Dispose()
    {
        StopAndDispose();
    }
}

public class StemProcessingChain
{
    public StemType Type { get; }
    
    // NAudio components
    public AudioFileReader? Reader { get; set; }
    public VolumeSampleProvider? VolumeProvider { get; set; }
    public PanningSampleProvider? PanProvider { get; set; }
    public ISampleProvider? FinalProvider { get; set; }

    // State
    public float UserVolume { get; set; } = 1.0f;
    public bool IsMuted { get; set; }
    public bool IsSolo { get; set; }
    
    public StemProcessingChain(StemType type)
    {
        Type = type;
    }
}
