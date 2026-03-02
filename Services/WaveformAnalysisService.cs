using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Service for generating high-fidelity waveform data (Peak + RMS) from audio files via FFmpeg.
/// Used for the "Max Ultra" waveform visualization.
/// </summary>
public class WaveformAnalysisService
{
    private readonly ILogger<WaveformAnalysisService> _logger;
    private readonly string _ffmpegPath = "ffmpeg"; // Assumes in PATH or co-located

    public WaveformAnalysisService(ILogger<WaveformAnalysisService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Generates detailed waveform data for the given audio file.
    /// Spawns FFmpeg with a complex filtergraph to extract frequency bands (RGB) in a single pass.
    /// </summary>
    /// <param name="filePath">Path to the audio file.</param>
    /// <param name="pointsPerSecond">Resolution of the waveform (default 100).</param>
    /// <returns>WaveformAnalysisData containing Peak, RMS, and RGB arrays.</returns>
    public async Task<WaveformAnalysisData> GenerateWaveformAsync(string filePath, CancellationToken cancellationToken = default, int pointsPerSecond = 100)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("Waveform generation skipped: file not found at {FilePath}", filePath);
            return new WaveformAnalysisData();
        }

        // TRI-BAND FORENSIC EXTRACTION:
        // We use a complex filtergraph to split the audio into 3 frequency bands + 1 clean mono channel.
        // Band 1 (Low/Red): 20Hz - 250Hz
        // Band 2 (Mid/Green): 250Hz - 2kHz
        // Band 3 (High/Blue): > 2kHz
        // Band 4 (Total): Clean mono for Peak/RMS
        // [0:a]aformat=channel_layouts=mono: Ensures consistent phase-safe mono before splitting.
        var filterGraph = "[0:a]aformat=channel_layouts=mono,asplit=4[lo][mi][hi][cl]; " +
                         "[lo]lowpass=f=250[loout]; " +
                         "[mi]bandpass=f=1125:width=1750[miout]; " +
                         "[hi]highpass=f=2000[hiout]; " +
                         "[loout][miout][hiout][cl]amerge=4";

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = $"{SystemInfoHelper.GetFfmpegHwAccelArgs()} -i \"{filePath}\" -filter_complex \"{filterGraph}\" -f s16le -ac 4 -ar 44100 -vn -",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var peakPoints = new List<byte>();
        var rmsPoints = new List<byte>();
        var lowPoints = new List<byte>();
        var midPoints = new List<byte>();
        var highPoints = new List<byte>();

        long totalSamples = 0;
        Process? process = null;

        try
        {
            process = new Process { StartInfo = startInfo };
            process.Start();

            // Register cancellation
            await using var ctr = cancellationToken.Register(() => 
            {
                try { if (process != null && !process.HasExited) process.Kill(); } catch { }
            });

            _ = Task.Run(async () => 
            {
                try { await process.StandardError.ReadToEndAsync(cancellationToken); } catch { }
            }, cancellationToken);

            // 44100 Hz / 100 points = 441 samples per point
            int samplesPerPoint = 44100 / pointsPerSecond;
            // 4 channels, 16-bit (2 bytes) per sample = 8 bytes per frame
            int bytesPerFrame = 8;
            int bytesPerWindow = samplesPerPoint * bytesPerFrame;
            
            byte[] buffer = new byte[bytesPerWindow];
            var baseStream = process.StandardOutput.BaseStream;
            int bytesRead;

            while ((bytesRead = await ReadFullBufferAsync(baseStream, buffer, cancellationToken)) > 0)
            {
                int frameCount = bytesRead / bytesPerFrame;
                if (frameCount == 0) continue;

                float maxPeak = 0;
                float maxHighPeak = 0; // For Hybrid approach (Peak for highs)
                double sumSqTotal = 0;
                double sumSqLow = 0;
                double sumSqMid = 0;
                double sumSqHigh = 0;
                
                for (int i = 0; i < frameCount; i++)
                {
                    int baseIdx = i * bytesPerFrame;
                    
                    // Channel 0: Low (Red)
                    short lowS = BitConverter.ToInt16(buffer, baseIdx);
                    // Channel 1: Mid (Green)
                    short midS = BitConverter.ToInt16(buffer, baseIdx + 2);
                    // Channel 2: High (Blue)
                    short highS = BitConverter.ToInt16(buffer, baseIdx + 4);
                    // Channel 3: Total Mono
                    short totalS = BitConverter.ToInt16(buffer, baseIdx + 6);

                    float fLow = Math.Abs(lowS / 32768f);
                    float fMid = Math.Abs(midS / 32768f);
                    float fHigh = Math.Abs(highS / 32768f);
                    float fTotal = Math.Abs(totalS / 32768f);

                    if (fTotal > maxPeak) maxPeak = fTotal;
                    if (fHigh > maxHighPeak) maxHighPeak = fHigh;
                    
                    sumSqTotal += (double)fTotal * fTotal;
                    sumSqLow += (double)fLow * fLow;
                    sumSqMid += (double)fMid * fMid;
                    sumSqHigh += (double)fHigh * fHigh;
                }

                // RMS Calculation for energy
                float rmsTotal = (float)Math.Sqrt(sumSqTotal / frameCount);
                float rmsLow = (float)Math.Sqrt(sumSqLow / frameCount);
                float rmsMid = (float)Math.Sqrt(sumSqMid / frameCount);
                
                // Hybrid Detection Mode: RMS for low/mid, Peak for high (transients)
                float valHigh = maxHighPeak; 

                // Perceptual Normalization & Scaling (0-255)
                // 1. Bass Boost (1.3x)
                rmsLow *= 1.3f;
                
                // 2. Gamma Correction (0.45) for visual density
                float finalLow = (float)Math.Pow(Math.Clamp(rmsLow, 0, 1), 0.45);
                float finalMid = (float)Math.Pow(Math.Clamp(rmsMid, 0, 1), 0.45);
                float finalHigh = (float)Math.Pow(Math.Clamp(valHigh, 0, 1), 0.45);

                peakPoints.Add((byte)Math.Clamp(maxPeak * 255, 0, 255));
                rmsPoints.Add((byte)Math.Clamp(rmsTotal * 255, 0, 255));
                
                lowPoints.Add((byte)Math.Clamp(finalLow * 255, 0, 255));
                midPoints.Add((byte)Math.Clamp(finalMid * 255, 0, 255));
                highPoints.Add((byte)Math.Clamp(finalHigh * 255, 0, 255));

                totalSamples += frameCount;
            }

            await process.WaitForExitAsync(cancellationToken);
            
            return new WaveformAnalysisData
            {
                PeakData = peakPoints.ToArray(),
                RmsData = rmsPoints.ToArray(),
                LowData = lowPoints.ToArray(),
                MidData = midPoints.ToArray(),
                HighData = highPoints.ToArray(),
                PointsPerSecond = pointsPerSecond,
                DurationSeconds = (double)totalSamples / 44100.0
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Waveform generation cancelled for {File}", filePath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate waveform for {File}", filePath);
            return new WaveformAnalysisData();
        }
        finally
        {
            if (process != null && !process.HasExited)
            {
                try { process.Kill(); } catch { }
            }
            process?.Dispose();
        }
    }

    /// <summary>
    /// Helper to ensure we read exact window size unless EOF.
    /// </summary>
    private async Task<int> ReadFullBufferAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer, totalRead, buffer.Length - totalRead, ct);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }
}
