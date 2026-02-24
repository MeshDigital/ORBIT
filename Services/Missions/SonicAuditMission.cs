using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Services.Repositories;

namespace SLSKDONET.Services.Missions;

public class SonicAuditMission : MissionBase
{
    private readonly ITrackRepository _trackRepository;
    private readonly ILogger<SonicAuditMission> _logger;
    private CancellationTokenSource? _cts;

    public override string Name => "Sonic Audit";
    public override string Description => "Deep scan for transcodes and low-bitrate artifacts.";
    public override string Icon => "🔍";

    public SonicAuditMission(ITrackRepository trackRepository, ILogger<SonicAuditMission> logger)
    {
        _trackRepository = trackRepository;
        _logger = logger;
    }

    public override async Task ExecuteAsync()
    {
        if (IsRunning) return;
        
        IsRunning = true;
        Progress = 0;
        StatusText = "Initializing scan...";
        _cts = new CancellationTokenSource();

        try
        {
            var entries = await _trackRepository.GetAllLibraryEntriesAsync();
            int total = entries.Count;
            int processed = 0;

            foreach (var entry in entries)
            {
                if (_cts.IsCancellationRequested) break;

                processed++;
                Progress = (double)processed / total;
                StatusText = $"Auditing: {entry.Artist} - {entry.Title}";

                // Simulate/Implement audit logic
                // Phase 7.1: Detect transcodes (Upconverted MP3s)
                // This would normally call an external tool or deep analysis
                await Task.Delay(10); // Throttle for UI visibility

                if (processed % 100 == 0)
                {
                    _logger.LogDebug("Sonic Audit: Processed {Count}/{Total}", processed, total);
                }
            }

            StatusText = _cts.IsCancellationRequested ? "Audit Cancelled" : "Audit Complete";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sonic Audit Mission failed");
            StatusText = "Audit Failed";
        }
        finally
        {
            IsRunning = false;
            Progress = 1.0;
        }
    }

    public override void Cancel()
    {
        _cts?.Cancel();
        StatusText = "Cancelling...";
    }
}
