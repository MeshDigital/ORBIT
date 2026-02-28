using System.ComponentModel;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Universal interface for tracks displayed in the unified DAW grid (2026 Alignment).
/// Ensures consistent column binding across different data sources (Library, Search, Spotify, etc.).
/// </summary>
public interface IDisplayableTrack : INotifyPropertyChanged
{
    string GlobalId { get; }
    object? Artwork { get; } // URL, Bitmap, or Drawing
    string Title { get; }
    string Artist { get; }
    double? Bpm { get; }
    string? Key { get; }
    double? Energy { get; }
    double? DeepDNAScore { get; }
    string StatusText { get; }
    
    // Selection state for grid interaction
    bool IsSelected { get; set; }
}
