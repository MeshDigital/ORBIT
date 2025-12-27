using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using SLSKDONET.Data; // For AppDbContext

namespace SLSKDONET.Services;

public class AnalysisQueueService
{
    private readonly Channel<AnalysisRequest> _channel;

    public AnalysisQueueService()
    {
        // Unbounded channel to prevent blocking producers (downloads)
        _channel = Channel.CreateUnbounded<AnalysisRequest>();
    }

    public void QueueAnalysis(string filePath, string trackHash)
    {
        _channel.Writer.TryWrite(new AnalysisRequest(filePath, trackHash));
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
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var analyzer = scope.ServiceProvider.GetRequiredService<IAudioIntelligenceService>();
                var dbContext = new AppDbContext(); // Manual instantiation for simple worker scope

                _logger.LogInformation("🧠 Analyzing: {Hash}", request.TrackHash);
                
                var result = await analyzer.AnalyzeTrackAsync(request.FilePath, request.TrackHash);
                
                if (result != null)
                {
                    dbContext.AudioFeatures.Add(result);
                    await dbContext.SaveChangesAsync(stoppingToken);
                    _logger.LogInformation("✅ Musical Intel saved for {Hash}", request.TrackHash);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing analysis queue item.");
            }
        }
    }
}
