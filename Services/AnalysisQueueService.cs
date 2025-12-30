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

namespace SLSKDONET.Services;

public class AnalysisQueueService : INotifyPropertyChanged
{
    private readonly Channel<AnalysisRequest> _channel;
    private readonly IEventBus _eventBus;
    private int _queuedCount = 0;
    private int _processedCount = 0;
    private string? _currentTrackHash = null;
    private bool _isPaused = false;
    
    // Thread tracking for Mission Control dashboard
    private readonly ConcurrentDictionary<int, ActiveThreadInfo> _activeThreads = new();
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

    public AnalysisQueueService(IEventBus eventBus)
    {
        _eventBus = eventBus;
        // Unbounded channel to prevent blocking producers (downloads)
        _channel = Channel.CreateUnbounded<AnalysisRequest>();
    }

    public void QueueAnalysis(string filePath, string trackHash)
    {
        _channel.Writer.TryWrite(new AnalysisRequest(filePath, trackHash));
        Interlocked.Increment(ref _queuedCount);
        OnPropertyChanged(nameof(QueuedCount));
        PublishStatusEvent();
    }

    public void NotifyProcessingStarted(string trackHash, string fileName)
    {
        CurrentTrackHash = trackHash;
        _eventBus.Publish(new TrackAnalysisStartedEvent(trackHash, fileName));
    }

    public void NotifyProcessingCompleted(string trackHash, bool success, string? error = null)
    {
        Interlocked.Increment(ref _processedCount);
        Interlocked.Decrement(ref _queuedCount);
        CurrentTrackHash = null;
        
        OnPropertyChanged(nameof(QueuedCount));
        OnPropertyChanged(nameof(ProcessedCount));
        PublishStatusEvent();
        
        _eventBus.Publish(new TrackAnalysisCompletedEvent(trackHash, success, error));
        // Publish legacy completion event for UI compatibility
        _eventBus.Publish(new AnalysisCompletedEvent(trackHash, success, error));
        
        if (!success && error != null)
        {
             _eventBus.Publish(new TrackAnalysisFailedEvent(trackHash, error));
        }
    }

    // Album Priority: Queue entire album for immediate analysis
    public int QueueAlbumWithPriority(System.Collections.Generic.List<SLSKDONET.Models.PlaylistTrack> tracks)
    {
        var count = 0;
        foreach (var track in tracks)
        {
            if (!string.IsNullOrEmpty(track.ResolvedFilePath) && !string.IsNullOrEmpty(track.TrackUniqueHash))
            {
                QueueAnalysis(track.ResolvedFilePath, track.TrackUniqueHash);
                count++;
            }
        }
        
        System.Diagnostics.Debug.WriteLine($"Queued {count} tracks from album for priority analysis");
        return count;
    }

    private void PublishStatusEvent()
    {
        _eventBus.Publish(new AnalysisQueueStatusChangedEvent(
            QueuedCount,
            ProcessedCount,
            CurrentTrackHash,
            IsPaused
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
    public void UpdateThreadStatus(int threadId, string trackName, string status, double progress = 0)
    {
        var info = new ActiveThreadInfo
        {
            ThreadId = threadId,
            CurrentTrack = string.IsNullOrEmpty(trackName) ? "-" : System.IO.Path.GetFileName(trackName),
            Status = status,
            Progress = progress,
            StartTime = status == "Idle" ? null : (DateTime?)DateTime.Now
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
            }
            else
            {
                // Add new thread
                ActiveThreads.Add(info);
            }
        });
    }

    public ChannelReader<AnalysisRequest> Reader => _channel.Reader;
}

public record AnalysisRequest(string FilePath, string TrackHash);

public class AnalysisWorker : BackgroundService
{
    private readonly AnalysisQueueService _queue;
    private readonly IServiceProvider _serviceProvider;
    private readonly IEventBus _eventBus;
    private readonly ILogger<AnalysisWorker> _logger;
    private readonly int _maxConcurrentAnalyses;
    private readonly SemaphoreSlim _concurrencyLimiter;
    
