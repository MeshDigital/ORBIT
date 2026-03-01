using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using System;
using System.Collections.Generic;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SLSKDONET.Views.Avalonia.Controls;

public partial class GrandPianoControl : UserControl
{
    private WaveOutEvent? _waveOut;
    private SignalGenerator? _signalGenerator;

    public GrandPianoControl()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnKeyClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string noteString)
        {
            PlayNote(noteString);
        }
    }

    private void PlayNote(string noteString)
    {
        // Simple mapping of note string (e.g. C3, D#4) to frequency in Hz
        double freq = GetFrequency(noteString);
        if (freq <= 0) return;

        try
        {
            if (_waveOut != null)
            {
                _waveOut.Stop();
                _waveOut.Dispose();
            }

            _signalGenerator = new SignalGenerator(44100, 1)
            {
                Type = SignalGeneratorType.Sin,
                Frequency = freq,
                Gain = 0.2 // Keep it quiet
            };

            // Play for a short burst
            var takeDuration = TimeSpan.FromMilliseconds(500);
            var provider = _signalGenerator.Take(takeDuration);

            _waveOut = new WaveOutEvent();
            _waveOut.Init(provider);
            _waveOut.Play();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GrandPianoControl] Failed to play sine wave: {ex.Message}");
        }
    }

    private double GetFrequency(string note)
    {
        // Quick dictionary for 2 octaves
        var freqs = new Dictionary<string, double>
        {
            {"C3", 130.81}, {"C#3", 138.59}, {"D3", 146.83}, {"D#3", 155.56}, {"E3", 164.81}, {"F3", 174.61}, {"F#3", 185.00}, {"G3", 196.00}, {"G#3", 207.65}, {"A3", 220.00}, {"A#3", 233.08}, {"B3", 246.94},
            {"C4", 261.63}, {"C#4", 277.18}, {"D4", 293.66}, {"D#4", 311.13}, {"E4", 329.63}, {"F4", 349.23}, {"F#4", 369.99}, {"G4", 392.00}, {"G#4", 415.30}, {"A4", 440.00}, {"A#4", 466.16}, {"B4", 493.88}
        };

        if (freqs.TryGetValue(note, out double frequency))
        {
            return frequency;
        }

        return 0;
    }
}
