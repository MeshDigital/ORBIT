using System;
using System.Windows.Input;
using ReactiveUI;

namespace SLSKDONET.ViewModels.Studio;

public class PianoKeyViewModel : ReactiveObject
{
    private bool _isInScale;
    private bool _isPressed;

    public string NoteName { get; }
    public float Frequency { get; }
    public bool IsBlackKey { get; }

    public bool IsInScale
    {
        get => _isInScale;
        set => this.RaiseAndSetIfChanged(ref _isInScale, value);
    }

    public bool IsPressed
    {
        get => _isPressed;
        set => this.RaiseAndSetIfChanged(ref _isPressed, value);
    }

    public ICommand PlayNoteCommand { get; }

    public PianoKeyViewModel(string noteName, float frequency, bool isBlackKey, Action<PianoKeyViewModel> onPlay)
    {
        NoteName = noteName;
        Frequency = frequency;
        IsBlackKey = isBlackKey;
        PlayNoteCommand = ReactiveCommand.Create(() => onPlay(this));
    }
}
