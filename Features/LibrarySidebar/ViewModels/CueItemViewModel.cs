using SLSKDONET.Models;
using ReactiveUI;

namespace SLSKDONET.Features.LibrarySidebar.ViewModels;

/// <summary>
/// ViewModel wrapper for OrbitCue data. 
/// Handles distinction between User and Ghost (Auto) cues.
/// </summary>
public class CueItemViewModel : ReactiveObject
{
    public CueItemViewModel(OrbitCue cue)
    {
        Cue = cue;
        IsGhost = cue.Source == CueSource.Auto;
        Label = string.IsNullOrEmpty(cue.Name) ? cue.Role.ToString() : cue.Name;
        Color = cue.Color;
        TimestampLabel = System.TimeSpan.FromSeconds(cue.Timestamp).ToString(@"m\:ss\.fff");
    }

    public OrbitCue Cue { get; }
    public bool IsGhost { get; }
    public string Label { get; }
    public string Color { get; }
    public string TimestampLabel { get; }
}
