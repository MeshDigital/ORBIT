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
using MathNet.Numerics;
using NAudio.Wave;
using MathNet.Numerics.IntegralTransforms;

namespace SLSKDONET.Services;

/// <summary>
/// Result of a sonic integrity analysis.
/// </summary>
public class SonicAnalysisResult
{
    public double QualityConfidence { get; set; } // 0.0 - 1.0
    public int FrequencyCutoff { get; set; } // Hz
    public double DynamicRange { get; set; } // DR Score (Peak - RMS Top 20%)
    public string SpectralHash { get; set; } = string.Empty;
    public bool IsTrustworthy { get; set; }
    public string? Details { get; set; }
}

/// <summary>
/// Service for validating audio fidelity using spectral analysis (FFT) and Dynamic Range calculation.
/// Uses NAudio for sample access and MathNet.Numerics for FFT.
/// </summary>
public class SonicIntegrityService : IDisposable
{
    private readonly IForensicLogger _forensicLogger;
    private readonly ILogger<SonicIntegrityService> _logger;
    private readonly string _ffmpegPath = "ffmpeg";
    
    // Producer-Consumer pattern for batch analysis
    private readonly Channel<AnalysisRequest> _analysisQueue;
    private readonly int _maxConcurrency = 2; 
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

    public async Task<bool> ValidateFfmpegAsync()
    {
        try
        {
            // 1. Check basic execution
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

            // 2. Configure Xabe.FFmpeg if valid
            if (_isInitialized)
            {
                try 
                {
                    // Resolve full path to configure Xabe
                    string? fullPath = ResolveExecutablePath(_ffmpegPath);
                    if (!string.IsNullOrEmpty(fullPath))
                    {
                        string? directory = Path.GetDirectoryName(fullPath);
                        if (!string.IsNullOrEmpty(directory))
                        {
                            Xabe.FFmpeg.FFmpeg.SetExecutablesPath(directory);
                            _logger.LogInformation("Xabe.FFmpeg configured with path: {Path}", directory);
                        }
                    }
                }
                catch (Exception xabeEx)
                {
                     _logger.LogWarning(xabeEx, "Failed to configure Xabe.FFmpeg path explicitly");
                }
            }
            
            return _isInitialized;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FFmpeg validation failed");
            return false;
        }
    }

    private string? ResolveExecutablePath(string executableName)
    {
        if (Path.IsPathRooted(executableName)) return File.Exists(executableName) ? executableName : null;

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        var paths = pathEnv.Split(Path.PathSeparator);
        var extensions = new[] { ".exe", ".cmd", ".bat", "" }; // Windows extensions

        foreach (var path in paths)
        {
            var fullPath = Path.Combine(path, executableName);
            foreach (var ext in extensions)
            {
                var fullPathWithExt = fullPath + ext;
                if (File.Exists(fullPathWithExt)) return fullPathWithExt;
            }
        }
        return null;
    }

    public bool IsFfmpegAvailable() => _isInitialized;

    public async Task<SonicAnalysisResult> AnalyzeTrackAsync(string filePath, string? correlationId = null)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Audio file not found for analysis", filePath);

        var tcs = new TaskCompletionSource<SonicAnalysisResult>();
        var request = new AnalysisRequest(filePath, correlationId ?? Guid.NewGuid().ToString(), tcs);
        
