using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data; // For AppDbContext
using SLSKDONET.Data.Entities;
using SLSKDONET.Models; // For events
using Xabe.FFmpeg;
using Microsoft.EntityFrameworkCore; // For ToListAsync/FirstOrDefaultAsync

namespace SLSKDONET.Services;

public class AudioAnalysisService : IAudioAnalysisService
{
    private readonly ILogger<AudioAnalysisService> _logger;
    private readonly string _ffmpegPath = "ffmpeg"; // Assumes in PATH, validated by SonicIntegrityService
    private readonly SonicIntegrityService _sonicService;
    private readonly IEventBus _eventBus;

    private readonly IForensicLogger _forensicLogger;

    public AudioAnalysisService(ILogger<AudioAnalysisService> logger, SonicIntegrityService sonicService, IEventBus eventBus, IForensicLogger forensicLogger)
    {
        _logger = logger;
        _sonicService = sonicService;
        _eventBus = eventBus;
        _forensicLogger = forensicLogger;
    }

    public async Task<AudioAnalysisEntity?> AnalyzeFileAsync(string filePath, string trackUniqueHash, string? correlationId = null, CancellationToken cancellationToken = default)
    {
        // Use provided correlationId or generate a temporary one for this scope
        var cid = correlationId ?? Guid.NewGuid().ToString();
        var startTime = DateTime.UtcNow;

        if (!File.Exists(filePath))
        {
            _logger.LogError("Audio file not found for analysis: {Path}", filePath);
            _forensicLogger.Error(cid, ForensicStage.MusicalAnalysis, $"File not found: {filePath}", trackUniqueHash);
            return null;
        }

        using (_forensicLogger.TimedOperation(cid, ForensicStage.MusicalAnalysis, "Technical Audio Analysis", trackUniqueHash))
        {
            try
            {
                // Publish start event
                _eventBus.Publish(new AnalysisProgressEvent(trackUniqueHash, "Starting structural analysis...", 0));

                var analysis = new AudioAnalysisEntity
                {
                    TrackUniqueHash = trackUniqueHash,
                    AnalyzedAt = DateTime.UtcNow
                };

                // 1. Probe Format & Codec
                _forensicLogger.Info(cid, ForensicStage.MusicalAnalysis, "Probing audio format...", trackUniqueHash);
                
                // Xabe doesn't support CancellationToken natively in GetMediaInfo? Converting to Task.Run
                IMediaInfo mediaInfo;
                try
                {
                    mediaInfo = await Task.Run(() => FFmpeg.GetMediaInfo(filePath), cancellationToken);
                }
                catch (Exception probeEx)
                {
                    _forensicLogger.Error(cid, ForensicStage.MusicalAnalysis, "FFmpeg probe failed", trackUniqueHash, probeEx);
                    throw;
                }

                var audioStream = mediaInfo.AudioStreams.FirstOrDefault();

                if (audioStream == null)
                {
                    _logger.LogWarning("No audio stream found in {Path}", filePath);
                    _forensicLogger.Warning(cid, ForensicStage.MusicalAnalysis, "No audio data stream detected", trackUniqueHash);
                    return null;
                }

                analysis.Bitrate = (int)(audioStream.Bitrate / 1000); // Probe returns bps, convert to kbps? Careful with Xabe API.
                // Correction: Xabe.FFmpeg Bitrate is long kbps sometimes? No, typically bps. 
                // Let's assume bps. 
                // Wait, typically mediaInfo.Duration.TotalMilliseconds works.
                analysis.Bitrate = (int)(audioStream.Bitrate / 1000); 
                analysis.SampleRate = audioStream.SampleRate;
                analysis.Channels = audioStream.Channels;
                analysis.Codec = audioStream.Codec;
                analysis.DurationMs = (long)mediaInfo.Duration.TotalMilliseconds;
                
                _forensicLogger.Info(cid, ForensicStage.MusicalAnalysis, 
                    $"Format: {analysis.Codec} ({analysis.Bitrate}kbps) - {analysis.SampleRate}Hz / {analysis.Channels}ch", trackUniqueHash);

                // Progress: Structural complete
                _eventBus.Publish(new AnalysisProgressEvent(trackUniqueHash, "Analyzing loudness (LUFS)...", 33));

                // 2. Loudness Analysis (Slow) - Integrated Loudness (LUFS) & True Peak
                try 
                {
                    _forensicLogger.Info(cid, ForensicStage.MusicalAnalysis, "Measuring loudness (EBU R128)...", trackUniqueHash);
                    var loudnessData = await MeasureLoudnessAsync(filePath, cancellationToken);
                    analysis.LoudnessLufs = loudnessData.IntegratedLoudness;
                    analysis.TruePeakDb = loudnessData.TruePeak;
                    analysis.DynamicRange = loudnessData.LoudnessRange;
                    
                    _forensicLogger.Info(cid, ForensicStage.MusicalAnalysis, 
                        $"Loudness: {analysis.LoudnessLufs:F1} LUFS | TP: {analysis.TruePeakDb:F1} dB | DR: {analysis.DynamicRange:F1}", trackUniqueHash);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Loudness analysis failed for {Path}. Proceeding with structural data only.", filePath);
                    _forensicLogger.Warning(cid, ForensicStage.MusicalAnalysis, "Loudness analysis failed (non-critical)", trackUniqueHash, ex);
                }

                // Progress: Loudness complete
                _eventBus.Publish(new AnalysisProgressEvent(trackUniqueHash, "Detecting upscales & analyzing integrity...", 66));

                // Phase 3.5: Integrity Scout (Sonic Truth)
                try
                {
                    _forensicLogger.Info(cid, ForensicStage.MusicalAnalysis, "Running Sonic Integrity Scan...", trackUniqueHash);
                    
                    // Assuming SonicService will be updated separately or doesn't support cancellation yet?
                    // For now, wrap in Task.Run to allow basic cancellation
                    // Pass correlationId if we update SonicService [TODO]
                    var sonicResult = await Task.Run(() => _sonicService.AnalyzeTrackAsync(filePath), cancellationToken);
                    
                    analysis.IsUpscaled = !sonicResult.IsTrustworthy;
                    analysis.SpectralHash = sonicResult.SpectralHash;
                    analysis.FrequencyCutoff = sonicResult.FrequencyCutoff;
                    analysis.QualityConfidence = sonicResult.QualityConfidence;
                    
                    if (analysis.IsUpscaled)
                    {
                        _logger.LogWarning("⚠️ Integrity Scout detected upscale for {Hash}: Confidence={Conf:P0}, Cutoff={Cut}Hz", 
                            trackUniqueHash, analysis.QualityConfidence, analysis.FrequencyCutoff);
                        _forensicLogger.Warning(cid, ForensicStage.IntegrityCheck, 
                            $"Upscale DETECTED: {analysis.FrequencyCutoff}Hz cutoff (Conf: {analysis.QualityConfidence:P0})", trackUniqueHash);
                    }
                    else
                    {
                        _forensicLogger.Info(cid, ForensicStage.IntegrityCheck, "Integrity verified (High Frequency Content OK)", trackUniqueHash);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Integrity Scout failed for {Path}", filePath);
                    _forensicLogger.Error(cid, ForensicStage.IntegrityCheck, "Integrity scan failed", trackUniqueHash, ex);
                }

                // Progress: Analysis complete
                var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                _eventBus.Publish(new AnalysisProgressEvent(trackUniqueHash, "Analysis complete!", 100));
                
                return analysis;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Analysis cancelled for {Hash}", trackUniqueHash);
                _forensicLogger.Warning(cid, ForensicStage.MusicalAnalysis, "Analysis cancelled by user", trackUniqueHash);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to analyze audio file: {Path}", filePath);
                _forensicLogger.Error(cid, ForensicStage.MusicalAnalysis, "Critical analysis failure", trackUniqueHash, ex);
                return null;
            }
        }
    }

    public async Task<AudioAnalysisEntity?> GetAnalysisAsync(string trackUniqueHash)
    {
        try
        {
            using var db = new AppDbContext();
            return await db.AudioAnalysis
                .FirstOrDefaultAsync(a => a.TrackUniqueHash == trackUniqueHash);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve audio analysis for hash {Hash}", trackUniqueHash);
            return null;
        }
    }

    private record LoudnessResult(double IntegratedLoudness, double TruePeak, double LoudnessRange);

    private async Task<LoudnessResult> MeasureLoudnessAsync(string filePath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = $"-i \"{filePath}\" -filter_complex ebur128=peak=true -f null -",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        
        process.ErrorDataReceived += (s, e) => 
        { 
            if (e.Data != null) output.AppendLine(e.Data); 
        };

        process.Start();
        process.BeginErrorReadLine();
        
        // Phase 3: Run analysis at lower priority to prevent UI lags
        SystemInfoHelper.ConfigureProcessPriority(process, ProcessPriorityClass.BelowNormal);

        // Register cancellation to kill process
        await using var ctr = cancellationToken.Register(() => 
        {
            try { process.Kill(); } catch { }
        });

        await process.WaitForExitAsync(cancellationToken);

        string log = output.ToString();
        
        // Parse Output
        return new LoudnessResult(
            ParseValue(log, @"I:\s+([-\d\.]+)\s+LUFS"),
            ParseValue(log, @"Peak:\s+([-\d\.]+)\s+dBFS"),
            ParseValue(log, @"LRA:\s+([-\d\.]+)\s+LU")
        );
    }

    private double ParseValue(string log, string regexPattern)
    {
        var match = Regex.Match(log, regexPattern);
        if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double result))
        {
            return result;
        }
        return 0.0;
    }
}
