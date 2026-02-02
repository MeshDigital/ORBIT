using System;
using System.Collections.ObjectModel;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services.Audio;

namespace SLSKDONET.ViewModels.Timeline;

/// <summary>
/// State machine for track lifecycle in the DAW timeline.
/// Tracks transition from Ghost (Spotify link) to Ready (analyzed local file).
/// </summary>
public enum TrackState
{
    /// <summary>Track exists only as metadata (Spotify/ISRC). No local file.</summary>
    Ghost,
    
    /// <summary>Track is being downloaded from Soulseek.</summary>
    Downloading,
    
    /// <summary>Track is downloaded but being analyzed by AI Lab.</summary>
    Enrichment,
    
    /// <summary>Track is fully analyzed and ready for timeline placement.</summary>
    Ready
}

/// <summary>
/// ViewModel for a single track lane in the Set Designer timeline.
/// Manages the track's lifecycle state, waveform data, and Smart Cues.
/// </summary>
public class TrackLaneViewModel : ReactiveObject, IDisposable
{
    private TrackState _state = TrackState.Ghost;
    private string _localFilePath = "";
    private float[]? _waveformPeaks;
    private double _volume = 1.0;
    private bool _isMuted;
    private bool _isSolo;
    private bool _isExpanded;
    private long _startSampleOffset;
    private long _endSample = long.MaxValue;
    private double _downloadProgress;

    // Cloud identity (before local file exists)
    public string SpotifyId { get; set; } = "";
    public string ISRC { get; set; } = "";
    public string MBID { get; set; } = "";
    
    // Track metadata
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public double BPM { get; set; }
    public string MusicalKey { get; set; } = "";
    public double Energy { get; set; }
    
    /// <summary>
    /// Current lifecycle state of the track.
    /// </summary>
    public TrackState State
    {
        get => _state;
        set => this.RaiseAndSetIfChanged(ref _state, value);
    }
    
    /// <summary>
    /// Path to the local audio file (empty if Ghost state).
    /// </summary>
    public string LocalFilePath
    {
        get => _localFilePath;
        set => this.RaiseAndSetIfChanged(ref _localFilePath, value);
    }
    
    /// <summary>
    /// GPU-ready waveform peak data for rendering.
    /// </summary>
    public float[]? WaveformPeaks
    {
        get => _waveformPeaks;
        set => this.RaiseAndSetIfChanged(ref _waveformPeaks, value);
    }
    
    /// <summary>
    /// Volume level (0.0 to 1.0+).
    /// </summary>
    public double Volume
    {
        get => _volume;
        set => this.RaiseAndSetIfChanged(ref _volume, value);
    }
    
    /// <summary>
    /// Whether this lane is muted.
    /// </summary>
    public bool IsMuted
    {
        get => _isMuted;
        set => this.RaiseAndSetIfChanged(ref _isMuted, value);
    }
    
    /// <summary>
    /// Whether this lane is soloed (only this lane plays).
    /// </summary>
    public bool IsSolo
    {
        get => _isSolo;
        set => this.RaiseAndSetIfChanged(ref _isSolo, value);
    }
    
    /// <summary>
    /// Whether stem sub-lanes are expanded.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }
    
    /// <summary>
    /// Where this clip starts on the timeline (in samples).
    /// </summary>
    public long StartSampleOffset
    {
        get => _startSampleOffset;
        set => this.RaiseAndSetIfChanged(ref _startSampleOffset, value);
    }
    
    /// <summary>
    /// Clip end boundary (in samples).
    /// </summary>
    public long EndSample
    {
        get => _endSample;
        set 
        {
            this.RaiseAndSetIfChanged(ref _endSample, value);
            this.RaisePropertyChanged(nameof(DurationSamples));
        }
    }

    /// <summary>
    /// Calculated duration of the clip in samples.
    /// </summary>
    public long DurationSamples => EndSample == long.MaxValue ? 0 : (EndSample - StartSampleOffset);
    
    /// <summary>
    /// Download progress (0.0 to 1.0) when in Downloading state.
    /// </summary>
    public double DownloadProgress
    {
        get => _downloadProgress;
        set => this.RaiseAndSetIfChanged(ref _downloadProgress, value);
    }
    
    /// <summary>
    /// AI-detected cue points for this track.
    /// </summary>
    public ObservableCollection<SmartCue> SmartCues { get; } = new();
    
    /// <summary>
    /// Stem sub-lanes (visible when IsExpanded is true).
    /// </summary>
    public ObservableCollection<StemSubLane> StemLanes { get; } = new();

    /// <summary>
    /// Called when the file has been downloaded.
    /// Transitions state from Ghost/Downloading to Enrichment.
    /// </summary>
    public void OnFileDownloaded(string filePath)
    {
        LocalFilePath = filePath;
        State = TrackState.Enrichment;
        DownloadProgress = 1.0;
    }
    
    /// <summary>
    /// Called when AI analysis is complete.
    /// Transitions state to Ready and populates SmartCues.
    /// </summary>
    public void OnAnalysisComplete(float[]? waveform, System.Collections.Generic.List<SmartCue>? cues)
    {
        WaveformPeaks = waveform;
        
        if (cues != null)
        {
            SmartCues.Clear();
            foreach (var cue in cues)
            {
                SmartCues.Add(cue);
            }
        }
        
        State = TrackState.Ready;
    }
    
    /// <summary>
    /// Creates a TrackLaneSampler for use with MultiTrackEngine.
    /// </summary>
    public TrackLaneSampler CreateSampler()
    {
        var sampler = new TrackLaneSampler
        {
            TrackId = SpotifyId ?? ISRC ?? MBID ?? Guid.NewGuid().ToString(),
            TrackTitle = $"{Artist} - {Title}",
            IsActive = State == TrackState.Ready,
            IsMuted = IsMuted,
            Volume = (float)Volume,
            StartSampleOffset = StartSampleOffset,
            EndSample = EndSample
        };
        
        if (!string.IsNullOrEmpty(LocalFilePath) && System.IO.File.Exists(LocalFilePath))
        {
            sampler.LoadFile(LocalFilePath);
        }
        
        return sampler;
    }

    public void Dispose()
    {
        SmartCues.Clear();
        StemLanes.Clear();
    }
}

/// <summary>
/// Represents a Smart Cue point detected by AI.
/// </summary>
public class SmartCue
{
    public double TimestampSeconds { get; set; }
    public long SamplePosition { get; set; }
    public string Label { get; set; } = "";
    public CueType Type { get; set; }
    public string Color { get; set; } = "#FFFFFF";
    public float Confidence { get; set; } = 1.0f;
}

/// <summary>
/// Types of AI-detected cue points.
/// </summary>
public enum CueType
{
    Start,
    Intro,
    Build,
    Drop,
    Break,
    Outro,
    Custom
}

/// <summary>
/// Represents a stem sub-lane (Vocals, Drums, Bass, Other).
/// </summary>
public class StemSubLane : ReactiveObject
{
    private double _volume = 1.0;
    private bool _isMuted;
    
    public string StemType { get; set; } = "";
    public string Color { get; set; } = "#FFFFFF";
    public float[]? WaveformPeaks { get; set; }
    
    public double Volume
    {
        get => _volume;
        set => this.RaiseAndSetIfChanged(ref _volume, value);
    }
    
    public bool IsMuted
    {
        get => _isMuted;
        set => this.RaiseAndSetIfChanged(ref _isMuted, value);
    }
}
