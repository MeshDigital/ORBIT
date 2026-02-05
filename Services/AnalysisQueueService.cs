using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Linq;
using SLSKDONET.Data; // For AppDbContext
using SLSKDONET.Data.Entities;
using SLSKDONET.Models; // For Events
using Microsoft.EntityFrameworkCore;
using Avalonia.Threading; // For UI thread safety
using SLSKDONET.Data.Essentia;

namespace SLSKDONET.Services;

/// <summary>
/// Phase 4: Musical Brain Queue - Producer-Consumer pattern for audio analysis.
/// 
/// ARCHITECTURE:
/// Producer: DownloadManager enqueues tracks as they complete
/// Queue: Unbounded channel (never blocks producers)
/// Consumer: 2 worker threads call EssentiaAnalyzerService
/// 
/// WHY THIS DESIGN:
/// 1. Decoupling: Downloads don't wait for analysis (10-15 second operation)
/// 2. Throttling: 2 workers prevent CPU saturation (Essentia uses 80-100% per process)
/// 3. Priority: High-value tracks (just played) can jump the queue
/// 4. Resilience: Worker crash doesn't kill the queue
/// 5. Visibility: UI shows "3 tracks analyzing, 47 queued, 2h ETA"
/// 
/// WORKER STRATEGY:
/// - 2 workers = sweet spot for 4+ core CPUs (leaves room for UI/downloads)
/// - SemaphoreSlim(2) ensures only 2 Essentia processes run concurrently
/// - More workers = faster analysis but UI lag and thermal throttling
/// </summary>
public class AnalysisQueueService : INotifyPropertyChanged
{
    private readonly Channel<AnalysisRequest> _channel;
    private readonly Channel<AnalysisRequest> _priorityChannel; // Phase 4 Queue Upgrade
    private readonly IEventBus _eventBus;
    private readonly ILogger<AnalysisQueueService> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private int _queuedCount = 0;
    private int _processedCount = 0;
    private string? _currentTrackHash = null;
    private bool _isPaused = false;
    private bool _isStealthMode = false;
    
