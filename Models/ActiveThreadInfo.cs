using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SLSKDONET.Models;

/// <summary>
/// Represents the current state of an analysis worker thread.
/// </summary>
public class ActiveThreadInfo : INotifyPropertyChanged
{
    private int _threadId;
    private string _currentTrack = string.Empty;
    private string _status = "Idle";
    private double _progress;
    private DateTime? _startTime;

    public int ThreadId
    {
        get => _threadId;
        set => SetField(ref _threadId, value);
    }

    public string CurrentTrack
    {
        get => _currentTrack;
        set => SetField(ref _currentTrack, value);
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    public DateTime? StartTime
    {
        get => _startTime;
        set => SetField(ref _startTime, value);
    }

    public string ElapsedTime
    {
        get
        {
            if (!StartTime.HasValue) return "--:--";
            var elapsed = DateTime.Now - StartTime.Value;
            return elapsed.ToString(@"mm\:ss");
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
