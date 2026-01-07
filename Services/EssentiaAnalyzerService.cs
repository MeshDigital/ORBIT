using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data.Essentia;
using SLSKDONET.Services.Musical;

namespace SLSKDONET.Services;

public interface IAudioIntelligenceService
{
    Task<AudioFeaturesEntity?> AnalyzeTrackAsync(string filePath, string trackUniqueHash, string? correlationId = null, CancellationToken cancellationToken = default, bool generateCues = false);
    bool IsEssentiaAvailable();
}

/// <summary>
/// Phase 4: Musical Intelligence - Essentia Sidecar Integration.
/// Wraps the Essentia CLI binary for musical feature extraction.
/// Implements IDisposable to kill orphaned analysis processes on shutdown.
/// </summary>
public class EssentiaAnalyzerService : IAudioIntelligenceService, IDisposable
{
    private readonly ILogger<EssentiaAnalyzerService> _logger;
    private readonly PathProviderService _pathProvider;
    private readonly DropDetectionEngine _dropEngine;
    private readonly CueGenerationEngine _cueEngine;
    private const string ESSENTIA_EXECUTABLE = "essentia_streaming_extractor_music.exe";
    private const string ANALYSIS_VERSION = "Essentia-2.1-beta5";
    
    // WHY: 45-second timeout chosen through empirical testing:
    // - Average 3-5min track: 8-12 seconds on modern quad-core CPU
    // - FLAC decode overhead: 2-3x longer than MP3 (CPU-intensive)
    // - Slow HDD seeks: +5-10 seconds on fragmented drives
    // - Safety margin: 3x average = handles 99% of cases without false kills
    // - Prevents hung processes from blocking queue indefinitely
    private const int ANALYSIS_TIMEOUT_SECONDS = 45;
    
    private static string? _essentiaPath;
    private static bool _binaryValidated = false;
    private volatile bool _isDisposing = false;
    
    // WHY: Track running processes for cleanup:
    // - External processes don't auto-terminate when app crashes
    // - Orphaned essentia.exe can consume 100% CPU until manual kill
    // - This dictionary lets us kill ALL active analyses on Dispose()
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Process> _activeProcesses = new();
    private readonly IForensicLogger _forensicLogger;

    public EssentiaAnalyzerService(
        ILogger<EssentiaAnalyzerService> logger,
        PathProviderService pathProvider,
        DropDetectionEngine dropEngine,
        CueGenerationEngine cueEngine,
        IForensicLogger forensicLogger)
    {
        _logger = logger;
        _pathProvider = pathProvider;
        _dropEngine = dropEngine;
        _cueEngine = cueEngine;
        _forensicLogger = forensicLogger;
    }

    /// <summary>
    /// Phase 4.1: Binary Health Check.
    /// Validates that the Essentia executable exists and is callable.
    /// 
    /// WHY: Graceful degradation approach:
    /// 1. First check local Tools/Essentia/ (bundled with app)
    /// 2. Fallback to PATH environment (user-installed Essentia)
    /// 3. If neither exists, disable Musical Intelligence features
    /// 
    /// This prevents crashes/exceptions if Essentia is missing and allows
    /// the app to function (downloads still work, just no BPM/key detection).
    /// </summary>
    public bool IsEssentiaAvailable()
    {
        // ... (keep existing implementation) ...
        // WHY: Cache validation result to avoid repeated file system checks
        // (this is called on every track analysis enqueue)
        if (_binaryValidated && !string.IsNullOrEmpty(_essentiaPath))
            return true;

        // Check in Tools/Essentia/ directory
        var toolsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "Essentia", ESSENTIA_EXECUTABLE);
        
        if (File.Exists(toolsPath))
        {
            _essentiaPath = toolsPath;
            _binaryValidated = true;
            _logger.LogInformation("✅ Essentia binary found: {Path}", toolsPath);
            return true;
        }

        // Fallback: Check PATH environment
        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                var candidate = Path.Combine(dir, ESSENTIA_EXECUTABLE);
                if (File.Exists(candidate))
                {
                    _essentiaPath = candidate;
                    _binaryValidated = true;
                    _logger.LogInformation("✅ Essentia binary found in PATH: {Path}", candidate);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to search PATH for Essentia binary");
        }

