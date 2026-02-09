using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services.Export;

namespace SLSKDONET.Services.Testing;

/// <summary>
/// Service to coordinate the "10k Marathon" scalability stress test.
/// </summary>
public class ScalabilityStressTestService
{
    private readonly ILogger<ScalabilityStressTestService> _logger;
    private readonly MockLibraryGenerator _generator;
    private readonly ILibraryService _libraryService;
    private readonly RekordboxService _rekordboxService;

    public ScalabilityStressTestService(
        ILogger<ScalabilityStressTestService> logger,
        MockLibraryGenerator generator,
        ILibraryService libraryService,
        RekordboxService rekordboxService)
    {
        _logger = logger;
        _generator = generator;
        _libraryService = libraryService;
        _rekordboxService = rekordboxService;
    }

    public event Action<string>? StatusUpdated;
    public event Action<string>? PhaseChanged;
    public event Action<double>? FpsUpdated;
    public event Action<string>? LogEntry;

    public async Task<StressTestMetrics> RunAsync(int trackCount = 10000, CancellationToken ct = default)
    {
        var metrics = new StressTestMetrics();
        var frameTimes = new List<double>();
        var cpuReadings = new List<double>();
        var sw = Stopwatch.StartNew();

        Guid? mockProjectId = null;

        try
        {
            LogEntry?.Invoke($"🚀 Starting 10K Marathon Scalability Test ({trackCount} tracks)...");
            StatusUpdated?.Invoke("Initializing...");

            // PHASE 1: Redline Injection
            PhaseChanged?.Invoke("Phase 1: Redline Injection");
            LogEntry?.Invoke("📦 [Phase 1] Injecting mock tracks into database...");
            var injectionSw = Stopwatch.StartNew();
            mockProjectId = await _generator.GenerateMockLibraryAsync(trackCount, ct);
            injectionSw.Stop();
            LogEntry?.Invoke($"   ✅ Injected {trackCount} tracks in {injectionSw.Elapsed.TotalSeconds:F1}s.");

            // PHASE 2: Indexing Surge
            PhaseChanged?.Invoke("Phase 2: Indexing Surge");
            LogEntry?.Invoke("🔍 [Phase 2] Measuring FTS search performance on 10k dataset...");
            var searchSw = Stopwatch.StartNew();
            var results = await _libraryService.SearchLibraryEntriesWithStatusAsync("Stress", 50);
            searchSw.Stop();
            LogEntry?.Invoke($"   ✅ Initial search for 'Stress' took {searchSw.Elapsed.TotalMilliseconds:F0}ms. Found {results.Count} results.");

            // PHASE 3: Search Fluidity (Simulated Virtualization Load)
            PhaseChanged?.Invoke("Phase 3: Search Fluidity");
            LogEntry?.Invoke("📽️ [Phase 3] Simulating rapid scrolling and search filtering...");
            await RunSearchFluidityTest(mockProjectId.Value, frameTimes, cpuReadings, ct);

            // PHASE 4: Export Marathon
            PhaseChanged?.Invoke("Phase 4: Export Marathon");
            LogEntry?.Invoke("📜 [Phase 4] Exporting 10k tracks to Rekordbox XML...");
            var exportSw = Stopwatch.StartNew();
            // We need to simulate the export flow. 
            // Mocking the export path
            var exportPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "StressTestExport.xml");
            var job = await _libraryService.FindPlaylistJobAsync(mockProjectId.Value);
            if (job != null)
            {
                await _rekordboxService.ExportPlaylistAsync(job, exportPath);
                exportSw.Stop();
                LogEntry?.Invoke($"   ✅ Exported {trackCount} tracks in {exportSw.Elapsed.TotalSeconds:F1}s.");
            }
            else
            {
                LogEntry?.Invoke("   ❌ Failed to load mock project for export.");
            }

            sw.Stop();
            CalculateMetrics(metrics, frameTimes, cpuReadings, sw.Elapsed);
            
            LogEntry?.Invoke(metrics.Verdict);
            StatusUpdated?.Invoke(metrics.Passed ? "✅ Test Passed" : "❌ Test Failed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scalability stress test crashed");
            LogEntry?.Invoke($"❌ CRASH: {ex.Message}");
            StatusUpdated?.Invoke("Test crashed");
        }
        finally
        {
            // Optional: Should we cleanup or keep the data for the user to see?
            // The user might want to see the 10k tracks in the UI.
            // LogEntry?.Invoke("🧹 Cleaning up mock data...");
            // if (mockProjectId.HasValue) await _generator.CleanupMockDataAsync(mockProjectId.Value);
        }

        return metrics;
    }

    private async Task RunSearchFluidityTest(
        Guid projectId,
        List<double> frameTimes,
        List<double> cpuReadings,
        CancellationToken ct)
    {
        var phaseSw = Stopwatch.StartNew();
        var frameTimer = Stopwatch.StartNew();

        while (phaseSw.Elapsed < TimeSpan.FromSeconds(5) && !ct.IsCancellationRequested)
        {
            // Simulate 5 rapid search queries per second
            if (phaseSw.Elapsed.TotalMilliseconds % 200 < 20)
            {
                var querySuffix = (int)(phaseSw.Elapsed.TotalSeconds * 10);
                await _libraryService.GetPagedPlaylistTracksAsync(projectId, (querySuffix % 100) * 10, 50, "Stress");
            }

            // Measure frame time
            double frameTime = frameTimer.Elapsed.TotalMilliseconds;
            if (frameTime > 0)
            {
                frameTimes.Add(frameTime);
                FpsUpdated?.Invoke(1000.0 / frameTime);
            }
            frameTimer.Restart();

            await Task.Delay(16, ct); // ~60 FPS target
        }
    }

    private void CalculateMetrics(
        StressTestMetrics metrics,
        List<double> frameTimes,
        List<double> cpuReadings,
        TimeSpan duration)
    {
        if (frameTimes.Count > 0)
        {
            double avgFrameTime = frameTimes.Average();
            metrics.AverageFps = 1000.0 / avgFrameTime;
            metrics.MinFps = 1000.0 / frameTimes.Max();
            metrics.MaxFps = 1000.0 / frameTimes.Min();
            metrics.FrameCount = frameTimes.Count;
            metrics.DroppedFrames = frameTimes.Count(ft => ft > 20); // >20ms = dropped

            double variance = frameTimes.Select(ft => Math.Pow(ft - avgFrameTime, 2)).Average();
            metrics.JitterMs = Math.Sqrt(variance);
        }

        metrics.Duration = duration;
        metrics.PeakMemoryMb = GC.GetTotalMemory(false) / (1024 * 1024);
        
        // Custom PASS criteria for scalability can be adjusted if needed.
        // The default in StressTestMetrics is AvgFps >= 55.
    }
}
