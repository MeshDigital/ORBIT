using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services;

/// <summary>
/// Result of a sonic integrity analysis.
/// </summary>
public class SonicAnalysisResult
{
    public double QualityConfidence { get; set; } // 0.0 - 1.0
    public int FrequencyCutoff { get; set; } // Hz
    public string SpectralHash { get; set; } = string.Empty;
    public bool IsTrustworthy { get; set; }
    public string? Details { get; set; }
}

/// <summary>
/// Service for validating audio fidelity using spectral analysis (headless FFmpeg).
/// 
/// WHY: Detects transcoded/fake lossless files that pass size checks:
/// - Upscaled MP3s claiming to be FLAC
/// - 128kbps files re-encoded to "320kbps"
/// - Lossy files with frequency cutoffs (16kHz brick wall)
/// 
/// HOW: FFmpeg spectral analysis extracts frequency data without decoding full audio.
/// We look for the "cutoff frequency" where content stops (MP3 encoders cut high freqs).
/// 
/// Phase 8 Enhancement: Producer-Consumer pattern for batch processing to prevent CPU/IO spikes.
/// 
/// ARCHITECTURE:
/// - Producer: Analysis requests queued to unbounded channel (never blocks caller)
/// - Consumers: 2 worker threads (configurable) process queue with FFmpeg
/// - WHY only 2 workers: FFmpeg is CPU-heavy (80-100% per process), 2 = balance speed/overhead
/// </summary>
public class SonicIntegrityService : IDisposable
{
    private readonly IForensicLogger _forensicLogger;
    private readonly ILogger<SonicIntegrityService> _logger;
    private readonly string _ffmpegPath = "ffmpeg";
    
    // Producer-Consumer pattern for batch analysis
    private readonly Channel<AnalysisRequest> _analysisQueue;
    private readonly int _maxConcurrency = 2; // WHY: 2 = sweet spot for 4+ core CPUs
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _workerTasks = new();
    private bool _isInitialized = false;

    public SonicIntegrityService(ILogger<SonicIntegrityService> logger, IForensicLogger forensicLogger)
    {
        _logger = logger;
        _forensicLogger = forensicLogger;
        
        // Create unbounded channel for analysis requests
        _analysisQueue = Channel.CreateUnbounded<AnalysisRequest>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
        
        // Start worker tasks
        for (int i = 0; i < _maxConcurrency; i++)
        {
            _workerTasks.Add(ProcessAnalysisQueueAsync(_cts.Token));
        }
        
        _logger.LogInformation("SonicIntegrityService initialized with {Workers} concurrent workers", _maxConcurrency);
    }

