using System;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SLSKDONET.Services.Missions;

/// <summary>
/// Universal interface for the ORBIT Mission framework.
/// Missions are automated, multi-step workflows that provide real-time feedback.
/// </summary>
public interface IMission : INotifyPropertyChanged
{
    string Name { get; }
    string Description { get; }
    string Icon { get; } // Emoji or MDL2 icon name
    
    bool IsRunning { get; }
    double Progress { get; } // 0.0 to 1.0
    string StatusText { get; }
    
    Task ExecuteAsync();
    void Cancel();
}

public abstract class MissionBase : IMission
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract string Icon { get; }

    private bool _isRunning;
    public bool IsRunning 
    { 
        get => _isRunning; 
        protected set => SetField(ref _isRunning, value); 
    }

    private double _progress;
    public double Progress 
    { 
        get => _progress; 
        protected set => SetField(ref _progress, value); 
    }

    private string _statusText = "Ready";
    public string StatusText 
    { 
        get => _statusText; 
        protected set => SetField(ref _statusText, value); 
    }

    public abstract Task ExecuteAsync();
    public virtual void Cancel() { }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
