using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using DynamicData;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Data.Entities;
using Avalonia.Media;
using System.Reactive.Disposables;

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

public class LiveLogViewModel
{
    private readonly ForensicLogEntry _log;
    
    public string LogText => $"[{_log.Timestamp:HH:mm:ss}] [{_log.Level.ToUpper()}] [{_log.Stage}] {_log.Message}";
    public IBrush LogColor 
    {
        get
        {
            return _log.Level switch
            {
                "Error" => Brushes.OrangeRed,
                "Warning" => Brushes.Yellow,
                "Debug" => Brushes.Gray,
                _ => Brushes.LightGreen
            };
        }
    }
    
    public LiveLogViewModel(ForensicLogEntry log)
    {
        _log = log;
    }
}

public class AnalysisQueueViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    private bool _isDisposed;

    private readonly ILogger<AnalysisQueueViewModel> _logger;
    private readonly IEventBus _eventBus;
    private readonly INavigationService _navigationService;
    private readonly LibraryViewModel _libraryViewModel;
    private readonly AnalysisQueueService _queueService;
    private readonly MusicalBrainTestService _testService;

    public ObservableCollection<AnalysisJobViewModel> ActiveJobs { get; } = new();
    public ObservableCollection<AnalysisJobViewModel> CompletedJobs { get; } = new();
    public ObservableCollection<AnalysisJobViewModel> FailedJobs { get; } = new();
    
    // Mission Control: Thread Activity Tracking
    public ObservableCollection<ActiveThreadInfo> ActiveThreads => _queueService.ActiveThreads;
    
    // Mission Control: Metrics
    private double _tracksPerMinute;
    public double TracksPerMinute
    {
        get => _tracksPerMinute;
        set => this.RaiseAndSetIfChanged(ref _tracksPerMinute, value);
    }
    
    private int _totalProcessed;
    public int TotalProcessed
    {
        get => _totalProcessed;
        set => this.RaiseAndSetIfChanged(ref _totalProcessed, value);
    }
    
    private string _estimatedCompletion = "N/A";
    public string EstimatedCompletion
    {
        get => _estimatedCompletion;
        set => this.RaiseAndSetIfChanged(ref _estimatedCompletion, value);
    }
    
    // Metrics tracking
    private DateTime _sessionStartTime = DateTime.UtcNow;
    private readonly List<DateTime> _completionTimestamps = new();
    
    // We keep a history limit?
    private const int MaxHistory = 50;
    
    // Musical Brain Test Mode
    private bool _isTestRunning;
    public bool IsTestRunning
    {
        get => _isTestRunning;
        set => this.RaiseAndSetIfChanged(ref _isTestRunning, value);
    }
    
    private string _testStatus = "Ready to test";
    public string TestStatus
    {
        get => _testStatus;
        set => this.RaiseAndSetIfChanged(ref _testStatus, value);
    }

    public ReactiveCommand<AnalysisJobViewModel, Unit> InspectTrackCommand { get; }
    public ReactiveCommand<Unit, Unit> RunBrainTestCommand { get; }

    // Operation "All-Seeing Eye": Forensic Lab Mode
    private bool _isLabModeActive;
    public bool IsLabModeActive
    {
        get => _isLabModeActive;
        set => this.RaiseAndSetIfChanged(ref _isLabModeActive, value);
    }

    private ForensicLabViewModel? _labViewModel;
    public ForensicLabViewModel? LabViewModel
    {
        get => _labViewModel;
        set => this.RaiseAndSetIfChanged(ref _labViewModel, value);
    }

    public ReactiveCommand<Unit, Unit> CloseLabCommand { get; }

    // Mission Control: Live Forensic Stream
    public ObservableCollection<LiveLogViewModel> LiveForensicLogs { get; } = new();
    private const int MaxLogHistory = 100;
    
    // Mission Control: Library Analysis Search
    private string? _searchQuery;
    public string? SearchQuery
    {
        get => _searchQuery;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchQuery, value);
            if (string.IsNullOrWhiteSpace(value))
            {
                LibrarySearchResults.Clear();
            }
        }
    }

    public ObservableCollection<LibraryEntry> LibrarySearchResults { get; } = new();
    
    private bool _isForensicStreamExpanded;
    public bool IsForensicStreamExpanded
    {
        get => _isForensicStreamExpanded;
        set => this.RaiseAndSetIfChanged(ref _isForensicStreamExpanded, value);
    }

    public ReactiveCommand<Unit, Unit> ToggleForensicStreamCommand { get; }

    private readonly IForensicLogger _forensicLogger;
    private readonly ILibraryService _libraryService; // For Forensic Lab

    public AnalysisQueueViewModel(
        ILogger<AnalysisQueueViewModel> logger,
        IEventBus eventBus,
        INavigationService navigationService,
        LibraryViewModel libraryViewModel,
        AnalysisQueueService queueService,
        MusicalBrainTestService testService,
        IForensicLogger forensicLogger,
        ILibraryService libraryService) // For Forensic Lab
    {
        _logger = logger;
        _eventBus = eventBus;
        _navigationService = navigationService;
        _libraryViewModel = libraryViewModel;
        _queueService = queueService;
        _testService = testService;
        _forensicLogger = forensicLogger;
        _libraryService = libraryService;

        InspectTrackCommand = ReactiveCommand.Create<AnalysisJobViewModel>(InspectTrack);
        RunBrainTestCommand = ReactiveCommand.CreateFromTask(RunBrainTestAsync);
        ToggleForensicStreamCommand = ReactiveCommand.Create(() => { IsForensicStreamExpanded = !IsForensicStreamExpanded; });
        CloseLabCommand = ReactiveCommand.Create(() => 
        {
            LabViewModel?.Dispose();
            LabViewModel = null;
            IsLabModeActive = false;
        });

        // Search debouncing
        this.WhenAnyValue(x => x.SearchQuery)
            .Throttle(TimeSpan.FromMilliseconds(400))
            .DistinctUntilChanged()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async q => 
            {
                if (!string.IsNullOrWhiteSpace(q) && q.Length >= 2)
                {
                    await PerformSearchAsync(q);
                }
            })
            .DisposeWith(_disposables);

        // Subscriptions
        _disposables.Add(_eventBus.GetEvent<TrackAnalysisStartedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnAnalysisStarted));

        _disposables.Add(_eventBus.GetEvent<AnalysisProgressEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnAnalysisProgress));

        _disposables.Add(_eventBus.GetEvent<TrackAnalysisCompletedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnAnalysisCompleted));

        _disposables.Add(_eventBus.GetEvent<TrackAnalysisFailedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnAnalysisFailed));

        // Subscribe to live logs
        if (_forensicLogger is Services.TrackForensicLogger concreteLogger)
        {
            _disposables.Add(Observable.FromEventPattern<EventHandler<ForensicLogEntry>, ForensicLogEntry>(
                h => concreteLogger.LogGenerated += h,
                h => concreteLogger.LogGenerated -= h)
                .Select(x => x.EventArgs)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(OnNewForensicLog));
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        if (disposing)
        {
            _disposables.Dispose();
            LabViewModel?.Dispose();
        }
        _isDisposed = true;
    }


    private void OnNewForensicLog(ForensicLogEntry log)
    {
        var vm = new LiveLogViewModel(log);
        LiveForensicLogs.Insert(0, vm);
        
        while (LiveForensicLogs.Count > MaxLogHistory)
        {
            LiveForensicLogs.RemoveAt(LiveForensicLogs.Count - 1);
        }
    }

    private async Task PerformSearchAsync(string query)
    {
        try
        {
            var results = await _libraryService.SearchLibraryEntriesWithStatusAsync(query);
            
            LibrarySearchResults.Clear();
            foreach (var res in results)
            {
                LibrarySearchResults.Add(res);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed in AnalysisQueue");
        }
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

    /// <summary>
    /// Opens a track in the Forensic Lab dashboard with smart state management.
    /// Reuses existing ViewModel if same track to avoid reloading heavy resources.
    /// </summary>
    public async Task OpenTrackInLab(string trackHash)
    {
        // If already loaded, just show it
        if (LabViewModel?.TrackUniqueHash == trackHash)
        {
            IsLabModeActive = true;
            return;
        }

        // Dispose previous heavy resources (bitmaps, etc.)
        LabViewModel?.Dispose();

        // Create and load new ViewModel
        var vm = new ForensicLabViewModel(_eventBus, _libraryService);
        
        try
        {
            await vm.LoadTrackAsync(trackHash);
            LabViewModel = vm;
            IsLabModeActive = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load track in Forensic Lab: {Hash}", trackHash);
            vm.Dispose();
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
                
                // Update Mission Control metrics
                UpdateMetrics();
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
    
    /// <summary>
    /// Update Mission Control metrics: throughput, total processed, and ETA.
    /// </summary>
    private void UpdateMetrics()
    {
        // Track completion time
        _completionTimestamps.Add(DateTime.UtcNow);
        TotalProcessed++;
        
        // Keep only last 60 minutes of timestamps for accurate rate calculation
        var cutoff = DateTime.UtcNow.AddMinutes(-60);
        _completionTimestamps.RemoveAll(t => t < cutoff);
        
        // Calculate throughput (tracks per minute)
        var elapsed = (DateTime.UtcNow - _sessionStartTime).TotalMinutes;
        if (elapsed > 0)
        {
            TracksPerMinute = TotalProcessed / elapsed;
        }
        
        // Calculate ETA based on queue size and current rate
        int remainingInQueue = _queueService.QueuedCount - _queueService.ProcessedCount;
        if (remainingInQueue > 0 && TracksPerMinute > 0)
        {
            double minutesRemaining = remainingInQueue / TracksPerMinute;
            var eta = TimeSpan.FromMinutes(minutesRemaining);
            
            if (eta.TotalHours >= 1)
                EstimatedCompletion = $"{(int)eta.TotalHours}h {eta.Minutes}m";
            else if (eta.TotalMinutes >= 1)
                EstimatedCompletion = $"{(int)eta.TotalMinutes}m {eta.Seconds}s";
            else
                EstimatedCompletion = $"{eta.Seconds}s";
        }
        else
        {
            EstimatedCompletion = "N/A";
        }
    }
    
    /// <summary>
    /// Run Musical Brain diagnostic test.
    /// </summary>
    private async Task RunBrainTestAsync()
    {
        IsTestRunning = true;
        TestStatus = "Running pre-flight checks...";
        
        try
        {
            // Pre-flight validation
            var preFlightResult = await _testService.RunPreFlightChecksAsync();
            
            if (!preFlightResult.AllChecksPassed)
            {
                TestStatus = "❌ Pre-flight checks failed";
                _logger.LogError("Musical Brain test failed pre-flight checks");
                foreach (var check in preFlightResult.Checks)
                {
                    _logger.LogInformation(check);
                }
                return;
            }
            
            TestStatus = "✅ Pre-flight passed. Selecting test tracks...";
            _logger.LogInformation("Musical Brain pre-flight checks passed");
            
            // Select test tracks
            var testTracks = await _testService.SelectTestTracksAsync(10);
            
            if (!testTracks.Any())
            {
                TestStatus = "⚠️ No tracks found in library for testing";
                _logger.LogWarning("No tracks available for Musical Brain test");
                return;
            }
            
            TestStatus = $"Queuing {testTracks.Count} test tracks...";
            _logger.LogInformation("Selected {Count} test tracks", testTracks.Count);
            
            // Queue for analysis
            _testService.QueueTestTracks(testTracks);
            
            TestStatus = $"🧠 Testing {testTracks.Count} tracks - Watch Mission Control dashboard above!";
            _logger.LogInformation("Musical Brain test started with {Count} tracks", testTracks.Count);
        }
        catch (Exception ex)
        {
            TestStatus = $"❌ Test failed: {ex.Message}";
            _logger.LogError(ex, "Musical Brain test encountered an error");
        }
        finally
        {
            IsTestRunning = false;
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
