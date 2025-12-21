using Avalonia.Data.Converters;
using Avalonia.Media;
using SLSKDONET.Models;
using System;
using System.Globalization;

namespace SLSKDONET.Views.Avalonia.Converters;

/// <summary>
/// Converts PlaylistTrackState to a colored brush for status badges.
/// </summary>
public class TrackStateToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not PlaylistTrackState state)
            return new SolidColorBrush(Color.Parse("#666666")); // Default gray

        return state switch
        {
            PlaylistTrackState.Completed => new SolidColorBrush(Color.Parse("#1DB954")), // Spotify green
            PlaylistTrackState.Downloading => new SolidColorBrush(Color.Parse("#00A3FF")), // Orbit blue
            PlaylistTrackState.Searching => new SolidColorBrush(Color.Parse("#B388FF")), // Purple
            PlaylistTrackState.Queued => new SolidColorBrush(Color.Parse("#FFA726")), // Orange
            PlaylistTrackState.Failed => new SolidColorBrush(Color.Parse("#F44336")), // Red
            PlaylistTrackState.Pending => new SolidColorBrush(Color.Parse("#757575")), // Gray
            PlaylistTrackState.Cancelled => new SolidColorBrush(Color.Parse("#9E9E9E")), // Light gray
            _ => new SolidColorBrush(Color.Parse("#666666"))
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts PlaylistTrackState to display text.
/// </summary>
public class TrackStateToTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not PlaylistTrackState state)
            return "Unknown";

        return state switch
        {
            PlaylistTrackState.Completed => "✓ Ready",
            PlaylistTrackState.Downloading => "↓ Downloading",
            PlaylistTrackState.Searching => "🔍 Searching",
            PlaylistTrackState.Queued => "⏳ Queued",
            PlaylistTrackState.Failed => "✗ Failed",
            PlaylistTrackState.Pending => "⊙ Missing",
            PlaylistTrackState.Cancelled => "⊘ Cancelled",
            _ => "?"
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts PlaylistTrackState to a boolean indicating if the track is "missing" (not downloaded).
/// </summary>
public class TrackStateToIsMissingConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not PlaylistTrackState state)
            return false;

        return state == PlaylistTrackState.Pending || state == PlaylistTrackState.Failed;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// Converts PlaylistTrackState to a boolean indicating if the track is actively downloading.
/// </summary>
public class TrackStateToIsDownloadingConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not PlaylistTrackState state)
            return false;

        return state == PlaylistTrackState.Downloading || state == PlaylistTrackState.Searching || state == PlaylistTrackState.Queued;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