    /// <summary>
    /// Validates FFmpeg availability. Should be called during app startup.
    /// </summary>
    public async Task<bool> ValidateFfmpegAsync()
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    UseShellExecute = false
                }
            };
            
            process.Start();
            await process.WaitForExitAsync();
            
            _isInitialized = process.ExitCode == 0;
            
            if (_isInitialized)
            {
                _logger.LogInformation("FFmpeg validation successful");
            }
            else
            {
                _logger.LogWarning("FFmpeg validation failed (exit code: {Code})", process.ExitCode);
            }
            
            return _isInitialized;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FFmpeg not found in PATH");
            _isInitialized = false;
            return false;
        }
    }

    /// <summary>
    /// Returns true if FFmpeg is available and validated.
    /// </summary>
    public bool IsFfmpegAvailable() => _isInitialized;

    /// <summary>
    /// Performs spectral analysis on an audio file to detect upscaling or low-quality VBR.
    /// Uses Producer-Consumer pattern to queue analysis and prevent CPU spikes.
    /// </summary>
    public async Task<SonicAnalysisResult> AnalyzeTrackAsync(string filePath, string? correlationId = null)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Audio file not found for analysis", filePath);

        if (!_isInitialized)
        {
            _logger.LogWarning("FFmpeg not available, skipping sonic analysis for {File}", Path.GetFileName(filePath));
             if (correlationId != null)
                 _forensicLogger.Warning(correlationId, ForensicStage.IntegrityCheck, "FFmpeg validation failed - skipping sonic check");
            return new SonicAnalysisResult 
            { 
                IsTrustworthy = true, // Assume trustworthy if can't analyze
                Details = "FFmpeg not available - analysis skipped" 
            };
        }

        // Create request with completion source
        var tcs = new TaskCompletionSource<SonicAnalysisResult>();
        var request = new AnalysisRequest(filePath, correlationId ?? Guid.NewGuid().ToString(), tcs);
        
        // Queue for processing
        await _analysisQueue.Writer.WriteAsync(request);
        
        // Wait for result
        return await tcs.Task;
    }

    /// <summary>
    /// Worker task that processes queued analysis requests.
    /// </summary>
    private async Task ProcessAnalysisQueueAsync(CancellationToken cancellationToken)
    {
        await foreach (var request in _analysisQueue.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                var result = await PerformAnalysisAsync(request.FilePath, request.CorrelationId, cancellationToken);
                request.CompletionSource.SetResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Analysis failed for {File}", Path.GetFileName(request.FilePath));
                
                // Log failure via forensic logger
                 _forensicLogger.Error(request.CorrelationId, ForensicStage.IntegrityCheck, "Analysis worker crashed", null, ex);

                request.CompletionSource.SetResult(new SonicAnalysisResult 
                { 
                    IsTrustworthy = false, 
                    Details = "Analysis error: " + ex.Message 
                });
            }
        }

    }

    /// <summary>
    /// Core analysis logic (extracted from original AnalyzeTrackAsync).
    /// </summary>
    private async Task<SonicAnalysisResult> PerformAnalysisAsync(string filePath, string correlationId, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Starting sonic integrity analysis for: {File}", Path.GetFileName(filePath));
            _forensicLogger.Info(correlationId, ForensicStage.IntegrityCheck, "Starting spectral energy distribution scan");

            // SPECTRAL ANALYSIS SCIENCE:
            // WHY these specific frequencies:
            // - MP3 encoders use "psychoacoustic" cutoffs to save space
            // - 16kHz = 128kbps cutoff (most humans can't hear above this)
            // - 19kHz = 256kbps cutoff ("transparent" for most music)
            // - 21kHz = 320kbps/lossless preserves near-ultrasonic content
            // 
            // HOW we detect fakes:
            // - Real 320kbps: energy at 19kHz+ is -30 to -45 dB
            // - Upscaled 128kbps: energy at 16kHz+ is < -70 dB (brick wall)
            // - The "brick wall" is unmistakable - it's like a cliff in the spectrogram
            
            // Stage 1: Check energy above 16kHz (Cutoff for 128kbps)
            // WHY: 128kbps MP3 hard-cuts everything above ~16kHz to save bits
            // If energy < -55dB here, it's either 128kbps or an upscale
            double energy16k = await GetEnergyAboveFrequencyAsync(filePath, 16000, ct);
            
            // Stage 2: Check energy above 19kHz (Cutoff for 256k/320k)
            double energy19k = await GetEnergyAboveFrequencyAsync(filePath, 19000, ct);

            // Stage 3: Check energy above 21kHz (True Lossless/High-Res)
            double energy21k = await GetEnergyAboveFrequencyAsync(filePath, 21000, ct);

            _logger.LogDebug("Energy Profile for {File}: 16k={E16}dB, 19k={E19}dB, 21k={E21}dB", 
                Path.GetFileName(filePath), energy16k, energy19k, energy21k);
                
            _forensicLogger.Info(correlationId, ForensicStage.IntegrityCheck, 
                $"Spectral Energy: 16k={energy16k:F1}dB | 19k={energy19k:F1}dB | 21k={energy21k:F1}dB");

            int cutoff = 0;
            double confidence = 1.0;
            bool trustworthy = true;
            string details = "";

            // FORENSIC EVALUATION:
            // WHY -55dB threshold:
            // - Natural music content at 16kHz: -20 to -40 dB (cymbals, hi-hats)
            // - MP3 128kbps cutoff: -70 to -90 dB (encoder removed everything)
            // - -55dB = midpoint where we're confident it's a cutoff, not just quiet music
            if (energy16k < -55)
            {
                cutoff = 16000;
                confidence = 0.3; // 30% trustworthy = "probably fake"
                trustworthy = energy16k > -70; // WHY: -90dB = absolute zero (hard fake)
                details = "FAKED: Low-quality upscale (128kbps profile)";
                _forensicLogger.Warning(correlationId, ForensicStage.IntegrityCheck, "Severe high-frequency cutoff detected (16kHz)");
            }
            else if (energy19k < -55)
            {
                cutoff = 19000;
                confidence = 0.7;
                details = "MID-QUALITY: 192kbps profile detected";
                 _forensicLogger.Warning(correlationId, ForensicStage.IntegrityCheck, "Moderate high-frequency attenuation (19kHz)");
            }
            else if (energy21k < -50)
            {
                cutoff = 21000;
                confidence = 0.9;
                details = "HIGH-QUALITY: 320kbps profile detected";
            }
            else
            {
                cutoff = 22050; // Standard Full Spectrum
                confidence = 1.0;
                details = "AUDIOPHILE: Full frequency spectrum confirmed";
            }

            // Simple spectral hash based on energy ratios
            string spectralHash = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{energy16k:F1}|{energy19k:F1}")).Substring(0, 8);
            
            _forensicLogger.Info(correlationId, ForensicStage.IntegrityCheck, $"Analysis Conclusion: {details}");

            return new SonicAnalysisResult
            {
                QualityConfidence = confidence,
                FrequencyCutoff = cutoff,
                SpectralHash = spectralHash,
                IsTrustworthy = trustworthy,
                Details = details
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sonic analysis failed for {File}", filePath);
             _forensicLogger.Error(correlationId, ForensicStage.IntegrityCheck, "Spectral analysis failed", null, ex);
            return new SonicAnalysisResult { IsTrustworthy = false, Details = "Analysis error: " + ex.Message };
        }
    }

    private async Task<double> GetEnergyAboveFrequencyAsync(string filePath, int freq, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            // Phase 4: Hardware Acceleration
            Arguments = $"{SystemInfoHelper.GetFfmpegHwAccelArgs()} -i \"{filePath}\" -af \"highpass=f={freq},volumedetect\" -f null -",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        process.ErrorDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };

        process.Start();
        process.BeginErrorReadLine();
        
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Killing zombie FFmpeg process due to cancellation");
            try 
            { 
                 process.Kill(); 
                 await process.WaitForExitAsync(); // Ensure it's dead
            } 
            catch {}
            throw; // Re-throw to abort analysis
        }

        string result = output.ToString();
        // Parse "max_volume: -24.5 dB"
        var match = System.Text.RegularExpressions.Regex.Match(result, @"max_volume:\s+(-?\d+\.?\d*)\s+dB");
        if (match.Success && double.TryParse(match.Groups[1].Value, out double vol))
        {
            return vol;
        }

        return -91.0; // Assume silence if parsing fails
    }

    /// <summary>
    /// Generates a visual spectrogram using FFmpeg's showspectrumpic filter.
    /// Phase 8/13B: Visual Truth.
    /// </summary>
    /// <param name="inputPath">Path to source audio file.</param>
    /// <param name="outputPath">Target path for the PNG image.</param>
    /// <returns>True if successful.</returns>
    public async Task<bool> GenerateSpectrogramAsync(string inputPath, string outputPath)
    {
        if (!_isInitialized) return false;

        try
        {
            // SPEK-style visualization:
            // - s=1024x512: Resolution
            // - mode=separate: Separate channels? No, combined is usually better for quick checks, but separate shows stereo width. Let's use combined for cleaner UI.
            // - color=rainbow: Classic heatmap
            // - scale=log: Logarithmic intensity
            // - legend=1: Show frequency/time axes (Critical for verification)
            var args = $"-y -i \"{inputPath}\" -lavfi showspectrumpic=s=1024x512:mode=combined:color=rainbow:scale=log:legend=1 \"{outputPath}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true
            };

            using var process = new Process { StartInfo = startInfo };
            
            // Capture errors just in case
            var errorOutput = new StringBuilder();
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) errorOutput.AppendLine(e.Data); };
            
            process.Start();
            process.BeginErrorReadLine();
            
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                _logger.LogError("Spectrogram generation failed (Exit {Code}): {Error}", process.ExitCode, errorOutput);
                return false;
            }

            return File.Exists(outputPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spectrogram generation failed for {File}", inputPath);
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _analysisQueue.Writer.Complete();
        
        try
        {
            Task.WaitAll(_workerTasks.ToArray(), TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error waiting for worker tasks to complete");
        }
        
        _cts.Dispose();
    }

    /// <summary>
    /// Internal request model for the Producer-Consumer queue.
    /// </summary>
    private record AnalysisRequest(string FilePath, string CorrelationId, TaskCompletionSource<SonicAnalysisResult> CompletionSource);
}
