using ReactiveUI;
using SLSKDONET.Models.Stem;
using SLSKDONET.Services.Audio;
using System;

namespace SLSKDONET.ViewModels.Stem;

public class StemChannelViewModel : ReactiveObject, IDisposable
{
    private readonly StemSettings _settings;
    private readonly RealTimeStemEngine? _engine;

    public StemType Type { get; }
    public string Name => Type.ToString();

    // Visual Color hint (could be properties or handled in View)
    public string ColorHex { get; }

    // Waveform visualization data
    private Models.WaveformAnalysisData? _waveformData;
    public Models.WaveformAnalysisData? WaveformData
    {
        get => _waveformData;
        set => this.RaiseAndSetIfChanged(ref _waveformData, value);
    }

    public float Volume
    {
        get => _settings.Volume;
        set
        {
            if (_settings.Volume != value)
            {
                _settings.Volume = value;
                this.RaisePropertyChanged();
                _engine?.SetVolume(Type, value);
            }
        }
    }

    public float Pan
    {
        get => _settings.Pan;
        set
        {
            if (_settings.Pan != value)
            {
                _settings.Pan = value;
                this.RaisePropertyChanged();
                // _engine?.SetPan(Type, value);
            }
        }
    }

    public bool IsMuted
    {
        get => _settings.IsMuted;
        set
        {
            if (_settings.IsMuted != value)
            {
                _settings.IsMuted = value;
                this.RaisePropertyChanged();
                _engine?.SetMute(Type, value);
            }
        }
    }

    public bool IsSolo
    {
        get => _settings.IsSolo;
        set
        {
            if (_settings.IsSolo != value)
            {
                _settings.IsSolo = value;
                this.RaisePropertyChanged();
                _engine?.SetSolo(Type, value);
            }
        }
    }

    public StemChannelViewModel(StemType type, StemSettings settings, RealTimeStemEngine? engine = null)
    {
        Type = type;
        _settings = settings;
        _engine = engine;
        
        ColorHex = GetColorForStem(type);
    }

    private string GetColorForStem(StemType type)
    {
        return type switch
        {
            StemType.Vocals => "#FF4081", // Pink/Red
            StemType.Drums => "#FFFF00",  // Yellow
            StemType.Bass => "#00B0FF",   // Blue
            StemType.Piano => "#00E676",  // Green
            StemType.Other => "#E0E0E0",  // Grey
            _ => "#FFFFFF"
        };
    }

    public void Dispose() { /* No resources to clean up currently */ }
}
