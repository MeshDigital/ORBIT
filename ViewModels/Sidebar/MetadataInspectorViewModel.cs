using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SLSKDONET.ViewModels.Library;

namespace SLSKDONET.ViewModels.Sidebar;

/// <summary>
/// Lightweight read-only metadata card for the sidebar.
/// A focused subset of TrackInspectorViewModel — no analysis, no cues, no commands.
/// </summary>
public class MetadataInspectorViewModel : INotifyPropertyChanged
{
    private string _title = string.Empty;
    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    private string _artist = string.Empty;
    public string Artist
    {
        get => _artist;
        private set => SetProperty(ref _artist, value);
    }

    private string _album = string.Empty;
    public string Album
    {
        get => _album;
        private set => SetProperty(ref _album, value);
    }

    private string _bpmDisplay = string.Empty;
    public string BpmDisplay
    {
        get => _bpmDisplay;
        private set => SetProperty(ref _bpmDisplay, value);
    }

    private string _keyDisplay = string.Empty;
    public string KeyDisplay
    {
        get => _keyDisplay;
        private set => SetProperty(ref _keyDisplay, value);
    }

    private string _genres = string.Empty;
    public string Genres
    {
        get => _genres;
        private set => SetProperty(ref _genres, value);
    }

    private string _isrc = string.Empty;
    public string Isrc
    {
        get => _isrc;
        private set => SetProperty(ref _isrc, value);
    }

    private string _label = string.Empty;
    public string Label
    {
        get => _label;
        private set => SetProperty(ref _label, value);
    }

    private string _durationFormatted = string.Empty;
    public string DurationFormatted
    {
        get => _durationFormatted;
        private set => SetProperty(ref _durationFormatted, value);
    }

    private string _bitrateFormatted = string.Empty;
    public string BitrateFormatted
    {
        get => _bitrateFormatted;
        private set => SetProperty(ref _bitrateFormatted, value);
    }

    private double _energy;
    public double Energy
    {
        get => _energy;
        private set => SetProperty(ref _energy, value);
    }

    private double _danceability;
    public double Danceability
    {
        get => _danceability;
        private set => SetProperty(ref _danceability, value);
    }

    private double _valence;
    public double Valence
    {
        get => _valence;
        private set => SetProperty(ref _valence, value);
    }

    private string? _albumArtUrl;
    public string? AlbumArtUrl
    {
        get => _albumArtUrl;
        private set => SetProperty(ref _albumArtUrl, value);
    }

    private bool _hasTrack;
    public bool HasTrack
    {
        get => _hasTrack;
        private set => SetProperty(ref _hasTrack, value);
    }

    public void Load(PlaylistTrackViewModel track)
    {
        if (track?.Model == null)
        {
            HasTrack = false;
            return;
        }

        var m = track.Model;
        Title = m.Title ?? string.Empty;
        Artist = m.Artist ?? string.Empty;
        Album = m.Album ?? string.Empty;
        BpmDisplay = m.BPM.HasValue && m.BPM > 0 ? $"{m.BPM.Value:F1}" : "—";
        KeyDisplay = track.KeyDisplay ?? string.Empty;
        Genres = m.Genres ?? string.Empty;
        Isrc = m.ISRC ?? string.Empty;
        Label = m.Label ?? string.Empty;
        DurationFormatted = track.DurationFormatted ?? string.Empty;
        BitrateFormatted = track.BitrateFormatted ?? string.Empty;
        Energy = (m.Energy ?? 0) * 100.0;
        Danceability = (m.Danceability ?? 0) * 100.0;
        Valence = (m.Valence ?? 0) * 100.0;
        AlbumArtUrl = m.AlbumArtUrl;
        HasTrack = true;
    }

    // ─── INotifyPropertyChanged ────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