    // Thread tracking for Mission Control dashboard
    private readonly ConcurrentDictionary<int, ActiveThreadInfo> _activeThreads = new();
    private readonly ConcurrentDictionary<string, byte> _activeRequestHashes = new(); // Phase 21: Runtime Deduplication
    public ObservableCollection<ActiveThreadInfo> ActiveThreads { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public int QueuedCount
    {
        get => _queuedCount;
        private set
        {
            if (_queuedCount != value)
            {
                _queuedCount = value;
                OnPropertyChanged();
                PublishStatusEvent();
            }
        }
    }

    public int ProcessedCount
    {
        get => _processedCount;
        private set
        {
            if (_processedCount != value)
            {
                _processedCount = value;
                OnPropertyChanged();
                PublishStatusEvent();
            }
        }
    }

    public string? CurrentTrackHash
    {
        get => _currentTrackHash;
        private set
        {
            if (_currentTrackHash != value)
            {
                _currentTrackHash = value;
                OnPropertyChanged();
                PublishStatusEvent();
            }
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (_isPaused != value)
            {
                _isPaused = value;
                OnPropertyChanged();
                PublishStatusEvent();
            }
        }
    }

    public bool IsStealthMode
    {
        get => _isStealthMode;
        set
        {
            if (_isStealthMode != value)
            {
                _isStealthMode = value;
                OnPropertyChanged();
                PublishStatusEvent();
            }
        }
    }

    public void SetStealthMode(bool enabled)
    {
        IsStealthMode = enabled;
        _logger.LogInformation("🕵️ Mission Control: Stealth Mode set to {Enabled}", enabled);
    }

    public AnalysisQueueService(IEventBus eventBus, ILogger<AnalysisQueueService> logger, IDbContextFactory<AppDbContext> dbFactory)
    {
        _eventBus = eventBus;
        _logger = logger;
        _dbFactory = dbFactory;
        // WHY: Unbounded channel instead of bounded:
        // - Producer (DownloadManager) should NEVER block on enqueue
        // - Analysis is non-critical: if queue grows to 1000, it just takes longer
        // - Bounded channel would cause downloads to pause (unacceptable UX)
        // - Memory cost: ~100 bytes per request = 10,000 queued = 1MB (negligible)
        // - Memory cost: ~100 bytes per request = 10,000 queued = 1MB (negligible)
        // - Memory cost: ~100 bytes per request = 10,000 queued = 1MB (negligible)
        _channel = Channel.CreateUnbounded<AnalysisRequest>();
        _priorityChannel = Channel.CreateUnbounded<AnalysisRequest>(); // Priority Lane
        
        // Subscribe to manual triggers
        _eventBus.GetEvent<TrackAnalysisRequestedEvent>().Subscribe(OnAnalysisRequested);
    }

    public void QueueAnalysis(string filePath, string trackHash, AnalysisTier tier = AnalysisTier.Tier1, bool highPriority = false, Guid? dbId = null)
    {
        // Phase 21: Runtime Deduplication
        if (!_activeRequestHashes.TryAdd(trackHash, 0))
        {
            _logger.LogDebug("🧠 Analysis already in progress/queued for {Hash}, skipping duplicate request.", trackHash);
            return;
        }

        var request = new AnalysisRequest(filePath, trackHash, tier, DatabaseId: dbId);
        
        if (highPriority)
        {
            _priorityChannel.Writer.TryWrite(request);
            _logger.LogInformation("🚀 Priority Analysis Queued for {Hash}", trackHash);
        }
        else
        {
            _channel.Writer.TryWrite(request);
        }

        Interlocked.Increment(ref _queuedCount);
        OnPropertyChanged(nameof(QueuedCount));
        PublishStatusEvent();
    }



    private void OnAnalysisRequested(TrackAnalysisRequestedEvent evt)
    {
        _logger.LogInformation("🧠 Manual Analysis Requested for {Hash}", evt.TrackGlobalId);
        
        // Fire and forget lookup (don't block event bus)
        Task.Run(async () =>
        {
            try
            {
                using var context = await _dbFactory.CreateDbContextAsync();
                
                // Try LibraryEntry first (primary source for files)
                var entry = await context.LibraryEntries
                    .FirstOrDefaultAsync(e => e.UniqueHash == evt.TrackGlobalId);
                    
                if (entry != null && System.IO.File.Exists(entry.FilePath))
                {
                    QueueAnalysis(entry.FilePath, entry.UniqueHash, evt.Tier, dbId: entry.Id);
                    return;
                }
                
                // Fallback: Check PlaylistTracks (might be downloaded but not fully indexed?)
                var track = await context.PlaylistTracks
                    .FirstOrDefaultAsync(t => t.TrackUniqueHash == evt.TrackGlobalId && t.Status == TrackStatus.Downloaded);
                    
                if (track != null && !string.IsNullOrEmpty(track.ResolvedFilePath) && System.IO.File.Exists(track.ResolvedFilePath))
                {
                    QueueAnalysis(track.ResolvedFilePath, evt.TrackGlobalId, evt.Tier, dbId: track.Id);
                    return;
                }
                
                _logger.LogWarning("❌ Could not find file for analysis request: {Hash}", evt.TrackGlobalId);
                _eventBus.Publish(new TrackAnalysisFailedEvent(evt.TrackGlobalId, "File not found locally"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing manual analysis request");
                _eventBus.Publish(new TrackAnalysisFailedEvent(evt.TrackGlobalId, "Internal Error"));
            }
        });
    }

    /// <summary>
    /// Phase 13A: Restoration utility.
    /// Scans the library for tracks that have not been analyzed or have stalled runs and enqueues them.
    /// </summary>
    public async Task RestoreQueueOrphansAsync()
    {
        _logger.LogInformation("🧠 Musical Brain: Restoring Queue Orphans (Scanning library for unanalyzed tracks)...");

        try
        {
            using var context = await _dbFactory.CreateDbContextAsync();
            
            // 1. Find tracks in library that need analysis
            // Optimization: Skip tracks that explicitly FAILED or are COMPLETED
            var unanalyzedTracks = await context.LibraryEntries
                .Where(le => le.AnalysisStatus == AnalysisStatus.None || le.AnalysisStatus == AnalysisStatus.Pending)
                .Where(le => !context.AudioFeatures.Any(af => af.TrackUniqueHash == le.UniqueHash))
                .OrderByDescending(le => le.AddedAt)
                .Take(2000) // Don't overwhelm the channel in one go
                .ToListAsync();

            // 2. Find Stalled Runs (Processing but app crashed)
            // If a run is "Processing" but we just started up, it's a crash. Mark as Failed or Re-queue.
            var stalledRuns = await context.AnalysisRuns
                .Where(r => r.Status == AnalysisRunStatus.Processing)
                .ToListAsync();

            if (stalledRuns.Any())
            {
                _logger.LogWarning("Found {Count} stalled analysis runs from previous session. Marking as Failed.", stalledRuns.Count);
                foreach (var run in stalledRuns)
                {
                    run.Status = AnalysisRunStatus.Failed;
                    run.ErrorMessage = "Application crashed / Restarted during analysis";
                    run.CompletedAt = DateTime.UtcNow;
                }
                await context.SaveChangesAsync();
                
                // Optional: Re-queue them? For now, let the unanalyzed check below pick them up if they strictly lack AudioFeatures.
                // If they have AudioFeatures but failed Forensics, the query above won't catch them.
                // Assuming "Unanalyzed" means missing AudioFeatures for now.
            }

            _logger.LogInformation("🧠 Queue Restore: Found {Count} unanalyzed tracks in library.", unanalyzedTracks.Count);

            int enqueuedCount = 0;
            foreach (var track in unanalyzedTracks)
            {
                if (File.Exists(track.FilePath))
                {
                    QueueAnalysis(track.FilePath, track.UniqueHash, AnalysisTier.Tier1, dbId: track.Id);
                    enqueuedCount++;
                }
                else
                {
                    _logger.LogDebug("Queue Restore: Skipping missing file {Path}", track.FilePath);
                }
            }

            _logger.LogInformation("✅ Queue Restore: Enqueued {Count} tracks for analysis.", enqueuedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore orphaned queue items");
        }
    }

    public void NotifyProcessingStarted(string trackHash, string fileName, Guid? dbId = null)
    {
        CurrentTrackHash = trackHash;
        _eventBus.Publish(new TrackAnalysisStartedEvent(trackHash, fileName) { DatabaseId = dbId });
    }

    public void NotifyProcessingCompleted(string trackHash, bool success, string? error = null)
    {
        _activeRequestHashes.TryRemove(trackHash, out _); // Cleanup deduplication set
        
        Interlocked.Increment(ref _processedCount);
        Interlocked.Decrement(ref _queuedCount);
        CurrentTrackHash = null;
        
        OnPropertyChanged(nameof(QueuedCount));
        OnPropertyChanged(nameof(ProcessedCount));
        PublishStatusEvent();
        
        // MOVED TO FLUSH BATCH ASYNC TO FIX RACE CONDITION
        // _eventBus.Publish(new TrackAnalysisCompletedEvent(trackHash, success, error));
        // Publish legacy completion event for UI compatibility
        // MOVED TO FLUSH BATCH ASYNC TO FIX RACE CONDITION
        // _eventBus.Publish(new AnalysisCompletedEvent(trackHash, success, error));
        
        if (!success && error != null)
        {
             _eventBus.Publish(new TrackAnalysisFailedEvent(trackHash, error));
        }
    }

    // Album Priority: Queue entire album for immediate analysis
    public int QueueAlbumWithPriority(List<PlaylistTrack> tracks, AnalysisTier tier = AnalysisTier.Tier1)
    {
        var count = 0;
        foreach (var track in tracks)
        {
            if (!string.IsNullOrEmpty(track.ResolvedFilePath) && !string.IsNullOrEmpty(track.TrackUniqueHash))
            {
                QueueAnalysis(track.ResolvedFilePath, track.TrackUniqueHash, tier, dbId: track.Id);
                count++;
            }
        }
        
        System.Diagnostics.Debug.WriteLine($"Queued {count} tracks from album for {tier} priority analysis");
        return count;
    }

    public void QueueTrackWithPriority(PlaylistTrack track, AnalysisTier tier = AnalysisTier.Tier1)
    {
        if (!string.IsNullOrEmpty(track.ResolvedFilePath) && !string.IsNullOrEmpty(track.TrackUniqueHash))
        {
            QueueAnalysis(track.ResolvedFilePath, track.TrackUniqueHash, tier, dbId: track.Id);
             _logger.LogInformation("Manual priority queue ({Tier}): {File}", tier, track.Title);
        }
    }

    public void ForceQueueAnalysis(string filePath, string trackHash, Guid? dbId = null)
    {
         _activeRequestHashes.TryRemove(trackHash, out _); // Force: allow re-entry
         QueueAnalysis(filePath, trackHash, AnalysisTier.Tier3, highPriority: true, dbId: dbId);
    }

    private void PublishStatusEvent()
    {
        _eventBus.Publish(new AnalysisQueueStatusChangedEvent(
            QueuedCount,
            ProcessedCount,
            CurrentTrackHash,
            IsPaused,
            IsStealthMode ? "Stealth (Eco)" : SystemInfoHelper.GetCurrentPowerMode().ToString(),
            SystemInfoHelper.GetOptimalParallelism()
        ));
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        if (PropertyChanged != null)
        {
            // Ensure UI updates happen on the UI thread
            Dispatcher.UIThread.Post(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));
        }
    }
    
    /// <summary>
    /// Update the status of a specific analysis thread for Mission Control dashboard.
    /// </summary>
    public void UpdateThreadStatus(int threadId, string trackName, string status, double progress = 0, float bpmConfidence = 0, float keyConfidence = 0, float integrityScore = 0, AnalysisStage stage = AnalysisStage.Probing)
    {
        var info = new ActiveThreadInfo
        {
            ThreadId = threadId,
            CurrentTrack = string.IsNullOrEmpty(trackName) ? "-" : System.IO.Path.GetFileName(trackName),
            Status = status,
            Progress = progress,
            StartTime = status == "Idle" ? null : (DateTime?)DateTime.Now,
            BpmConfidence = bpmConfidence,
            KeyConfidence = keyConfidence,
            IntegrityScore = integrityScore,
            DatabaseId = status == "Idle" ? null : (Guid?)null // Will be updated by caller
        };
        
        _activeThreads.AddOrUpdate(threadId, info, (id, old) => info);
        
        // Update UI collection on UI thread
        Dispatcher.UIThread.Post(() =>
        {
            var existing = ActiveThreads.FirstOrDefault(t => t.ThreadId == threadId);
            if (existing != null)
            {
                // Update existing item
                existing.CurrentTrack = info.CurrentTrack;
                existing.Status = info.Status;
                existing.Progress = info.Progress;
                existing.StartTime = info.StartTime;
                existing.BpmConfidence = info.BpmConfidence;
                existing.KeyConfidence = info.KeyConfidence;
                existing.IntegrityScore = info.IntegrityScore;
                existing.CurrentStage = info.CurrentStage;
                // Update DatabaseId if available
            }
            else
            {
                // Add new thread
                ActiveThreads.Add(info);
            }
        });
    }

    public ChannelReader<AnalysisRequest> Reader => _channel.Reader;
    public ChannelReader<AnalysisRequest> PriorityReader => _priorityChannel.Reader;
}

public record AnalysisRequest(string FilePath, string TrackHash, AnalysisTier Tier = AnalysisTier.Tier1, bool Force = false, Guid? DatabaseId = null);

public class AnalysisWorker : BackgroundService
{
    private readonly AnalysisQueueService _queue;
    private readonly IServiceProvider _serviceProvider;
    private readonly IEventBus _eventBus;
    private readonly ILogger<AnalysisWorker> _logger;
    private readonly IForensicLogger _forensicLogger;
    private readonly Services.Musical.CueGenerationEngine _cueEngine;
    private readonly IForensicLockdownService _lockdown;
    private readonly Services.Audio.PhraseDetectionService _phraseDetector;
    private readonly Services.Musical.VocalIntelligenceService _vocalService;

    
    // Phase 1.2: Dynamic Concurrency & Pressure Monitor
#pragma warning disable CA1416 // Validate platform compatibility
    private System.Diagnostics.PerformanceCounter? _cpuCounter;
#pragma warning restore CA1416 // Validate platform compatibility
    private DateTime _lastCpuCheck = DateTime.MinValue;
    private float _lastCpuUsage = 0;
    
    // Batching & Throttling State
    private DateTime _lastProgressReport = DateTime.MinValue;
    private readonly List<AnalysisResultContext> _pendingResults = new();
    private DateTime _lastBatchSave = DateTime.UtcNow;
    private const int BatchSize = 3; // Reduced from 10 to 3 for safer persistence
    private readonly TimeSpan BatchTimeout = TimeSpan.FromSeconds(2); // Reduced from 5s to 2s

    public AnalysisWorker(
        AnalysisQueueService queue, 
        IServiceProvider serviceProvider, 
        IEventBus eventBus, 
        ILogger<AnalysisWorker> logger, 
        Configuration.AppConfig config, 
        IForensicLogger forensicLogger, 
        Services.Musical.CueGenerationEngine cueEngine, 
        IForensicLockdownService lockdown, 
        Services.Audio.PhraseDetectionService phraseDetector,
        Services.Musical.VocalIntelligenceService vocalService)
    {
        _queue = queue;
        _serviceProvider = serviceProvider;
        _eventBus = eventBus;
        _logger = logger;
        _forensicLogger = forensicLogger;
        _cueEngine = cueEngine;
        _lockdown = lockdown;
        _phraseDetector = phraseDetector;
        _vocalService = vocalService;
        
        // Initialize CPU Counter for Pressure Monitor (Windows only)
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            try
            {
#pragma warning disable CA1416 // Validate platform compatibility
                _cpuCounter = new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total");
                _cpuCounter.NextValue(); // First call always returns 0
#pragma warning restore CA1416 // Validate platform compatibility
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to initialize CPU Pressure Monitor: {Ex}", ex.Message);
            }
        }
    }
    
    /// <summary>
    /// Phase 1.2: Calculates dynamic concurrency limit based on System Pressure.
    /// </summary>
    private int GetDynamicConcurrencyLimit()
    {
        int optimal = SystemInfoHelper.GetOptimalParallelism();
        
        // If we have a CPU counter, apply pressure throttling
        if (_cpuCounter != null)
        {
            // Only poll every 2 seconds to avoid perf overhead
            if ((DateTime.UtcNow - _lastCpuCheck).TotalSeconds > 2)
            {
#pragma warning disable CA1416 // Validate platform compatibility
                _lastCpuUsage = _cpuCounter.NextValue();
#pragma warning restore CA1416 // Validate platform compatibility
                _lastCpuCheck = DateTime.UtcNow;
            }
            
            // Pressure Logic:
            // > 85% CPU: Throttle down aggressively
            if (_lastCpuUsage > 85)
            {
                return Math.Max(1, optimal / 2);
            }
            // < 50% CPU: Allow full speed
        }
        
        return optimal;
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🧠 Musical Brain (AnalysisWorker) started with ~{Threads} parallel threads.", SystemInfoHelper.GetOptimalParallelism());
        
        // Restore Queue Orphans on startup
        await _queue.RestoreQueueOrphansAsync();

        // Track batch metrics
        int processedInBatch = 0;
        var batchStartTime = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            // Cooperate with Thread Pool
            await Task.Yield();

            // 1. Process Pending Batch if needed (Timeout or Size)
            if (_pendingResults.Count > 0 && (_pendingResults.Count >= BatchSize || DateTime.UtcNow - _lastBatchSave > BatchTimeout))
            {
                await FlushBatchAsync(stoppingToken);
            }

            // 2. Collect batch of requests (Dynamic Parallelism)
            // Phase 1.2: Check Pressure Monitor
            int currentMaxThreads = GetDynamicConcurrencyLimit();
            
            var batch = new List<AnalysisRequest>();
            try
            {
                // Try to read up to currentMaxThreads items without blocking too long
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(1000); // 1 second timeout

                // 1. Check Priority Lane (Drain first)
                while (batch.Count < currentMaxThreads && _queue.PriorityReader.TryRead(out var pRequest))
                {
                     batch.Add(pRequest);
                }
                
                // 2. Fill remaining from Standard Lane
                while (batch.Count < currentMaxThreads && _queue.Reader.TryRead(out var request))
                {
                    batch.Add(request);
                }
                
                // If no items ready, wait for at least one (Standard lane)
                if (batch.Count == 0)
                {
                    try
                    {
                        // Note: This only waits on standard channel. Ideally we'd WaitAny on both.
                        // For simplicity, we poll standard. Priority tracks might wait up to 1s if idle.
                        var request = await _queue.Reader.ReadAsync(cts.Token);
                        batch.Add(request);
                    }
                    catch (OperationCanceledException)
                    {
                        // Timeout or stop - just loop
                        if (stoppingToken.IsCancellationRequested) break;
                        continue;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (stoppingToken.IsCancellationRequested) break;
                continue;
            }

            if (batch.Count == 0) continue;

            // Check pause state
            while (_queue.IsPaused && !stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(500, stoppingToken);
            }

            // Phase 25/27: Forensic Lockdown / Safe Mode check
            // If lockdown is active (e.g., DJ is playing), we suspend all analysis to protect the audio buffer.
            while (_lockdown.IsLockdownActive && !stoppingToken.IsCancellationRequested)
            {
                // Set all threads to "Standby"
                for (int i = 0; i < currentMaxThreads; i++)
                {
                    _queue.UpdateThreadStatus(i, string.Empty, "🛡️ Lockdown Standby", 0);
                }
                
                await Task.Delay(2000, stoppingToken);
            }

            // Phase 1.2: Determine Priority based on Power Mode (Architecture Aware)
            var powerMode = SystemInfoHelper.GetCurrentPowerMode();
            var processPriority = (powerMode == SystemInfoHelper.PowerEfficiencyMode.Efficiency || _queue.IsStealthMode) 
                ? System.Diagnostics.ProcessPriorityClass.Idle      // Eco Mode = E-Cores
                : System.Diagnostics.ProcessPriorityClass.BelowNormal; // Balanced

            // 3. Process batch in parallel
            var processingTasks = batch.Select(async (request, index) =>
            {
                var threadId = index; // Use index as thread ID for this batch
                
                // REMOVED SemaphoreSlim wait - Batch size ALREADY limits concurrency dynamically
                try
                {
                    // Update thread status: Processing
                    _queue.UpdateThreadStatus(threadId, request.FilePath, "Processing", 0);
                    
                    // Update thread info with DB ID
                    var threadInfo = _queue.ActiveThreads.FirstOrDefault(t => t.ThreadId == threadId);
                    if (threadInfo != null) threadInfo.DatabaseId = request.DatabaseId;

                    // Phase 1.2: Pass Priority
                    await ProcessRequestAsync(request, threadId, processPriority, stoppingToken);
                    Interlocked.Increment(ref processedInBatch);
                    
                    // Update thread status: Complete
                    _queue.UpdateThreadStatus(threadId, request.FilePath, "Complete", 100, 1, 1, 1, AnalysisStage.Complete);
                    
                    // Log progress every 10 tracks
                    if (processedInBatch % 10 == 0)
                    {
                        LogBatchProgress(processedInBatch, batchStartTime, currentMaxThreads);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Thread {ThreadId} failed processing track", threadId);
                    _queue.UpdateThreadStatus(threadId, request.FilePath, "Error", 0, 0, 0, 0, AnalysisStage.Complete);
                }
                finally
                {
                    // Return thread to idle
                    _queue.UpdateThreadStatus(threadId, string.Empty, "Idle", 0, 0, 0, 0, AnalysisStage.Probing);
                }
            }).ToList();

            await Task.WhenAll(processingTasks);
        }

        // Final Flush
        if (_pendingResults.Any()) await FlushBatchAsync(CancellationToken.None);
        
        _logger.LogInformation("🧠 Musical Brain (AnalysisWorker) stopped. Processed {Total} tracks.", processedInBatch);
    }
    
    private void LogBatchProgress(int processed, DateTime startTime, int activeThreads)
    {
        var elapsed = DateTime.UtcNow - startTime;
        var tracksPerMinute = elapsed.TotalMinutes > 0 ? processed / elapsed.TotalMinutes : 0;
        
        _logger.LogInformation(
            "📈 Analysis progress: {Processed} tracks in {Elapsed} ({Rate:F1}/min) - {Threads} threads active",
            processed,
            elapsed.ToString(@"hh\:mm\:ss"),
            tracksPerMinute,
            activeThreads
        );
    }


    private async Task ProcessRequestAsync(AnalysisRequest request, int threadId, System.Diagnostics.ProcessPriorityClass priority, CancellationToken stoppingToken)
    {
        string trackHash = request.TrackHash;
        bool analysisSucceeded = false;
        string? errorMessage = null;
        
        // Local telemetry state
        float currentBpmConfidence = 0;
        float currentKeyConfidence = 0;
        float currentIntegrityScore = 0;

        // Generate a Correlation ID for this entire analysis flow
        string correlationId = Guid.NewGuid().ToString();
        
        // Phase 21: Analysis Run Tracking
        var runId = Guid.NewGuid();
        var runStartTime = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Context to hold results for batching
        var resultContext = new AnalysisResultContext { TrackHash = trackHash, FilePath = request.FilePath, CorrelationId = correlationId, DatabaseId = request.DatabaseId };

        using (_forensicLogger.TimedOperation(correlationId, ForensicStage.AnalysisQueue, "Full Analysis Pipeline", trackHash))
        {
            try
            {
                _queue.NotifyProcessingStarted(trackHash, request.FilePath, request.DatabaseId);
                _forensicLogger.Info(correlationId, ForensicStage.AnalysisQueue, "Processing started", trackHash);

                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var essentiaAnalyzer = scope.ServiceProvider.GetRequiredService<IAudioIntelligenceService>();
                var audioAnalyzer = scope.ServiceProvider.GetRequiredService<IAudioAnalysisService>();
                var waveformAnalyzer = scope.ServiceProvider.GetRequiredService<WaveformAnalysisService>();
                
                // Stage: Probing
                _queue.UpdateThreadStatus(threadId, request.FilePath, "Probing File...", 5, stage: AnalysisStage.Probing);

                // Create initial run record
                var run = new AnalysisRunEntity
                {
                    RunId = runId,
                    TrackUniqueHash = trackHash,
                    TrackTitle = System.IO.Path.GetFileNameWithoutExtension(request.FilePath),
                    FilePath = request.FilePath,
                    StartedAt = runStartTime,
                    Status = AnalysisRunStatus.Processing,
                    Tier = request.Tier, // Set the requested tier
                    TriggerSource = "AutoQueue", // TODO: pass from request
                    AnalysisVersion = "Essentia-2.1-beta5", // TODO: get from config
                    CurrentStage = AnalysisStage.Probing
                };
                dbContext.AnalysisRuns.Add(run);
                await dbContext.SaveChangesAsync(stoppingToken);

                _logger.LogInformation("🧠 Analyzing: {Hash}", trackHash);

                // Match timeout to EssentiaAnalyzerService (120s) + buffer for Waveform/IO
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);

                // 1. Generate Waveform
                run.CurrentStage = AnalysisStage.Waveform;
                _queue.UpdateThreadStatus(threadId, request.FilePath, "Generating Waveform...", 10, stage: AnalysisStage.Waveform);
                PublishThrottled(new AnalysisProgressEvent(trackHash, "Generating waveform...", 10, currentBpmConfidence, currentKeyConfidence, currentIntegrityScore));
                
                var waveformStopwatch = System.Diagnostics.Stopwatch.StartNew();
                resultContext.WaveformData = await Task.Run(() => waveformAnalyzer.GenerateWaveformAsync(request.FilePath, linkedCts.Token), linkedCts.Token);
                _forensicLogger.Info(correlationId, ForensicStage.AnalysisQueue, "Waveform generated", trackHash);
                run.WaveformGenerated = true;
                run.FfmpegDurationMs = waveformStopwatch.ElapsedMilliseconds;

                // Phase 13: Demo-Safe Analysis Mode
                if (request.FilePath.Contains("DEMO_TRACK", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("🎬 DEMO MODE: Providing deterministic analysis for {Hash}", trackHash);
                    resultContext.MusicalResult = new AudioFeaturesEntity 
                    { 
                        TrackUniqueHash = trackHash,
                        Danceability = 0.85f,
                        Energy = 0.9f,
                        MoodTag = "Happy",
                        MoodConfidence = 0.95f,
                        BpmConfidence = 0.99f 
                    };
                    analysisSucceeded = true;
                }
                else
                {
                    // 2. Musical Analysis
                    run.CurrentStage = AnalysisStage.Intelligence;
                    _queue.UpdateThreadStatus(threadId, request.FilePath, "Musical Intelligence...", 40, stage: AnalysisStage.Intelligence);
                    PublishThrottled(new AnalysisProgressEvent(trackHash, "Analyzing musical features...", 40, currentBpmConfidence, currentKeyConfidence, currentIntegrityScore));
                    
                    var essentiaStopwatch = System.Diagnostics.Stopwatch.StartNew();
                    resultContext.MusicalResult = await Task.Run(() => essentiaAnalyzer.AnalyzeTrackAsync(request.FilePath, trackHash, correlationId, linkedCts.Token, tier: request.Tier, priority: priority), linkedCts.Token);
                    run.EssentiaAnalysisCompleted = true;
                    run.EssentiaDurationMs = essentiaStopwatch.ElapsedMilliseconds;
                    
                    if (resultContext.MusicalResult != null)
                    {
                        currentBpmConfidence = resultContext.MusicalResult.BpmConfidence;
                        currentKeyConfidence = 0.8f; // Placeholder as Essentia gives key but confidence varies
                        run.BpmConfidence = currentBpmConfidence;
                        run.KeyConfidence = currentKeyConfidence;
                        
                        // Phase 3.5: Vocal Intelligence Post-Processing
                        if (!string.IsNullOrEmpty(resultContext.MusicalResult.VocalDensityCurveJson))
                        {
                            try
                            {
                                float[] densityCurve = System.Text.Json.JsonSerializer.Deserialize<float[]>(resultContext.MusicalResult.VocalDensityCurveJson) ?? Array.Empty<float>();
                                double duration = resultContext.MusicalResult.TrackDuration > 0 ? resultContext.MusicalResult.TrackDuration : (double)(resultContext.WaveformData?.DurationSeconds ?? 0);
                                
                                var vocalMetrics = _vocalService.AnalyzeVocalDensity(densityCurve, duration);
                                resultContext.MusicalResult.DetectedVocalType = vocalMetrics.Type;
                                resultContext.MusicalResult.VocalIntensity = vocalMetrics.Intensity;
                                resultContext.MusicalResult.VocalStartSeconds = vocalMetrics.StartSeconds;
                                resultContext.MusicalResult.VocalEndSeconds = vocalMetrics.EndSeconds;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning("Vocal analysis failed for {Hash}: {Msg}", trackHash, ex.Message);
                            }
                        }

                        _queue.UpdateThreadStatus(threadId, request.FilePath, "AI Musical Scan Complete", 60, currentBpmConfidence, currentKeyConfidence, currentIntegrityScore, AnalysisStage.Intelligence);
                    }
                }

                // 3. Technical Analysis
                run.CurrentStage = AnalysisStage.Forensics;
                _queue.UpdateThreadStatus(threadId, request.FilePath, "Forensic Integrity Scan...", 70, currentBpmConfidence, currentKeyConfidence, currentIntegrityScore, stage: AnalysisStage.Forensics);
                PublishThrottled(new AnalysisProgressEvent(trackHash, "Running technical analysis...", 70, currentBpmConfidence, currentKeyConfidence, currentIntegrityScore));
                
                // Optimization: Check if DownloadManager already performed analysis (Phase 0.9 Resilience)
                // Unless Force is true (Phase 1.4: Deep Retry)
                AudioAnalysisEntity? existingAnalysis = null;
                if (!request.Force)
                {
                    existingAnalysis = await dbContext.AudioAnalysis.AsNoTracking()
                        .FirstOrDefaultAsync(a => a.TrackUniqueHash == trackHash, linkedCts.Token);
                }

                if (existingAnalysis != null)
                {
                     _logger.LogInformation("🧠 Forensics: Reusing existing technical analysis for {Hash}", trackHash);
                     resultContext.TechResult = existingAnalysis;
                }
                else
                {
                    resultContext.TechResult = await Task.Run(() => audioAnalyzer.AnalyzeFileAsync(request.FilePath, trackHash, correlationId, linkedCts.Token), linkedCts.Token);
                }
                run.FfmpegAnalysisCompleted = true;
                
                if (resultContext.TechResult != null)
                {
                    currentIntegrityScore = (float)resultContext.TechResult.QualityConfidence;
                    run.IntegrityScore = currentIntegrityScore;
                    _queue.UpdateThreadStatus(threadId, request.FilePath, "Forensics Complete", 80, currentBpmConfidence, currentKeyConfidence, currentIntegrityScore, AnalysisStage.Forensics);
                }

                // 4. Cue Generation (Heuristic)
                run.CurrentStage = AnalysisStage.Finalizing;
                _queue.UpdateThreadStatus(threadId, request.FilePath, "Finalizing Cues...", 85, currentBpmConfidence, currentKeyConfidence, currentIntegrityScore, stage: AnalysisStage.Finalizing);
                
                if (resultContext.WaveformData != null)
                {
                    PublishThrottled(new AnalysisProgressEvent(trackHash, "Generating cue points...", 85, currentBpmConfidence, currentKeyConfidence, currentIntegrityScore));
                    
                    // 1. Get Physical Cues (based on waveform peaks/troughs)
                    var cues = await _cueEngine.GenerateCuesAsync(resultContext.WaveformData, resultContext.WaveformData.DurationSeconds);
                    
                    // 2. Enrich with AI Musical Cues (Essentia)
                    if (resultContext.MusicalResult != null)
                    {
                        // Drop
                        if (resultContext.MusicalResult.DropTimeSeconds.HasValue && resultContext.MusicalResult.DropTimeSeconds > 5)
                        {
                            // Remove any physical cue near this timestamp to avoid duplicates (within 2s)
                            cues.RemoveAll(c => Math.Abs(c.Timestamp - resultContext.MusicalResult.DropTimeSeconds.Value) < 2.0);
                            
                            cues.Add(new OrbitCue 
                            { 
                                Timestamp = resultContext.MusicalResult.DropTimeSeconds.Value,
                                Name = "DROP",
                                Role = CueRole.Drop,
                                Color = "#FF0055", // Neon Red
                                Source = CueSource.Auto,
                                Confidence = 0.9f
                            });
                        }

                        // Intro End (Phrase Start)
                        if (resultContext.MusicalResult.CueIntro > 0)
                        {
                            cues.Add(new OrbitCue 
                            { 
                                Timestamp = resultContext.MusicalResult.CueIntro,
                                Name = "Intro End",
                                Role = CueRole.Custom, // Or PhraseStart
                                Color = "#00BFFF", // Deep Sky Blue
                                Source = CueSource.Auto,
                                Confidence = 0.85f
                            });
                        }
                    }
                    
                    // Sort by time
                    resultContext.Cues = cues.OrderBy(c => c.Timestamp).ToList();
                    
                    _forensicLogger.Info(correlationId, ForensicStage.AnalysisQueue, $"Generated {cues.Count} structural cues (Merged AI + Physical)", trackHash);
                }

                PublishThrottled(new AnalysisProgressEvent(trackHash, "Queued for save...", 90));
                
                // Add to batch buffer
                lock (_pendingResults)
                {
                    _pendingResults.Add(resultContext);
                    _logger.LogInformation("💾 Result buffered for {Hash}. Pending count: {Count}", trackHash, _pendingResults.Count);
                }
                analysisSucceeded = true;
                _forensicLogger.Info(correlationId, ForensicStage.AnalysisQueue, "Results queued for batch persistence", trackHash);
                
                // Mark run as completed
                run.Status = Data.Entities.AnalysisRunStatus.Completed;
                run.CompletedAt = DateTime.UtcNow;
                run.DurationMs = sw.ElapsedMilliseconds;
                await dbContext.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogError("⏱ Track analysis timed out or cancelled: {Hash}", trackHash);
                errorMessage = "Analysis timed out";
                _forensicLogger.Warning(correlationId, ForensicStage.AnalysisQueue, "Analysis timed out or cancelled", trackHash);
                
                // Update run record with cancellation
                using var errorScope = _serviceProvider.CreateScope();
                var errorDb = errorScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var errorRun = await errorDb.AnalysisRuns.FindAsync(runId);
                if (errorRun != null)
                {
                    errorRun.Status = Data.Entities.AnalysisRunStatus.Cancelled;
                    errorRun.ErrorMessage = "Timeout or cancellation";
                    errorRun.CompletedAt = DateTime.UtcNow;
                    errorRun.DurationMs = sw.ElapsedMilliseconds;
                    await errorDb.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error analyzing: {Hash}", trackHash);
                errorMessage = ex.Message;
                _forensicLogger.Error(correlationId, ForensicStage.AnalysisQueue, "Pipeline crashed", trackHash, ex);
                
                // Update run record with error
                using var errorScope = _serviceProvider.CreateScope();
                var errorDb = errorScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var errorRun = await errorDb.AnalysisRuns.FindAsync(runId);
                if (errorRun != null)
                {
                    errorRun.Status = Data.Entities.AnalysisRunStatus.Failed;
                    errorRun.ErrorMessage = ex.Message;
                    errorRun.ErrorStackTrace = ex.StackTrace;
                    errorRun.FailedStage = "Unknown"; 
                    errorRun.CompletedAt = DateTime.UtcNow;
                    errorRun.DurationMs = sw.ElapsedMilliseconds;
                    await errorDb.SaveChangesAsync();
                }

                // Phase 21: Mark track as Failed in DB to prevent infinite retry loops
                using var dbScope = _serviceProvider.CreateScope();
                var db = dbScope.ServiceProvider.GetRequiredService<AppDbContext>();
                
                var entry = await db.LibraryEntries.FirstOrDefaultAsync(e => e.UniqueHash == trackHash, stoppingToken);
                if (entry != null) { entry.AnalysisStatus = AnalysisStatus.Failed; }
                
                var tracks = await db.PlaylistTracks.Where(t => t.TrackUniqueHash == trackHash).ToListAsync(stoppingToken);
                foreach(var t in tracks) { t.AnalysisStatus = AnalysisStatus.Failed; }
                
                await db.SaveChangesAsync(stoppingToken);
            }
            finally
            {
                // If failed, notify immediately. If succeeded, notification happens after batch save?
                // User expects visual update. If we delay "Completed" event until flush, UI stays "Analyzing...".
                // If we send "Completed" now, UI thinks it's done but DB isn't updated.
                // Compromise: Send Completed now so UI unblocks. Data consistency is handled by Flush.
                _queue.NotifyProcessingCompleted(trackHash, analysisSucceeded, errorMessage);
            }
        }
    }

    private async Task FlushBatchAsync(CancellationToken token)
    {
                List<AnalysisResultContext> batchToSave;
                lock (_pendingResults)
                {
                    if (!_pendingResults.Any()) return;
                    batchToSave = _pendingResults.ToList();
                    _pendingResults.Clear();
                }

                _logger.LogInformation("💾 Flushing batch of {Count} analysis results...", batchToSave.Count);

                int retryCount = 0;
                const int maxRetries = 5;
                
                while (retryCount < maxRetries)
                {
                    try
                    {
                        using var dbContext = new AppDbContext();
                        
                        // Process all pending items in one transaction logic
                        foreach (var result in batchToSave)
                        {
                            var trackHash = result.TrackHash;

                            if (result.MusicalResult != null)
                            {
                                var existingFeatures = await dbContext.AudioFeatures.FirstOrDefaultAsync(f => f.TrackUniqueHash == trackHash, token);
                                if (existingFeatures != null) dbContext.AudioFeatures.Remove(existingFeatures);
                                
                                // Fix for SQLite Error 19: Arousal NOT NULL constraint
                                if (result.MusicalResult.Arousal == null) result.MusicalResult.Arousal = 0.5f; 
                                if (result.MusicalResult.Valence == 0f) result.MusicalResult.Valence = 0.5f;
                                
                                dbContext.AudioFeatures.Add(result.MusicalResult);
                            }

                            if (result.TechResult != null)
                            {
                                var existingAnalysis = await dbContext.AudioAnalysis.FirstOrDefaultAsync(a => a.TrackUniqueHash == trackHash, token);
                                if (existingAnalysis != null) dbContext.AudioAnalysis.Remove(existingAnalysis);
                                dbContext.AudioAnalysis.Add(result.TechResult);
                            }

                            var playlistTracks = await dbContext.PlaylistTracks
                                .Include(t => t.TechnicalDetails)
                                .Where(t => t.TrackUniqueHash == trackHash)
                                .ToListAsync(token);

                            foreach (var track in playlistTracks)
                            {
                                ApplyResultsToTrack(track, result);
                                if (track.TechnicalDetails != null && dbContext.Entry(track.TechnicalDetails).State == Microsoft.EntityFrameworkCore.EntityState.Detached)
                                {
                                    dbContext.TechnicalDetails.Add(track.TechnicalDetails);
                                }
                            }
                            
                            var libraryEntry = await dbContext.LibraryEntries.FirstOrDefaultAsync(e => e.UniqueHash == trackHash, token);
                            if (libraryEntry != null)
                            {
                                ApplyResultsToLibraryEntry(libraryEntry, result);
                            }
                        }

                        await dbContext.SaveChangesAsync(token);
                        
                        foreach (var result in batchToSave)
                        {
                            _eventBus.Publish(new TrackMetadataUpdatedEvent(result.TrackHash));
                            
                            // Phase 1: Structural Intelligence - Run after DB commit
                            try
                            {
                                await _phraseDetector.DetectPhrasesAsync(result.TrackHash);
                            }
                            catch (Exception ex)
                            {
                                 _logger.LogWarning("Phrase detection failed for {Hash}: {Msg}", result.TrackHash, ex.Message);
                            }

                            // Race Condition Check: Publish Completion events HERE, after DB commit
                            _eventBus.Publish(new TrackAnalysisCompletedEvent(result.TrackHash, true) { DatabaseId = result.DatabaseId });
                            _eventBus.Publish(new AnalysisCompletedEvent(result.TrackHash, true));
                        }

                        _lastBatchSave = DateTime.UtcNow;
                        _logger.LogInformation("✅ Batch save completed.");
                        return;
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 5) // SQLITE_BUSY
                    {
                        retryCount++;
                        _logger.LogWarning("⚠️ Database locked during analysis batch save. Retry {Count}/{Max}...", retryCount, maxRetries);
                        await Task.Delay(500 * retryCount, token); // Exponential backoff
                    }
                    catch (Exception ex)
                    {
                         _logger.LogError(ex, "❌ Failed to save analysis batch! Data may be lost for {Count} tracks.", batchToSave.Count);
                         
                         // Fix for Ghost Items: Notify failure so UI can clean up
                         foreach (var result in batchToSave)
                         {
                             _eventBus.Publish(new TrackAnalysisFailedEvent(result.TrackHash, $"Batch Save Failed: {ex.Message}"));
                         }
 
                         return;
                    }
                }
    }

    private void ApplyResultsToTrack(PlaylistTrackEntity track, AnalysisResultContext result)
    {
        // Fix: Only mark as enriched if we actually got musical results
        if (result.MusicalResult != null)
        {
            track.IsEnriched = true;
            track.AnalysisStatus = AnalysisStatus.Completed; // Phase 21
            track.BPM = result.MusicalResult.Bpm;
            track.MusicalKey = result.MusicalResult.Key + (result.MusicalResult.Scale == "minor" ? "m" : "");
            track.Energy = result.MusicalResult.Energy;
            track.Danceability = result.MusicalResult.Danceability;
            track.Valence = result.MusicalResult.Valence;
            track.MoodTag = result.MusicalResult.MoodTag;
            track.InstrumentalProbability = result.MusicalResult.InstrumentalProbability;
            track.DetectedSubGenre = result.MusicalResult.DetectedSubGenre;
            track.PrimaryGenre = result.MusicalResult.ElectronicSubgenre;
            
            // Phase 3.5: Vocal Metrics
            track.VocalType = result.MusicalResult.DetectedVocalType;
            track.VocalIntensity = result.MusicalResult.VocalIntensity;
            track.VocalStartSeconds = result.MusicalResult.VocalStartSeconds;
            track.VocalEndSeconds = result.MusicalResult.VocalEndSeconds;
        }
        else if (track.AnalysisStatus == AnalysisStatus.None || track.AnalysisStatus == AnalysisStatus.Pending)
        {
             // If we didn't get musical results, it might be a partial or error state that we're still saving
             // track.AnalysisStatus = AnalysisStatus.Failed; // Handled by Flush caller if batch fails?
             // Actually, FlushBatchAsync handles SUCCESSFUL results. Failed results are notified via NotifyProcessingCompleted(false).
             // But we should mark it as Failed in DB too if we know.
        }

        if (result.WaveformData != null)
        {
            if (track.TechnicalDetails == null)
            {
                track.TechnicalDetails = new TrackTechnicalEntity 
                { 
                    Id = Guid.NewGuid(),
                    PlaylistTrackId = track.Id 
                };
            }
            
            track.TechnicalDetails.WaveformData = result.WaveformData.PeakData;
            track.TechnicalDetails.RmsData = result.WaveformData.RmsData;
            track.TechnicalDetails.LowData = result.WaveformData.LowData;
            track.TechnicalDetails.MidData = result.WaveformData.MidData;
            track.TechnicalDetails.HighData = result.WaveformData.HighData;
            
            // Phase 10: Persist Cues
            if (result.Cues != null && result.Cues.Any())
            {
                var json = System.Text.Json.JsonSerializer.Serialize(result.Cues);
                track.CuePointsJson = json;
                track.TechnicalDetails.CuePointsJson = json;
                track.IsPrepared = result.Cues.Any(c => c.Confidence > 0.8);
            }
        }


        if (result.TechResult != null)
        {
            track.Bitrate = result.TechResult.Bitrate;
            track.QualityConfidence = result.TechResult.QualityConfidence;
            track.FrequencyCutoff = result.TechResult.FrequencyCutoff;
            track.IsTrustworthy = !result.TechResult.IsUpscaled;
            track.SpectralHash = result.TechResult.SpectralHash;
            
            // Phase 17: Technical Audio Analysis
            track.Loudness = result.TechResult.LoudnessLufs;
            track.TruePeak = result.TechResult.TruePeakDb;
            track.DynamicRange = result.TechResult.DynamicRange;
            
            // --- Phase 9: Spectral Honesty Check ---
            // If the file claims to be high-quality (320k or Lossless) but the High band (Blue) 
            // is near-zero, it's a "silent upscale" or a bad transcode.
            bool isHighQualityClaim = track.Bitrate >= 310 || (result.FilePath != null && result.FilePath.EndsWith(".flac", StringComparison.OrdinalIgnoreCase));
            bool hasHighFreqEnergy = true;
            
            if (result.WaveformData != null && result.WaveformData.HighData.Length > 0)
            {
                double avgHigh = result.WaveformData.HighData.Average(b => (int)b);
                // Threshold of 5 out of 255 is very low; anything below this is effectively a brick wall at 2.5kHz
                if (avgHigh < 5.0 && isHighQualityClaim)
                {
                    hasHighFreqEnergy = false;
                    _logger.LogWarning("🚩 Spectral Honesty Failure: {Track} has near-zero high frequency energy despite {Bitrate}kbps claim.", track.Title, track.Bitrate);
                }
            }

            if (result.TechResult.IsUpscaled || !hasHighFreqEnergy)
            {
                track.Integrity = IntegrityLevel.Suspicious;
                track.IsTrustworthy = false;
            }
            else if (track.QualityConfidence > 0.8)
            {
                track.Integrity = IntegrityLevel.Verified;
                track.IsTrustworthy = true;
            }
            else
            {
                track.Integrity = IntegrityLevel.None;
            }
        }
    }

    private void ApplyResultsToLibraryEntry(LibraryEntryEntity entry, AnalysisResultContext result)
    {
        if (result.MusicalResult != null)
        {
            entry.IsEnriched = true;
            entry.AnalysisStatus = AnalysisStatus.Completed; // Phase 21
            entry.BPM = result.MusicalResult.Bpm;
            // Fix: Map properties directly from result
            entry.MusicalKey = result.MusicalResult.Key + (result.MusicalResult.Scale == "minor" ? "m" : "");
            entry.Energy = result.MusicalResult.Energy;
            entry.Danceability = result.MusicalResult.Danceability;
            entry.Valence = result.MusicalResult.Valence;
            // Map missing AI fields
            entry.DetectedSubGenre = result.MusicalResult.DetectedSubGenre;
            entry.PrimaryGenre = result.MusicalResult.ElectronicSubgenre;
            entry.InstrumentalProbability = result.MusicalResult.InstrumentalProbability;

            // Phase 3.5: Vocal Metrics
            entry.VocalType = result.MusicalResult.DetectedVocalType;
            entry.VocalIntensity = result.MusicalResult.VocalIntensity;
            entry.VocalStartSeconds = result.MusicalResult.VocalStartSeconds;
            entry.VocalEndSeconds = result.MusicalResult.VocalEndSeconds;
        }

        if (result.WaveformData != null)
        {
            entry.WaveformData = result.WaveformData.PeakData;
            entry.RmsData = result.WaveformData.RmsData;
            entry.LowData = result.WaveformData.LowData;
            entry.MidData = result.WaveformData.MidData;
            entry.HighData = result.WaveformData.HighData;
            
            // Phase 10: Persist Cues
            if (result.Cues != null && result.Cues.Any())
            {
                var json = System.Text.Json.JsonSerializer.Serialize(result.Cues);
                entry.CuePointsJson = json;
                entry.IsPrepared = result.Cues.Any(c => c.Confidence > 0.8);
            }
        }

        if (result.TechResult != null)
        {
            entry.Bitrate = result.TechResult.Bitrate;
            
            // Phase 9: Integrity Logic
            bool isHighQualityClaim = entry.Bitrate >= 310 || (result.FilePath != null && result.FilePath.EndsWith(".flac", StringComparison.OrdinalIgnoreCase));
            bool hasHighFreqEnergy = true;
            
            if (result.WaveformData != null && result.WaveformData.HighData.Length > 0)
            {
                double avgHigh = result.WaveformData.HighData.Average(b => (int)b);
                if (avgHigh < 5.0 && isHighQualityClaim) hasHighFreqEnergy = false;
            }

            if (result.TechResult.IsUpscaled || !hasHighFreqEnergy)
            {
                entry.Integrity = IntegrityLevel.Suspicious;
            }
            else
            {
                entry.Integrity = IntegrityLevel.Verified;
            }
            
            // Phase 17: Technical Audio Analysis
            entry.Loudness = result.TechResult.LoudnessLufs;
            entry.TruePeak = result.TechResult.TruePeakDb;
            entry.DynamicRange = result.TechResult.DynamicRange;
        }
    }

    private void PublishThrottled(AnalysisProgressEvent evt)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastProgressReport).TotalMilliseconds > 250 || evt.ProgressPercent >= 100)
        {
            _eventBus.Publish(evt);
            _lastProgressReport = now;
        }
    }

    private class AnalysisResultContext
    {
        public string InteractionId { get; set; } = Guid.NewGuid().ToString(); // Internal tracking
        public string CorrelationId { get; set; } = string.Empty;
        public string TrackHash { get; set; } = string.Empty;
        public Guid? DatabaseId { get; set; } // Phase 21: Database visibility
        public string FilePath { get; set; } = string.Empty;
        public WaveformAnalysisData? WaveformData { get; set; } // Changed from WaveformData
        public AudioAnalysisEntity? TechResult { get; set; }
        public AudioFeaturesEntity? MusicalResult { get; set; }
        public List<OrbitCue>? Cues { get; set; } // Phase 10
    }
}
