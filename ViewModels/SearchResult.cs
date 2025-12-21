using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels;

public class SearchResult : INotifyPropertyChanged
{
    public Track Model { get; }

    /// <summary>
    /// Phase 2.8: Constructor now uses Null Object Pattern.
    /// Never allows null tracks - uses Track.Null as fallback.
    /// </summary>
    public SearchResult(Track? track)
    {
        Model = track ?? Track.Null;
    }

    public string Artist => Model.Artist ?? "Unknown Artist";
    public string Title => Model.Title ?? "Unknown Track";
    public string Album => Model.Album ?? "Unknown Album";
    public int Bitrate => Model.Bitrate;
    public long Size => Model.Size ?? 0;
    public string Username => Model.Username ?? "Unknown";
    public bool HasFreeUploadSlot => Model.HasFreeUploadSlot;
    
    // Rank is updated on the Model by ResultSorter, we just expose it
    public double CurrentRank => Model.CurrentRank;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
