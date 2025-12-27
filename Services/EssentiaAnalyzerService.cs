using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data.Essentia;

namespace SLSKDONET.Services;

public interface IAudioIntelligenceService
{
    Task<AudioFeaturesEntity?> AnalyzeTrackAsync(string filePath, string trackUniqueHash);
}

public class EssentiaAnalyzerService : IAudioIntelligenceService
{
    private readonly ILogger<EssentiaAnalyzerService> _logger;
    private const string ESSENTIA_EXECUTABLE = "essentia_streaming_extractor_music";

    public EssentiaAnalyzerService(ILogger<EssentiaAnalyzerService> logger)
    {
        _logger = logger;
    }

    public async Task<AudioFeaturesEntity?> AnalyzeTrackAsync(string filePath, string trackUniqueHash)
    {
        if (!File.Exists(filePath)) return null;

        var tempJsonPath = Path.GetTempFileName();
        
        try
        {
            // 1. Run Essentia CLI
            // Usage: essentia_streaming_extractor_music input_audio output_json [profile]
            var startInfo = new ProcessStartInfo
            {
                FileName = ESSENTIA_EXECUTABLE,
                Arguments = $"\"{filePath}\" \"{tempJsonPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Check if executable exists in PATH or local directory
            // logic to find exec...
            
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                _logger.LogWarning("Essentia analysis failed for {File}. Error: {Err}", filePath, stderr);
                return null;
            }

            // 2. Parse JSON Output
            if (!File.Exists(tempJsonPath)) return null;

            var jsonContent = await File.ReadAllTextAsync(tempJsonPath);
            var data = JsonSerializer.Deserialize<EssentiaOutput>(jsonContent);

            if (data == null) return null;

            // 3. Map to Entity
            var entity = new AudioFeaturesEntity
            {
                TrackUniqueHash = trackUniqueHash,
                Bpm = data.Rhythm?.Bpm ?? 0,
                Danceability = data.Rhythm?.Danceability ?? 0,
                Key = MapToCamelot(data.Tonal?.KeyEdma?.Key, data.Tonal?.KeyEdma?.Scale),
                Scale = data.Tonal?.KeyEdma?.Scale ?? "unknown",
                Energy = 0, // Essentia extractor music doesn't always output 'energy' directly in top level, omitting for now
                AnalyzedAt = DateTime.UtcNow
            };
            
            _logger.LogInformation("🧠 Essentia Analyzed {Hash}: BPM={Bpm}, Key={Key}", trackUniqueHash, entity.Bpm, entity.Key);

            return entity;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Essentia critical failure on {Path}", filePath);
            return null;
        }
        finally
        {
            if (File.Exists(tempJsonPath)) File.Delete(tempJsonPath);
        }
    }

    private string MapToCamelot(string? key, string? scale)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(scale)) return "Unknown";
        
        // Simple mapping table logic could go here
        // For now, return raw format e.g. "C major"
        return $"{key} {scale}";
    }
}