        await _analysisQueue.Writer.WriteAsync(request);
        return await tcs.Task;
    }

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
                
                _forensicLogger.Error(request.CorrelationId, ForensicStage.IntegrityCheck, "Analysis worker crashed", null, ex);

                request.CompletionSource.SetResult(new SonicAnalysisResult 
                { 
                    IsTrustworthy = false, 
                    Details = "Analysis error: " + ex.Message 
                });
            }
        }
    }

    private async Task<SonicAnalysisResult> PerformAnalysisAsync(string filePath, string correlationId, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Starting deep forensic analysis for: {File}", Path.GetFileName(filePath));
            _forensicLogger.Info(correlationId, ForensicStage.IntegrityCheck, "Starting spectral scan & dynamics check");

            return await Task.Run(() => 
            {
                // 1. Setup Analysis Window
                // We analyze a 30s chunk from the middle
                const int analysisDurationSeconds = 30;
                
                using var reader = new AudioFileReader(filePath);
                var totalDuration = reader.TotalTime.TotalSeconds;
                var startPosition = totalDuration > analysisDurationSeconds 
                    ? (totalDuration / 2) - (analysisDurationSeconds / 2) 
                    : 0;
                
                reader.CurrentTime = TimeSpan.FromSeconds(startPosition);
                
                // Buffers
                int sampleRate = reader.WaveFormat.SampleRate;
                int channels = reader.WaveFormat.Channels;
                int fftSize = 4096; // 10.7Hz resolution at 44.1kHz
                var buffer = new float[fftSize];
                
                // Statistics
                var spectrumAccumulator = new double[fftSize / 2];
                int fftCount = 0;
                
                // Dynamic Range
                double maxPeak = 0;
                var rmsValues = new List<double>();
                
                // samples required for 30s
                long totalSamplesToRead = (long)(analysisDurationSeconds * sampleRate * channels);
                long samplesReadTotal = 0;
                
                // Processing Loop
                while (samplesReadTotal < totalSamplesToRead)
                {
                    int read = reader.Read(buffer, 0, fftSize);
                    if (read == 0) break;
                    
                    samplesReadTotal += read;

                    // 1. Dynamic Range Stats (per block)
                    double sumSquares = 0;
                    for (int i = 0; i < read; i++)
                    {
                        float abs = Math.Abs(buffer[i]);
                        if (abs > maxPeak) maxPeak = abs;
                        sumSquares += buffer[i] * buffer[i];
                    }
                    rmsValues.Add(Math.Sqrt(sumSquares / read));

                    // 2. FFT Analysis (Mono mixdown for spectrum)
                    // Only run FFT if we have a full buffer
                    if (read == fftSize)
                    {
                        var complexBuffer = new System.Numerics.Complex[fftSize];
                        var window = Window.Hann(fftSize);
                        
                        for (int i = 0; i < fftSize; i++)
                        {
                            complexBuffer[i] = new System.Numerics.Complex(buffer[i] * window[i], 0); 
                        }

                        // Perform FFT
                        Fourier.Forward(complexBuffer, FourierOptions.Matlab);
                        
                        // Accumulate Magnitude
                        for (int i = 0; i < fftSize / 2; i++)
                        {
                            spectrumAccumulator[i] += complexBuffer[i].Magnitude;
                        }
                        fftCount++;
                    }
                }
                
                // -- CALCULATE RESULTS -- //
                
                // A. Frequency Cutoff
                int cutoffFreq = 22050; // Default
                if (fftCount > 0)
                {
                    // Normalize spectrum
                    for(int i=0; i < spectrumAccumulator.Length; i++) spectrumAccumulator[i] /= fftCount;
                    
                    // Find 99% Energy Rolloff
                    double totalEnergy = spectrumAccumulator.Sum();
                    double currentEnergy = 0;
                    int binIndex = spectrumAccumulator.Length - 1;
                    
                    for (int i = 0; i < spectrumAccumulator.Length; i++)
                    {
                        currentEnergy += spectrumAccumulator[i];
                        if (currentEnergy / totalEnergy > 0.99) // 99% of energy is below this bin
                        {
                            binIndex = i;
                            break;
                        }
                    }
                    
                    double freqPerBin = (double)sampleRate / fftSize;
                    cutoffFreq = (int)(binIndex * freqPerBin);
                    
                    _logger.LogDebug($"Detected Cutoff: {cutoffFreq} Hz (Bin {binIndex})");
                }
                
                // B. Dynamic Range (Simplified implementation of DR meter)
                double drScore = 0;
                if (rmsValues.Count > 0)
                {
                    rmsValues.Sort();
                    // Take top 20% RMS values
                    int top20Start = (int)(rmsValues.Count * 0.8);
                    double top20RmsSum = 0;
                    for(int i = top20Start; i < rmsValues.Count; i++) top20RmsSum += rmsValues[i];
                    
                    double avgTop20Rms = top20RmsSum / (rmsValues.Count - top20Start);
                    
                    double peakDb = 20 * Math.Log10(maxPeak);
                    double rmsDb = 20 * Math.Log10(avgTop20Rms);
                    
                    drScore = peakDb - rmsDb;
                    _logger.LogDebug($"DR Calc: Peak {peakDb:F1}dB, RMS {rmsDb:F1}dB = DR {drScore:F1}");
                }

                // C. Interpret Results
                bool isFake = cutoffFreq < 17000; // Hard cutoff at 16k
                bool isBrickwalled = drScore < 6;
                string details = "";
                double confidence = 1.0;
                
                if (isFake)
                {
                    details = $"FAKE FLAC: {cutoffFreq/1000}kHz cutoff detected";
                    confidence = 0.2;
                     _forensicLogger.Warning(correlationId, ForensicStage.IntegrityCheck, details);
                }
                else if (cutoffFreq < 19500)
                {
                    details = $"Possible Transcode: {cutoffFreq/1000}kHz cutoff";
                    confidence = 0.6;
                }
                else
                {
                    details = "Verified Spectrum";
                }
                
                if (isBrickwalled)
                {
                    details += $" | Low Dynamic Range (DR {drScore:F0})";
                }
                
                return new SonicAnalysisResult
                {
                    FrequencyCutoff = cutoffFreq,
                    DynamicRange = drScore,
                    IsTrustworthy = !isFake,
                    QualityConfidence = confidence,
                    Details = details,
                    SpectralHash = $"{cutoffFreq}-{drScore:F1}"
                };

            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deep forensic analysis failed");
            _forensicLogger.Error(correlationId, ForensicStage.IntegrityCheck, "Analysis crashed", null, ex);
            throw;
        }
    }

    public async Task<bool> GenerateSpectrogramAsync(string inputPath, string outputPath)
    {
        if (!_isInitialized) return false;

        try
        {
            // Standard ffmpeg visualization
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
            process.Start();
            await process.WaitForExitAsync();

            return File.Exists(outputPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spectrogram generation failed");
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _analysisQueue.Writer.Complete();
        try { Task.WaitAll(_workerTasks.ToArray(), TimeSpan.FromSeconds(5)); } catch {}
        _cts.Dispose();
    }

    private record AnalysisRequest(string FilePath, string CorrelationId, TaskCompletionSource<SonicAnalysisResult> CompletionSource);
}
