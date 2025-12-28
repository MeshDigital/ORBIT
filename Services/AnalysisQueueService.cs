using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using SLSKDONET.Data; // For AppDbContext
using SLSKDONET.Models; // For Events

namespace SLSKDONET.Services;

public class AnalysisQueueService : INotifyPropertyChanged
{
    private readonly Channel<AnalysisRequest> _channel;
    private readonly IEventBus _eventBus;
    private int _queuedCount = 0;
    private int _processedCount = 0;
    private string? _currentTrackHash = null;
    private bool _isPaused = false;

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

    public void NotifyProcessingStarted(string trackHash)
    {
        CurrentTrackHash = trackHash;
    }

    public void NotifyProcessingCompleted(string trackHash, bool success, string? error = null)
    {
        Interlocked.Increment(ref _processedCount);
        Interlocked.Decrement(ref _queuedCount);
        CurrentTrackHash = null;
        
        OnPropertyChanged(nameof(QueuedCount));
        OnPropertyChanged(nameof(ProcessedCount));
        
        // Publish completion event for UI
        _eventBus.Publish(new AnalysisCompletedEvent(trackHash, success, error));
        PublishStatusEvent();
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public ChannelReader<AnalysisRequest> Reader => _channel.Reader;
}

public record AnalysisRequest(string FilePath, string TrackHash);

public class AnalysisWorker : BackgroundService
{
    private readonly AnalysisQueueService _queue;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AnalysisWorker> _logger;

    public AnalysisWorker(AnalysisQueueService queue, IServiceProvider serviceProvider, ILogger<AnalysisWorker> logger)
    {
        _queue = queue;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🧠 Musical Brain (AnalysisWorker) started.");

        await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            // Check pause state
            while (_queue.IsPaused && !stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(500, stoppingToken);
            }

            string? trackHash = request.TrackHash;
            bool analysisSucceeded = false;
            string? errorMessage = null;

            try
            {
                // Notify start (updates CurrentTrackHash, publishes event)
                _queue.NotifyProcessingStarted(trackHash);

                using var scope = _serviceProvider.CreateScope();
                var analyzer = scope.ServiceProvider.GetRequiredService<IAudioIntelligenceService>();
                var dbContext = new AppDbContext();

                _logger.LogInformation("🧠 Analyzing: {Hash}", trackHash);
                
                // Enhancement #4: Stuck File Watchdog - 45s timeout per track
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);
                
                // Note: Interface doesn't support CancellationToken yet, but timeout helps detect hangs
                var analysisTask = analyzer.AnalyzeTrackAsync(request.FilePath, trackHash);
                var result = await analysisTask.WaitAsync(linkedCts.Token);
                
                if (result != null)
                {
                    dbContext.AudioFeatures.Add(result);
                    await dbContext.SaveChangesAsync(stoppingToken);
                    _logger.LogInformation("✅ Musical Intel saved for {Hash}", trackHash);
                    analysisSucceeded = true;
                }
                else
                {
                    _logger.LogWarning("⚠️ Analysis returned null for {Hash}", trackHash);
                    errorMessage = "Analysis returned null";
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Timeout occurred (not app shutdown)
                _logger.LogError("⏱ Track analysis timed out after 45s - skipping: {Hash}", trackHash);
                errorMessage = "Analysis timed out (45s)";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error processing analysis queue item: {Hash}", trackHash);
                errorMessage = ex.Message;
            }
            finally
            {
                // Enhancement #1: ALWAYS decrement counter to prevent desync
                _queue.NotifyProcessingCompleted(trackHash, analysisSucceeded, errorMessage);
            }
        }
    }
}
