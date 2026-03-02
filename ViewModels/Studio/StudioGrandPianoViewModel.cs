using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SLSKDONET.ViewModels.Studio;

public class StudioGrandPianoViewModel : ReactiveObject, IStudioModuleViewModel, IDisposable
{
    private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    private static readonly bool[] IsBlackKeyPattern = { false, true, false, true, false, false, true, false, true, false, true, false };

    private readonly WaveOutEvent _waveOut = new();
    
    public ObservableCollection<PianoKeyViewModel> Keys { get; } = new();

    public StudioGrandPianoViewModel()
    {
        InitializeKeyboard();
    }

    private void InitializeKeyboard()
    {
        // 2 Octaves from C3 (index 48 in MIDI, approx 130.81Hz) to B4
        // Start frequency for C3
        double baseFreq = 130.8128; 

        for (int i = 0; i < 24; i++)
        {
            int noteIndex = i % 12;
            string name = NoteNames[noteIndex];
            bool isBlack = IsBlackKeyPattern[noteIndex];
            
            // Frequency = base * 2^(n/12)
            float freq = (float)(baseFreq * Math.Pow(2, (double)i / 12.0));

            Keys.Add(new PianoKeyViewModel(name, freq, isBlack, PlayNote));
        }
    }

    private void PlayNote(PianoKeyViewModel key)
    {
        try
        {
            key.IsPressed = true;
            
            var signal = new SignalGenerator(44100, 1)
            {
                Gain = 0.2,
                Frequency = key.Frequency,
                Type = SignalGeneratorType.Sin
            }
            .Take(TimeSpan.FromMilliseconds(500));

            _waveOut.Stop(); // Kill previous note if still playing
            _waveOut.Init(signal);
            _waveOut.Play();

            // Reset visual state after a short delay
            Task.Delay(200).ContinueWith(_ => key.IsPressed = false);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to play piano note preview");
        }
    }

    public async Task LoadTrackContextAsync(IDisplayableTrack track, CancellationToken cancellationToken)
    {
        // Camelot Key to Scale Mapping
        // 8A = A Minor, 8B = C Major, etc.
        var scaleNotes = GetNotesForCamelotKey(track.Key);

        foreach (var key in Keys)
        {
            key.IsInScale = scaleNotes.Contains(key.NoteName);
        }

        await Task.CompletedTask;
    }

    public void ClearContext()
    {
        foreach (var key in Keys)
        {
            key.IsInScale = false;
        }
    }

    private HashSet<string> GetNotesForCamelotKey(string? camelot)
    {
        if (string.IsNullOrEmpty(camelot)) return new HashSet<string>();

        // Minor Scales (A)
        var minorScales = new Dictionary<string, string[]>
        {
            { "1A", new[] { "G#", "A#", "B", "C#", "D#", "E", "F#" } }, // Ab Minor
            { "2A", new[] { "D#", "F", "F#", "G#", "A#", "B", "C#" } },  // Eb Minor
            { "3A", new[] { "A#", "C", "C#", "D#", "F", "F#", "G#" } },  // Bb Minor
            { "4A", new[] { "F", "G", "G#", "A#", "C", "C#", "D#" } },   // F Minor
            { "5A", new[] { "C", "D", "D#", "F", "G", "G#", "A#" } },   // C Minor
            { "6A", new[] { "G", "A", "A#", "C", "D", "D#", "F" } },    // G Minor
            { "7A", new[] { "D", "E", "F", "G", "A", "A#", "C" } },     // D Minor
            { "8A", new[] { "A", "B", "C", "D", "E", "F", "G" } },      // A Minor
            { "9A", new[] { "E", "F#", "G", "A", "B", "C", "D" } },     // E Minor
            { "10A", new[] { "B", "C#", "D", "E", "F#", "G", "A" } },   // B Minor
            { "11A", new[] { "F#", "G#", "A", "B", "C#", "D", "E" } },  // F# Minor
            { "12A", new[] { "C#", "D#", "E", "F#", "G#", "A", "B" } }  // C# Minor
        };

        // Major Scales (B)
        var majorScales = new Dictionary<string, string[]>
        {
            { "1B", new[] { "B", "C#", "D#", "E", "F#", "G#", "A#" } },  // B Major
            { "2B", new[] { "F#", "G#", "A#", "B", "C#", "D#", "F" } }, // F# Major
            { "3B", new[] { "C#", "D#", "F", "F#", "G#", "A#", "C" } },  // Db Major
            { "4B", new[] { "G#", "A#", "C", "C#", "D#", "F", "G" } },   // Ab Major
            { "5B", new[] { "D#", "F", "G", "G#", "A#", "C", "D" } },    // Eb Major
            { "6B", new[] { "A#", "C", "D", "D#", "F", "G", "A" } },     // Bb Major
            { "7B", new[] { "F", "G", "A", "A#", "C", "D", "E" } },      // F Major
            { "8B", new[] { "C", "D", "E", "F", "G", "A", "B" } },       // C Major
            { "9B", new[] { "G", "A", "B", "C", "D", "E", "F#" } },     // G Major
            { "10B", new[] { "D", "E", "F#", "G", "A", "B", "C#" } },   // D Major
            { "11B", new[] { "A", "B", "C#", "D", "E", "F#", "G#" } },  // A Major
            { "12B", new[] { "E", "F#", "G#", "A", "B", "C#", "D#" } }  // E Major
        };

        if (minorScales.TryGetValue(camelot.ToUpper(), out var notes)) return new HashSet<string>(notes);
        if (majorScales.TryGetValue(camelot.ToUpper(), out var majorNotes)) return new HashSet<string>(majorNotes);

        return new HashSet<string>();
    }

    public void Dispose()
    {
        try
        {
            _waveOut.Stop();
            _waveOut.Dispose();
        }
        catch { /* Ignore disposal errors */ }
    }
}
