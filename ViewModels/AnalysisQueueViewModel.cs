using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using DynamicData;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels;

public class AnalysisJobViewModel : ReactiveObject
{
    private string _status = "Pending";
    private string _step = "";
    private int _progressPcent = 0;
    private string? _error;
    
    public string TrackHash { get; set; }
    public string FilePath { get; set; }
    public string FileName => System.IO.Path.GetFileName(FilePath);
    
    public string Status 
    {
        get => _status;
        set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public string Step 
    {
        get => _step;
        set => this.RaiseAndSetIfChanged(ref _step, value);
    }
    
    public int ProgressPercent
    {
        get => _progressPcent;
        set => this.RaiseAndSetIfChanged(ref _progressPcent, value);
    }
    
    public string? Error 
    {
        get => _error;
        set => this.RaiseAndSetIfChanged(ref _error, value);
    }
    
    public DateTime Timestamp { get; set; } = DateTime.Now;
    
    public string Age => (DateTime.Now - Timestamp).ToString(@"mm\:ss") + " ago";

    public AnalysisJobViewModel(string trackHash, string filePath)
    {
        TrackHash = trackHash;
        FilePath = filePath;
    }
}

public class AnalysisQueueViewModel : ReactiveObject
{
    private readonly ILogger<AnalysisQueueViewModel> _logger;
    private readonly IEventBus _eventBus;
    private readonly INavigationService _navigationService;
    private readonly LibraryViewModel _libraryViewModel;

    public ObservableCollection<AnalysisJobViewModel> ActiveJobs { get; } = new();
    public ObservableCollection<AnalysisJobViewModel> CompletedJobs { get; } = new();
    public ObservableCollection<AnalysisJobViewModel> FailedJobs { get; } = new();
    
    // We keep a history limit?
    private const int MaxHistory = 50;

    public ReactiveCommand<AnalysisJobViewModel, Unit> InspectTrackCommand { get; }

    public AnalysisQueueViewModel(
        ILogger<AnalysisQueueViewModel> logger,
        IEventBus eventBus,
        INavigationService navigationService,
        LibraryViewModel libraryViewModel)
    {
        _logger = logger;
        _eventBus = eventBus;
        _navigationService = navigationService;
        _libraryViewModel = libraryViewModel;

        InspectTrackCommand = ReactiveCommand.Create<AnalysisJobViewModel>(InspectTrack);

        // Subscriptions
        _eventBus.GetEvent<TrackAnalysisStartedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnAnalysisStarted);

        _eventBus.GetEvent<AnalysisProgressEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnAnalysisProgress);

        _eventBus.GetEvent<TrackAnalysisCompletedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnAnalysisCompleted);

        _eventBus.GetEvent<TrackAnalysisFailedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnAnalysisFailed);
    }

    private void InspectTrack(AnalysisJobViewModel job)
    {
        _navigationService.NavigateTo(PageType.Library);
        
        // Find track in current view
        // Ideally we search globally but simplified for now:
        var track = _libraryViewModel.Tracks.CurrentProjectTracks.FirstOrDefault(t => t.GlobalId == job.TrackHash)
                    ?? _libraryViewModel.Tracks.FilteredTracks.FirstOrDefault(t => t.GlobalId == job.TrackHash);
                    
        // If not found in current lists, we might need to load it or switch project? 
        // For now, only works if visible.
             
        if (track != null)
        {
             _libraryViewModel.ToggleInspectorCommand.Execute(track);
        }
    }


    private void OnAnalysisStarted(TrackAnalysisStartedEvent evt)
    {
        var job = new AnalysisJobViewModel(evt.TrackGlobalId, evt.FileName) 
        { 
            Status = "Analyzing",
            Step = "Initializing..."
        };
        ActiveJobs.Add(job);
    }

    private void OnAnalysisProgress(AnalysisProgressEvent evt)
    {
        var job = System.Linq.Enumerable.FirstOrDefault(ActiveJobs, j => j.TrackHash == evt.TrackGlobalId);
        if (job != null)
        {
            job.Step = evt.CurrentStep;
            job.ProgressPercent = evt.ProgressPercent;
        }
    }

    private void OnAnalysisCompleted(TrackAnalysisCompletedEvent evt)
    {
        var job = System.Linq.Enumerable.FirstOrDefault(ActiveJobs, j => j.TrackHash == evt.TrackGlobalId);
        if (job != null)
        {
            ActiveJobs.Remove(job);
            
            if (evt.Success)
            {
                job.Status = "Completed";
                job.Step = "Saved";
                job.ProgressPercent = 100;
                job.Timestamp = DateTime.Now;
                CompletedJobs.Insert(0, job);
                if (CompletedJobs.Count > MaxHistory) CompletedJobs.RemoveAt(CompletedJobs.Count - 1);
            }
            else
            {
                // Should fall through to Failed handler usually, but Event logic might double dip?
                // TrackAnalysisCompletedEvent has Success=false if error.
                // AnalysisQueueService publishes BOTH Completed(false) AND Failed event.
                // So handle cleanup here, insert into failed in failed handler? 
                // Or handle both.
                // Let's rely on Failed event for Failed list insertion to avoid duplication.
            }
        }
    }

    private void OnAnalysisFailed(TrackAnalysisFailedEvent evt)
    {
         // Find if it's still in Active (if Completed handler didn't run first)
         var job = System.Linq.Enumerable.FirstOrDefault(ActiveJobs, j => j.TrackHash == evt.TrackGlobalId);
         if (job != null)
         {
             ActiveJobs.Remove(job);
             job.Status = "Failed";
             job.Error = evt.Error;
             job.Timestamp = DateTime.Now;
             FailedJobs.Insert(0, job);
             if (FailedJobs.Count > MaxHistory) FailedJobs.RemoveAt(FailedJobs.Count - 1);
         }
         else 
         {
            // Maybe check Completed list if it was moved there erroneously?
             var completedJob = System.Linq.Enumerable.FirstOrDefault(CompletedJobs, j => j.TrackHash == evt.TrackGlobalId);
             if (completedJob != null)
             {
                 CompletedJobs.Remove(completedJob);
                 completedJob.Status = "Failed";
                 completedJob.Error = evt.Error;
                 FailedJobs.Insert(0, completedJob);
             }
             else 
             {
                 // Create new if missed start event
                 var newJob = new AnalysisJobViewModel(evt.TrackGlobalId, "Unknown")
                 {
                     Status = "Failed",
                     Error = evt.Error,
                     Timestamp = DateTime.Now
                 };
                 FailedJobs.Insert(0, newJob);
             }
         }
    }
}
