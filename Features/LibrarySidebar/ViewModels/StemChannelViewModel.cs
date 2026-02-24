using System;
using System.Reactive.Linq;
using ReactiveUI;
using SLSKDONET.Models.Stem;

namespace SLSKDONET.Features.LibrarySidebar.ViewModels;

/// <summary>
/// Represents a single audio channel (e.g., Vocals) in the Stem Mixer.
/// Implements the logic matrix for Mute/Solo calculations.
/// </summary>
public class StemChannelViewModel : ReactiveObject
{
    private readonly StemSidebarViewModel _parent;
    private double _volume = 1.0;
    private bool _isMuted;
    private bool _isSoloed;

    public StemType Type { get; }
    public string Name { get; }
    public string Color { get; }

    public StemChannelViewModel(StemSidebarViewModel parent, StemType type, string name, string color)
    {
        _parent = parent;
        Type = type;
        Name = name;
        Color = color;

        // Recalculate EffectiveVolume whenever Volume, Mute, or Solo changes
        // Also listen to parent's Solo count
        _effectiveVolume = Observable.CombineLatest(
            this.WhenAnyValue(x => x.Volume, x => x.IsMuted, x => x.IsSoloed),
            _parent.WhenAnyValue(p => p.AnySoloActive),
            (vals, anyS) => CalculateEffectiveVolume(anyS))
            .ToProperty(this, x => x.EffectiveVolume);
    }

    public double Volume
    {
        get => _volume;
        set => this.RaiseAndSetIfChanged(ref _volume, value);
    }

    public bool IsMuted
    {
        get => _isMuted;
        set => this.RaiseAndSetIfChanged(ref _isMuted, value);
    }

    public bool IsSoloed
    {
        get => _isSoloed;
        set 
        {
            if (this.RaiseAndSetIfChanged(ref _isSoloed, value))
            {
                _parent.UpdateSoloStates();
            }
        }
    }

    private readonly ObservableAsPropertyHelper<double> _effectiveVolume;
    
    /// <summary>
    /// The final volume value passed to the audio engine.
    /// Logic: If Muted -> 0. If ANY channel is Soloed and THIS is not -> 0. Otherwise -> Volume.
    /// </summary>
    public double EffectiveVolume => _effectiveVolume.Value;

    private double CalculateEffectiveVolume(bool anySoloActive)
    {
        if (IsMuted) return 0;
        if (anySoloActive && !IsSoloed) return 0;
        return Volume;
    }

    public void ToggleMute() => IsMuted = !IsMuted;
    public void ToggleSolo() => IsSoloed = !IsSoloed;
}