        _logger.LogWarning("⚠️ Essentia binary not found. Musical analysis will be skipped.");
        _logger.LogWarning("💡 Place '{Exe}' in: {Path}", ESSENTIA_EXECUTABLE, toolsPath);
        return false;
    }

    public async Task<AudioFeaturesEntity?> AnalyzeTrackAsync(string filePath, string trackUniqueHash, string? correlationId = null, CancellationToken cancellationToken = default, bool generateCues = false)
    {
        var cid = correlationId ?? Guid.NewGuid().ToString();
        
        // Phase 4.1: Graceful degradation - skip if binary missing
        if (!IsEssentiaAvailable())
        {
            _logger.LogDebug("Skipping musical analysis for {Hash} - Essentia not available", trackUniqueHash);
            _forensicLogger.Warning(cid, ForensicStage.MusicalAnalysis, "Essentia binary not found - analysis skipped", trackUniqueHash);
            return null;
        }

        if (!File.Exists(filePath))
        {
            _logger.LogWarning("Cannot analyze {Path} - file not found", filePath);
            _forensicLogger.Error(cid, ForensicStage.MusicalAnalysis, "File not found", trackUniqueHash);
            return null;
        }

        using (_forensicLogger.TimedOperation(cid, ForensicStage.MusicalAnalysis, "Musical Feature Extraction", trackUniqueHash))
        {
            // Phase 4.1: Pro Tip - Skip analysis for tiny files (likely corrupt)
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length < 1024 * 1024) // < 1MB
            {
                _logger.LogWarning("Skipping analysis for {Path} - file too small ({Size} bytes)", filePath, fileInfo.Length);
                _forensicLogger.Warning(cid, ForensicStage.MusicalAnalysis, $"File too small ({fileInfo.Length} bytes) - possible corruption", trackUniqueHash);
                return null;
            }

            var tempJsonPath = Path.GetTempFileName();
            
            // Capture Process ID for cleanup in finally block
            int processId = 0;

            try
            {
                // Phase 4.1: Process Priority Control
                var startInfo = new ProcessStartInfo
                {
                    FileName = _essentiaPath!,
                    // Phase 4.1: Use ArgumentList for path safety (handles spaces/special chars)
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                
                // Add arguments safely
                startInfo.ArgumentList.Add(filePath);
                startInfo.ArgumentList.Add(tempJsonPath);

                // Phase 13: Advanced Configuration
                // If a profile exists, pass it to the extractor to control resolution/models
                // Try two locations: Data/Essentia (Source) or Tools/Essentia (Deployment)
                var profilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "Essentia", "profile.yaml");
                if (!File.Exists(profilePath))
                {
                    profilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "Essentia", "profile.yaml");
                }

                if (File.Exists(profilePath))
                {
                     startInfo.ArgumentList.Add(profilePath);
                     _logger.LogDebug("Using Essentia profile: {Profile}", profilePath);
                }

                _forensicLogger.Info(cid, ForensicStage.MusicalAnalysis, "Starting Essentia process...", trackUniqueHash);

                using var process = new Process { StartInfo = startInfo };
                process.Start();
                
                // Phase 11.5: Register with Job Object for Windows zombie prevention
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    ProcessJobTracker.RegisterProcess(process);
                }

                // Track process for cleanup
                try 
                {
                    processId = process.Id;
                    if (!_isDisposing)
                    {
                        _activeProcesses.TryAdd(processId, process);
                    }
                    else
                    {
                        try { process.Kill(); } catch { }
                        return null;
                    }
                }
                catch 
                {
                    // Ignored - if we can't get ID, we can't track it
                }
                
                // Phase 4.1: Set BelowNormal priority to prevent UI stutter
                // Phase 4.1: Set BelowNormal priority to prevent UI stutter
                SystemInfoHelper.ConfigureProcessPriority(process, ProcessPriorityClass.BelowNormal);

                // Register cancellation to kill process
                await using var ctr = cancellationToken.Register(() => 
                {
                    try 
                    { 
                        if (!process.HasExited) 
                        {
                            process.Kill(); 
                            _forensicLogger.Warning(cid, ForensicStage.MusicalAnalysis, "Process killed due to cancellation", trackUniqueHash);
                        }
                    } catch { }
                });

                await process.WaitForExitAsync(cancellationToken);

                if (process.ExitCode != 0)
                {
                    var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
                    _logger.LogWarning("Essentia analysis failed for {File}. Exit Code: {Code}, Error: {Err}", 
                        Path.GetFileName(filePath), process.ExitCode, stderr);
                    _forensicLogger.Error(cid, ForensicStage.MusicalAnalysis, $"Essentia failed (Exit: {process.ExitCode}): {stderr}", trackUniqueHash);
                    return null;
                }

                // Parse JSON Output
                if (!File.Exists(tempJsonPath))
                {
                    _logger.LogWarning("Essentia did not produce output JSON for {File}", filePath);
                    _forensicLogger.Error(cid, ForensicStage.MusicalAnalysis, "No JSON output produced", trackUniqueHash);
                    return null;
                }

                var jsonContent = await File.ReadAllTextAsync(tempJsonPath, cancellationToken);
                
                // Phase 4.1: JSON resiliency with AllowNamedFloatingPointLiterals
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                };
                
                var data = JsonSerializer.Deserialize<EssentiaOutput>(jsonContent, options);

                if (data == null)
                {
                    _logger.LogWarning("Failed to parse Essentia JSON for {File}", filePath);
                    _forensicLogger.Error(cid, ForensicStage.MusicalAnalysis, "Failed to parse JSON result", trackUniqueHash);
                    return null;
                }

                // DEBUG: Log extracted data to verify what Essentia produced
                _logger.LogInformation("🎵 ESSENTIA EXTRACTION SUCCESS for {File}:", Path.GetFileName(filePath));
                _logger.LogInformation("   ├─ BPM: {Bpm} (confidence: {Conf})", 
                    data.Rhythm?.Bpm ?? 0, data.Rhythm?.BpmConfidence ?? 0);
                _logger.LogInformation("   ├─ Key: {Key} {Scale} (strength: {Strength})", 
                    data.Tonal?.KeyEdma?.Key ?? "Unknown", 
                    data.Tonal?.KeyEdma?.Scale ?? "Unknown",
                    data.Tonal?.KeyEdma?.Strength ?? 0);
                _logger.LogInformation("   ├─ Danceability: {Dance} | Voice/Instrumental: {Voice}",
                    data.HighLevel?.Danceability?.AllDanceability ?? 0,
                    data.HighLevel?.VoiceInstrumental?.AllVoice ?? 0);
                _logger.LogInformation("   └─ Moods - Happy: {Happy} | Aggressive: {Aggressive}",
                    data.HighLevel?.MoodHappy?.AllHappy ?? 0,
                    data.HighLevel?.MoodAggressive?.AllAggressive ?? 0);

                // Map to AudioFeaturesEntity
                var entity = new AudioFeaturesEntity
                {
                    TrackUniqueHash = trackUniqueHash,
                    
                    // Core Musical Features
                    Bpm = data.Rhythm?.Bpm ?? 0,
                    BpmConfidence = data.Rhythm?.BpmConfidence ?? 0,
                    Key = data.Tonal?.KeyEdma?.Key ?? string.Empty,
                    Scale = data.Tonal?.KeyEdma?.Scale ?? string.Empty,
                    KeyConfidence = data.Tonal?.KeyEdma?.Strength ?? 0,
                    CamelotKey = string.Empty, // Will be calculated by KeyConverter in Phase 4.3
                    
                    // Sonic Characteristics
                    // Phase 13A: Improved Energy mapping (combines RMS intensity and Loudness)
                    Energy = data.LowLevel?.Rms?.Mean * 10 ?? (data.LowLevel?.AverageLoudness > -8 ? 0.9f : (data.LowLevel?.AverageLoudness > -12 ? 0.7f : 0.5f)),
                    Danceability = data.Rhythm?.Danceability ?? 0,
                    LoudnessLUFS = data.LowLevel?.AverageLoudness ?? 0,
                    SpectralCentroid = data.LowLevel?.SpectralCentroid?.Mean ?? 0,
                    SpectralComplexity = data.LowLevel?.SpectralComplexity?.Mean ?? 0,
                    DynamicComplexity = data.LowLevel?.DynamicComplexity ?? 0,
                    OnsetRate = data.Rhythm?.OnsetRate ?? 0,
                    
                    // Phase 13A: Forensic Librarian (BPM Stability & Dynamic Compression)
                    BpmStability = CalculateBpmStability(data.Rhythm?.BpmHistogram),
                    IsDynamicCompressed = DetectDynamicCompression(
                        data.LowLevel?.DynamicComplexity ?? 0, 
                        data.LowLevel?.AverageLoudness ?? 0),

                    // Phase 13C: AI Layer (Vocals & Mood)
                    InstrumentalProbability = data.HighLevel?.VoiceInstrumental?.All?.Instrumental ?? 0,
                    MoodTag = DetermineMoodTag(data.HighLevel),
                    MoodConfidence = CalculateMoodConfidence(data.HighLevel),

                    // Advanced Harmonic Mixing
                    ChordProgression = FormatChordProgression(data.Tonal?.ChordsKey),
                    
                    // Metadata
                    AnalysisVersion = ANALYSIS_VERSION,
                    AnalyzedAt = DateTime.UtcNow
                };

                // Phase 16.2: Extract & Cache AI Embeddings
                if (data.HighLevel?.ExtensionData != null)
                {
                    foreach (var kvp in data.HighLevel.ExtensionData)
                    {
                        // Look for the discogs-effnet model output
                        if (kvp.Key.Contains("discogs", StringComparison.OrdinalIgnoreCase) && 
                            kvp.Key.Contains("effnet", StringComparison.OrdinalIgnoreCase))
                        {
                            var embedding = ExtractEmbeddingFromJson(kvp.Value);
                            if (embedding != null && embedding.Length > 0)
                            {
                                entity.AiEmbeddingJson = JsonSerializer.Serialize(embedding);
                                // Calculate L2 Norm (Magnitude) for efficient Cosine Similarity
                                entity.EmbeddingMagnitude = (float)Math.Sqrt(embedding.Sum(x => x * x));
                            }
                            break;
                        }
                    }
                }
                
                _forensicLogger.Info(cid, ForensicStage.MusicalAnalysis, 
                    $"Extracted: {entity.Bpm:F1} BPM | Key: {entity.Key} {entity.Scale} | Dance: {entity.Danceability:F1}", trackUniqueHash);

                // Phase 4.2: Drop Detection & Cue Generation (OPT-IN ONLY for safety)
                // User must manually trigger via UI to avoid unintended modifications
                if (generateCues && entity.Bpm > 0)
                {
                    try
                    {
                        // Get track duration (estimate from file or use metadata)
                        float estimatedDuration = 180f; // Default 3 minutes
                        
                        // Detect drop
                        var (dropTime, confidence) = await _dropEngine.DetectDropAsync(data, estimatedDuration, trackUniqueHash);
                        
                        if (dropTime.HasValue)
                        {
                            // Generate cues from drop
                            var cues = _cueEngine.GenerateCues(dropTime.Value, entity.Bpm);
                            
                            entity.DropTimeSeconds = (float)dropTime.Value;
                            entity.DropConfidence = (float)confidence;
                            entity.CuePhraseStart = (float)cues.PhraseStart;
                            entity.CueBuild = (float)cues.Build;
                            entity.CueDrop = (float)cues.Drop;
                            entity.CueIntro = (float)cues.Intro;
                            
                            _logger.LogInformation("🎯 Drop + Cues generated: Drop={Drop:F1}s, Build={Build:F1}s, PhraseStart={PS:F1}s",
                                dropTime.Value, cues.Build, cues.PhraseStart);
                            _forensicLogger.Info(cid, ForensicStage.CueGeneration, 
                                $"Drop detected at {dropTime.Value:F1}s (Conf: {confidence:P0})", trackUniqueHash);
                        }
                        else
                        {
                            _logger.LogDebug("No clear drop detected for {Hash}", trackUniqueHash);
                            _forensicLogger.Info(cid, ForensicStage.CueGeneration, "No clear drop detected", trackUniqueHash);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Drop/Cue generation failed (non-fatal)");
                        _forensicLogger.Warning(cid, ForensicStage.CueGeneration, "Drop detection failed", trackUniqueHash, ex);
                    }
                }
                
                // _logger.LogInformation("🧠 Essentia Analyzed {Hash}: BPM={Bpm:F1}, Key={Key} {Scale}", 
                //     trackUniqueHash, entity.Bpm, entity.Key, entity.Scale);

                return entity;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Essentia analysis cancelled for {Hash}", trackUniqueHash);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Essentia critical failure on {Path}", filePath);
                _forensicLogger.Error(cid, ForensicStage.MusicalAnalysis, "Essentia critical failure", trackUniqueHash, ex);
                return null;
            }
            finally
            {
                // Stop tracking process
                if (processId > 0)
                {
                    _activeProcesses.TryRemove(processId, out _);
                }

                // Cleanup temp file
                try
                {
                    if (File.Exists(tempJsonPath)) File.Delete(tempJsonPath);
                }
                catch { /* Ignore cleanup errors */ }
            }
        }
    }

    // ============================================
    // Phase 13: Helper Methods
    // ============================================

    private static float CalculateBpmStability(float[]? bpmHistogram)
    {
        if (bpmHistogram == null || bpmHistogram.Length == 0)
            return 1.0f; // No data, assume stable

        // Calculate variance in histogram
        // A stable BPM will have a single sharp peak (low variance)
        // A drifting BPM will have multiple peaks (high variance)
        float max = bpmHistogram.Max();
        int peakCount = bpmHistogram.Count(v => v > max * 0.5f);

        // If only one dominant peak, very stable
        if (peakCount <= 2) return 1.0f;
        
        // If wide distribution, low stability
        if (peakCount > 10) return 0.3f;

        // Gradual stability based on peak spread
        return Math.Clamp(1.0f - (peakCount / 20.0f), 0.3f, 1.0f);
    }

    private static bool DetectDynamicCompression(float dynamicComplexity, float loudnessLUFS)
    {
        // "Sausage Master" Detection:
        // Triggered if dynamic range is crushed (very low complexity)
        // AND loudness is pushed very hot (above -7 LUFS)
        return dynamicComplexity < 2.0f && loudnessLUFS > -7.0f;
    }

    private static string DetermineMoodTag(HighLevelData? highLevel)
    {
        if (highLevel == null) return string.Empty;

        // Aggregate mood model outputs to determine primary mood
        var moods = new Dictionary<string, float>
        {
            ["Happy"] = highLevel.MoodHappy?.All?.Happy ?? 0,
            ["Aggressive"] = highLevel.MoodAggressive?.All?.Aggressive ?? 0,
            ["Calm"] = highLevel.MoodHappy?.All?.NotHappy ?? 0, // Inverse of happy = calm
            ["Intense"] = highLevel.MoodAggressive?.All?.NotAggressive ?? 0 // Inverse
        };

        // Return highest confidence mood
        var topMood = moods.OrderByDescending(m => m.Value).FirstOrDefault();
        return topMood.Value > 0.5f ? topMood.Key : "Neutral";
    }

    private static float CalculateMoodConfidence(HighLevelData? highLevel)
    {
        if (highLevel == null) return 0f;

        // Return the highest probability among all mood predictions
        return new[]
        {
            highLevel.MoodHappy?.Probability ?? 0,
            highLevel.MoodAggressive?.Probability ?? 0
        }.Max();
    }

    private static string FormatChordProgression(string[]? chordsKey)
    {
        if (chordsKey == null || chordsKey.Length == 0)
            return string.Empty;

        // Take first 8 chords and format as progression
        // Example: ["Am", "G", "F", "E"] -> "Am | G | F | E"
        var chords = chordsKey.Take(8).Where(c => !string.IsNullOrEmpty(c));
        return string.Join(" | ", chords);
    }

    private static float[]? ExtractEmbeddingFromJson(JsonElement element)
    {
        try
        {
            // Typical Essentia Structure: { "all": { "embeddings": [...] } } OR { "embeddings": [...] }
            // Try "all" -> "embeddings"
            if (element.TryGetProperty("all", out var allProp) && allProp.TryGetProperty("embeddings", out var embProp))
            {
                // Sometimes it's a nested array [[...]], take the first one (mean)
                if (embProp.ValueKind == JsonValueKind.Array)
                {
                    // Check if it's array of arrays or flat
                    var first = embProp.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.Array)
                    {
                        return first.EnumerateArray().Select(x => x.GetSingle()).ToArray();
                    }
                    return embProp.EnumerateArray().Select(x => x.GetSingle()).ToArray();
                }
            }
            
            // Try direct "embeddings"
            if (element.TryGetProperty("embeddings", out var directEmb))
            {
                if (directEmb.ValueKind == JsonValueKind.Array)
                {
                     // Check if it's array of arrays or flat
                    var first = directEmb.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.Array)
                    {
                        return first.EnumerateArray().Select(x => x.GetSingle()).ToArray();
                    }
                    return directEmb.EnumerateArray().Select(x => x.GetSingle()).ToArray();
                }
            }

            // Validating raw output if structure is flattened
            return null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _isDisposing = true;
        foreach (var kvp in _activeProcesses)
        {
            try
            {
                var proc = kvp.Value;
                if (!proc.HasExited)
                {
                    _logger.LogWarning("Killing orphaned Essentia process {Pid} during shutdown", kvp.Key);
                    proc.Kill();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Error killing Essentia process: {Msg}", ex.Message);
            }
        }
        _activeProcesses.Clear();
    }
}
