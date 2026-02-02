using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using DynamicData;
using ReactiveUI;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services;
using SLSKDONET.Models;
using System.Timers;
using System.Reactive.Disposables;

namespace SLSKDONET.ViewModels.Timeline;

/// <summary>
/// Root ViewModel for the DAW Set Designer.
/// Orchestrates the MultiTrackEngine, TransitionEngine, and individual TrackLanes.
/// </summary>
public class SetDesignerViewModel : ReactiveObject, IDisposable
{
    private readonly MultiTrackEngine _engine;
    private readonly TransitionEngine _transitionEngine;
    private readonly IEventBus _eventBus;
    private readonly MasterBus _masterBus;
    private readonly System.Timers.Timer _uiTimer;
    private readonly CompositeDisposable _disposables = new();
    
    // === Observable Collections ===
    public ObservableCollection<TrackLaneViewModel> Lanes { get; } = new();
    
    // === Properties ===
    
    private double _zoomLevel = 1.0;
    public double ZoomLevel
    {
        get => _zoomLevel;
        set => this.RaiseAndSetIfChanged(ref _zoomLevel, value);
    }
    
    private double _scrollOffset = 0;
    public double ScrollOffset
    {
        get => _scrollOffset;
        set => this.RaiseAndSetIfChanged(ref _scrollOffset, value);
    }
    
    private long _currentPosition;
    public long CurrentPosition
    {
        get => _currentPosition;
        set => this.RaiseAndSetIfChanged(ref _currentPosition, value);
    }
    
    private long _totalDurationSamples = 44100 * 60 * 10; // 10 minutes default
    public long TotalDurationSamples
    {
        get => _totalDurationSamples;
        set => this.RaiseAndSetIfChanged(ref _totalDurationSamples, value);
    }
    
    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        set => this.RaiseAndSetIfChanged(ref _isPlaying, value);
    }
    
    private double _masterVolume = 1.0;
    public double MasterVolume
    {
        get => _masterVolume;
        set 
        {
            this.RaiseAndSetIfChanged(ref _masterVolume, value);
            _masterBus.OutputGainDb = (float)(20 * Math.Log10(value));
        }
    }

    // === Commands ===
    public ReactiveCommand<Unit, Unit> PlayCommand { get; }
    public ReactiveCommand<Unit, Unit> PauseCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand { get; }
    public ReactiveCommand<long, Unit> SeekCommand { get; }
    public ReactiveCommand<string, Unit> AddTrackCommand { get; }

    public SetDesignerViewModel(
        MultiTrackEngine engine,
        TransitionEngine transitionEngine,
        IEventBus eventBus)
    {
        _engine = engine;
        _transitionEngine = transitionEngine;
        _eventBus = eventBus;
        
        // Wrap engine in master bus for processing
        _masterBus = new MasterBus(_engine);
        
        // Initialize engine with master bus as source for the provider
        _engine.Initialize(new AudioOutputSettings(), _masterBus); 
        
        PlayCommand = ReactiveCommand.Create(() => { _engine.Play(); IsPlaying = true; });
        PauseCommand = ReactiveCommand.Create(() => { _engine.Pause(); IsPlaying = false; });
        StopCommand = ReactiveCommand.Create(() => { _engine.Stop(); IsPlaying = false; });
        SeekCommand = ReactiveCommand.Create<long>(pos => _engine.SeekToSample(pos));
        
        AddTrackCommand = ReactiveCommand.CreateFromTask<string>(AddTrackAsync);

        // Subscribe to cross-app events
        _disposables.Add(_eventBus.GetEvent<AddToTimelineRequestEvent>().Subscribe(evt => 
        {
            foreach (var track in evt.Tracks)
            {
                if (!string.IsNullOrEmpty(track.ResolvedFilePath))
                {
                    _ = AddTrackAsync(track.ResolvedFilePath);
                }
            }
        }));

        // UI update timer (30fps)
        _uiTimer = new System.Timers.Timer(33);
        _uiTimer.Elapsed += (s, e) => 
        {
            CurrentPosition = _engine.CurrentSamplePosition;
            IsPlaying = _engine.IsPlaying;
        };
        _uiTimer.Start();
    }

    /// <summary>
    /// Adds a track to the timeline at the end of the current duration or at playhead.
    /// </summary>
    public async Task AddTrackAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath)) return;

        var laneVm = new TrackLaneViewModel
        {
            Artist = "Loading...",
            Title = System.IO.Path.GetFileNameWithoutExtension(filePath),
            StartSampleOffset = CurrentPosition, // Put at playhead
            State = TrackState.Enrichment
        };

        Lanes.Add(laneVm);
        
        // In a real scenario, we'd trigger analysis here
        // For now, let's just mark it ready and load file
        laneVm.OnFileDownloaded(filePath);
        
        // Create sampler and add to engine
        var sampler = laneVm.CreateSampler();
        _engine.AddLane(sampler);
        
        // Update total duration if needed
        if (laneVm.EndSample > TotalDurationSamples)
        {
            TotalDurationSamples = (long)(laneVm.EndSample * 1.1); // Add 10% buffer
        }
    }

    public void Dispose()
    {
        _uiTimer.Stop();
        _uiTimer.Dispose();
        _disposables.Dispose();
        _engine.Dispose();
        _masterBus.Dispose();
    }
}