    // Batching & Throttling State
    private DateTime _lastProgressReport = DateTime.MinValue;
    private readonly List<AnalysisResultContext> _pendingResults = new();
    private DateTime _lastBatchSave = DateTime.UtcNow;
    private const int BatchSize = 10;
    private readonly TimeSpan BatchTimeout = TimeSpan.FromSeconds(5);

    public AnalysisWorker(AnalysisQueueService queue, IServiceProvider serviceProvider, IEventBus eventBus, ILogger<AnalysisWorker> logger, Configuration.AppConfig config)
    {
        _queue = queue;
        _serviceProvider = serviceProvider;
        _eventBus = eventBus;
        _logger = logger;
        
        // Determine optimal parallelism
        int configuredValue = config.MaxConcurrentAnalyses;
        if (configuredValue == 0)
        {
            _maxConcurrentAnalyses = SystemInfoHelper.GetOptimalParallelism();
            _logger.LogInformation("🧠 Analysis parallelism: AUTO-DETECTED {Threads} threads ({SystemInfo})",
                _maxConcurrentAnalyses, SystemInfoHelper.GetSystemDescription());
        }
        else if (configuredValue < 0)
        {
            _maxConcurrentAnalyses = 1;
            _logger.LogWarning("Invalid MaxConcurrentAnalyses: {Value}. Defaulting to 1.", configuredValue);
        }
        else
        {
            _maxConcurrentAnalyses = Math.Min(configuredValue, Environment.ProcessorCount * 2);
            _logger.LogInformation("🧠 Analysis parallelism: CONFIGURED {Threads} threads", _maxConcurrentAnalyses);
        }
        
        _concurrencyLimiter = new SemaphoreSlim(_maxConcurrentAnalyses, _maxConcurrentAnalyses);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🧠 Musical Brain (AnalysisWorker) started with {Threads} parallel threads.", _maxConcurrentAnalyses);
        
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

            // 2. Collect batch of requests (up to parallelism limit)
            var batch = new List<AnalysisRequest>();
            try
            {
                // Try to read up to _maxConcurrentAnalyses items without blocking too long
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(1000); // 1 second timeout
                
                while (batch.Count < _maxConcurrentAnalyses && _queue.Reader.TryRead(out var request))
                {
                    batch.Add(request);
                }
                
                // If no items ready, wait for at least one
                if (batch.Count == 0)
                {
                    try
                    {
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

            // 3. Process batch in parallel
            var processingTasks = batch.Select(async (request, index) =>
            {
                var threadId = index; // Use index as thread ID for this batch
                
                await _concurrencyLimiter.WaitAsync(stoppingToken);
                try
                {
                    // Update thread status: Processing
                    _queue.UpdateThreadStatus(threadId, request.FilePath, "Processing", 0);
                    
                    await ProcessRequestAsync(request, stoppingToken);
                    Interlocked.Increment(ref processedInBatch);
                    
                    // Update thread status: Complete
                    _queue.UpdateThreadStatus(threadId, request.FilePath, "Complete", 100);
                    
                    // Log progress every 10 tracks
                    if (processedInBatch % 10 == 0)
                    {
                        LogBatchProgress(processedInBatch, batchStartTime);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Thread {ThreadId} failed processing track", threadId);
                    _queue.UpdateThreadStatus(threadId, request.FilePath, "Error", 0);
                }
                finally
                {
                    // Return thread to idle
                    _queue.UpdateThreadStatus(threadId, string.Empty, "Idle", 0);
                    _concurrencyLimiter.Release();
                }
            }).ToList();

            await Task.WhenAll(processingTasks);
        }

        // Final Flush
        if (_pendingResults.Any()) await FlushBatchAsync(CancellationToken.None);
        
        _logger.LogInformation("🧠 Musical Brain (AnalysisWorker) stopped. Processed {Total} tracks.", processedInBatch);
    }
    
    private void LogBatchProgress(int processed, DateTime startTime)
    {
        var elapsed = DateTime.UtcNow - startTime;
        var tracksPerMinute = elapsed.TotalMinutes > 0 ? processed / elapsed.TotalMinutes : 0;
        
        _logger.LogInformation(
            "📈 Analysis progress: {Processed} tracks in {Elapsed} ({Rate:F1}/min) - {Threads} threads active",
            processed,
            elapsed.ToString(@"hh\:mm\:ss"),
            tracksPerMinute,
            _maxConcurrentAnalyses
        );
    }

    private async Task ProcessRequestAsync(AnalysisRequest request, CancellationToken stoppingToken)
    {
        string trackHash = request.TrackHash;
        bool analysisSucceeded = false;
        string? errorMessage = null;
        
        // Context to hold results for batching
        var resultContext = new AnalysisResultContext { TrackHash = trackHash, FilePath = request.FilePath };

        try
        {
            _queue.NotifyProcessingStarted(trackHash, request.FilePath);

            using var scope = _serviceProvider.CreateScope();
            var essentiaAnalyzer = scope.ServiceProvider.GetRequiredService<IAudioIntelligenceService>();
            var audioAnalyzer = scope.ServiceProvider.GetRequiredService<IAudioAnalysisService>();
            var waveformAnalyzer = scope.ServiceProvider.GetRequiredService<WaveformAnalysisService>();

            _logger.LogInformation("🧠 Analyzing: {Hash}", trackHash);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);

            // 1. Generate Waveform
            PublishThrottled(new AnalysisProgressEvent(trackHash, "Generating waveform...", 10));
            // Offload CPU bound work
            resultContext.WaveformData = await Task.Run(() => waveformAnalyzer.GenerateWaveformAsync(request.FilePath, linkedCts.Token), linkedCts.Token);

            // 2. Musical Analysis
            PublishThrottled(new AnalysisProgressEvent(trackHash, "Analyzing musical features...", 40));
            resultContext.MusicalResult = await Task.Run(() => essentiaAnalyzer.AnalyzeTrackAsync(request.FilePath, trackHash, linkedCts.Token), linkedCts.Token);

            // 3. Technical Analysis
            PublishThrottled(new AnalysisProgressEvent(trackHash, "Running technical analysis...", 70));
            resultContext.TechResult = await Task.Run(() => audioAnalyzer.AnalyzeFileAsync(request.FilePath, trackHash, linkedCts.Token), linkedCts.Token);

            PublishThrottled(new AnalysisProgressEvent(trackHash, "Queued for save...", 90));
            
            // Add to batch buffer
            _pendingResults.Add(resultContext);
            analysisSucceeded = true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogError("⏱ Track analysis timed out or cancelled: {Hash}", trackHash);
            errorMessage = "Analysis timed out";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error analyzing: {Hash}", trackHash);
            errorMessage = ex.Message;
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

    private async Task FlushBatchAsync(CancellationToken token)
    {
        if (!_pendingResults.Any()) return;

        var batchCount = _pendingResults.Count;
        _logger.LogInformation("💾 Flushing batch of {Count} analysis results...", batchCount);

        try
        {
            using var dbContext = new AppDbContext();
            
            // Process all pending items in one transaction logic
            foreach (var result in _pendingResults)
            {
                var trackHash = result.TrackHash;

                // Feature Tables
                if (result.MusicalResult != null)
                {
                    // Basic Upsert logic (Remove old, Add new) - optimized for batch?
                    // EF Core doesn't support bulk upsert natively well without extensions, doing per-item check is costly for large batches.
                    // But for 10 items it's fine.
                    var existingFeatures = await dbContext.AudioFeatures.FirstOrDefaultAsync(f => f.TrackUniqueHash == trackHash, token);
                    if (existingFeatures != null) dbContext.AudioFeatures.Remove(existingFeatures);
                    dbContext.AudioFeatures.Add(result.MusicalResult);
                }

                if (result.TechResult != null)
                {
                    var existingAnalysis = await dbContext.AudioAnalysis.FirstOrDefaultAsync(a => a.TrackUniqueHash == trackHash, token);
                    if (existingAnalysis != null) dbContext.AudioAnalysis.Remove(existingAnalysis);
                    dbContext.AudioAnalysis.Add(result.TechResult);
                }

                // Update PlaylistTracks
                 var playlistTracks = await dbContext.PlaylistTracks
                    .Where(t => t.TrackUniqueHash == trackHash)
                    .ToListAsync(token);

                foreach (var track in playlistTracks)
                {
                    ApplyResultsToTrack(track, result);
                }
                
                // Update LibraryEntries
                var libraryEntry = await dbContext.LibraryEntries.FirstOrDefaultAsync(e => e.UniqueHash == trackHash, token);
                if (libraryEntry != null)
                {
                    ApplyResultsToLibraryEntry(libraryEntry, result);
                }
            }

            await dbContext.SaveChangesAsync(token);
            _lastBatchSave = DateTime.UtcNow;
            _pendingResults.Clear();
            _logger.LogInformation("✅ Batch save completed.");
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "❌ Failed to save analysis batch!");
             // Potential data loss if batch fails. In robust system we'd retry or dead-letter.
             // For now, clear to prevent infinite loop.
             _pendingResults.Clear(); 
        }
    }

    private void ApplyResultsToTrack(PlaylistTrackEntity track, AnalysisResultContext result)
    {
        track.IsEnriched = true;
        if (result.WaveformData != null)
        {
            track.WaveformData = result.WaveformData.PeakData;
            track.RmsData = result.WaveformData.RmsData;
        }
        
        if (result.MusicalResult != null)
        {
            track.BPM = result.MusicalResult.Bpm;
            track.MusicalKey = result.MusicalResult.Key + (result.MusicalResult.Scale == "minor" ? "m" : "");
            track.Energy = result.MusicalResult.Energy;
            track.Danceability = result.MusicalResult.Danceability;
        }

        if (result.TechResult != null)
        {
            track.Bitrate = result.TechResult.Bitrate;
            track.QualityConfidence = result.TechResult.QualityConfidence;
            track.FrequencyCutoff = result.TechResult.FrequencyCutoff;
            track.IsTrustworthy = !result.TechResult.IsUpscaled;
            track.SpectralHash = result.TechResult.SpectralHash;
        }
    }

    private void ApplyResultsToLibraryEntry(LibraryEntryEntity entry, AnalysisResultContext result)
    {
        entry.IsEnriched = true;
        if (result.WaveformData != null)
        {
            entry.WaveformData = result.WaveformData.PeakData;
            entry.RmsData = result.WaveformData.RmsData;
        }
        if (result.MusicalResult != null)
        {
            entry.BPM = result.MusicalResult.Bpm;
            entry.MusicalKey = result.MusicalResult.Key + (result.MusicalResult.Scale == "minor" ? "m" : "");
            entry.Energy = result.MusicalResult.Energy;
            entry.Danceability = result.MusicalResult.Danceability;
        }
        if (result.TechResult != null)
        {
            entry.Bitrate = result.TechResult.Bitrate;
            entry.Integrity = result.TechResult.IsUpscaled ? IntegrityLevel.Suspicious : IntegrityLevel.Verified;
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

    // Helper class for batch context
    private class AnalysisResultContext
    {
        public string TrackHash { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public WaveformAnalysisData? WaveformData { get; set; } // Changed from WaveformData
        public AudioAnalysisEntity? TechResult { get; set; }
        public AudioFeaturesEntity? MusicalResult { get; set; }
    }
}
