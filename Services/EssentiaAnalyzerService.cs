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
    Task<AudioFeaturesEntity?> AnalyzeTrackAsync(string filePath, string trackUniqueHash, string? correlationId = null, CancellationToken cancellationToken = default, bool generateCues = false, AnalysisTier tier = AnalysisTier.Tier1, System.Diagnostics.ProcessPriorityClass priority = System.Diagnostics.ProcessPriorityClass.Normal);
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
    private const int ANALYSIS_TIMEOUT_SECONDS = 120;
    
    private static string? _essentiaPath;
    private static bool _binaryValidated = false;
    // private volatile bool _isDisposing = false; // Unused
    
    // WHY: Track running processes for cleanup:
    // - External processes don't auto-terminate when app crashes
    // - Orphaned essentia.exe can consume 100% CPU until manual kill
    // - This dictionary lets us kill ALL active analyses on Dispose()
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Process> _activeProcesses = new();
    private readonly IForensicLogger _forensicLogger;
    private readonly Services.AI.TensorFlowModelPool _modelPool;

    public EssentiaAnalyzerService(
        ILogger<EssentiaAnalyzerService> logger,
        PathProviderService pathProvider,
        DropDetectionEngine dropEngine,
        CueGenerationEngine cueEngine,
        IForensicLogger forensicLogger,
        Services.AI.TensorFlowModelPool modelPool)
    {
        _logger = logger;
        _pathProvider = pathProvider;
        _dropEngine = dropEngine;
        _cueEngine = cueEngine;
        _forensicLogger = forensicLogger;
        _modelPool = modelPool;
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

    public async Task<AudioFeaturesEntity?> AnalyzeTrackAsync(string filePath, string trackUniqueHash, string? correlationId = null, CancellationToken cancellationToken = default, bool generateCues = false, AnalysisTier tier = AnalysisTier.Tier1, System.Diagnostics.ProcessPriorityClass priority = System.Diagnostics.ProcessPriorityClass.Normal)
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

        var stopwatch = Stopwatch.StartNew();
        using (_forensicLogger.TimedOperation(cid, ForensicStage.MusicalAnalysis, $"Musical Feature Extraction ({tier})", trackUniqueHash))
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

                // Phase 13: Advanced Sidecar Configuration
                // Use tiered profile.yaml to configure the C++ sidecar (Efficient model loading)
                string profileFileName = tier switch
                {
                    AnalysisTier.Tier2 => "profile_tier2.yaml",
                    AnalysisTier.Tier3 => "profile_tier3.yaml",
                    _ => "profile_tier1.yaml"
                };

                var profilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "Essentia", profileFileName);
                if (File.Exists(profilePath))
                {
                     startInfo.ArgumentList.Add(profilePath);
                     _logger.LogInformation("🧠 SIDECAR: Using Essentia {Tier} profile: {Profile}", tier, profileFileName);
                }
                else
                {
                    // Fallback to default profile if tier-specific is missing
                    profilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "Essentia", "profile.yaml");
                    if (File.Exists(profilePath))
                    {
                        startInfo.ArgumentList.Add(profilePath);
                        _logger.LogInformation("🧠 SIDECAR: Tier-specific profile missing, falling back to default: {Profile}", profilePath);
                    }
                }

                _forensicLogger.Info(cid, ForensicStage.MusicalAnalysis, "Starting Essentia process...", trackUniqueHash);

                using var process = new Process { StartInfo = startInfo };
                process.Start();
                
                // Phase 1.2: Multicore Optimization (Leaf Icon / EcoQoS)
                if (priority != System.Diagnostics.ProcessPriorityClass.Normal)
                {
                    SystemInfoHelper.ConfigureProcessPriority(process, priority);
                }
                
                // Zombie prevention
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    ProcessJobTracker.RegisterProcess(process);
                }

                // Process tracking
                processId = process.Id;
                _activeProcesses.TryAdd(processId, process);
                
                SystemInfoHelper.ConfigureProcessPriority(process, ProcessPriorityClass.BelowNormal);

                // Phase 13B: Hardening - Watchdog Implementation
                using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                watchdogCts.CancelAfter(TimeSpan.FromSeconds(ANALYSIS_TIMEOUT_SECONDS));

                try 
                {
                    // Use a more aggressive wait for the process to exit
                    await process.WaitForExitAsync(watchdogCts.Token);
                }
                catch (OperationCanceledException) when (watchdogCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // Mark as timeout only if the watchdog triggered it
                    _logger.LogError("⏱ SIDECAR TIMEOUT: Analysis killed for {Hash} after {Timeout}s", trackUniqueHash, ANALYSIS_TIMEOUT_SECONDS);
                    try { process.Kill(true); } catch { }
                    return null;
                }

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
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };
                
                // Sanitize JSON content (strip control characters)
                // Sanitize JSON content (strip control characters)
                jsonContent = SanitizeJson(jsonContent);

                if (string.IsNullOrWhiteSpace(jsonContent))
                {
                    _logger.LogWarning("Essentia produced empty JSON output for {File}", filePath);
                    return null;
                }

                EssentiaOutput? data = null;
                try
                {
                    data = JsonSerializer.Deserialize<EssentiaOutput>(jsonContent, options);
                }
                catch (JsonException jex)
                {
                     _logger.LogError(jex, "JSON Deserialization failed for {File}. Content length: {Len}", filePath, jsonContent.Length);
                     // Allow fallback to null return
                }

                if (data == null)
                {
                    _logger.LogWarning("Failed to parse Essentia JSON for {File} (Result was null)", filePath);
                    _forensicLogger.Error(cid, ForensicStage.MusicalAnalysis, "Failed to parse JSON result", trackUniqueHash);
                    return null;
                }

                // Phase 13B: Deep Learning Cortex (In-Process ML)
                // Use TensorFlowModelPool for high-level classification to avoid process overhead.
                // We'll feed the basic statistics extracted by Essentia into our TF models.
                string modelsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "Essentia", "models");
                
                if (Directory.Exists(modelsDir))
                {
                    // Example: Predict Danceability using in-process model if possible
                    string danceModel = Path.Combine(modelsDir, "danceability-effnet-discogs-1.pb");
                    if (File.Exists(danceModel))
                    {
                        // Simplified feature feed for demo - in reality, we'd extract specific embeddings
                        float[] dummyEmbeddings = new float[128]; // Placeholder for real EffNet embeddings
                        var danceResults = await _modelPool.PredictAsync(danceModel, dummyEmbeddings);
                        if (danceResults.Length > 0)
                        {
                            _logger.LogInformation("🧠 CORTEX: In-process Danceability prediction: {Result}", danceResults[0]);
                            // Update data if model prediction is more authoritative
                        }
                    }
                }

                // Helper to extract probability from either standard property or ExtensionData
                float GetProb(ModelPrediction? pred, string modelKey, string classKey) 
                {
                    if (pred?.All != null) 
                    {
                        // Map standard class names if needed, or rely on caller to check All properties??
                        // Simpler: Just rely on ExtensionData fallback if standard is missing/zero
                        // But standard property "Danceability" might be populated if model name matched "danceability"
                        // checks:
                        if (classKey == "danceable" && pred.All.Danceable > 0) return pred.All.Danceable;
                        if (classKey == "happy" && pred.All.Happy > 0) return pred.All.Happy;
                        if (classKey == "aggressive" && pred.All.Aggressive > 0) return pred.All.Aggressive;
                        if (classKey == "sad" && pred.All.Sad > 0) return pred.All.Sad;
                        if (classKey == "voice" && pred.All.Voice > 0) return pred.All.Voice;
                    }
                    return ExtractModelProbability(data.HighLevel?.ExtensionData, modelKey, classKey);
                }

                // Tier 1 Models (discogs-effnet) often land in ExtensionData
                float danceProb = GetProb(data.HighLevel?.Danceability, "danceability-discogs-effnet-1", "danceable");
                float voiceProb = GetProb(data.HighLevel?.VoiceInstrumental, "voice_instrumental-msd-musicnn-1", "voice");
                float happyProb = GetProb(data.HighLevel?.MoodHappy, "mood_happy-discogs-effnet-1", "happy"); // EffNet often uses "happy"/"not_happy" or similar? Need to verify class naming for "mood_happy" model. 
                // Metadata for mood_happy-discogs-effnet-1.json likely says "happy", "not_happy"
                float aggressiveProb = GetProb(data.HighLevel?.MoodAggressive, "mood_aggressive-discogs-effnet-1", "aggressive");

                // DEBUG: Log extracted data to verify what Essentia produced
                _logger.LogInformation("🎵 ESSENTIA EXTRACTION SUCCESS for {File}:", Path.GetFileName(filePath));
                _logger.LogInformation("   ├─ BPM: {Bpm} (confidence: {Conf})", 
                    data.Rhythm?.Bpm ?? 0, data.Rhythm?.BpmConfidence ?? 0);
                _logger.LogInformation("   ├─ Key: {Key} {Scale} (strength: {Strength})", 
                    data.Tonal?.KeyEdma?.Key ?? "Unknown", 
                    data.Tonal?.KeyEdma?.Scale ?? "Unknown",
                    data.Tonal?.KeyEdma?.Strength ?? 0);
                _logger.LogInformation("   ├─ Danceability: {Dance:F2} | Voice: {Voice:F2}", danceProb, voiceProb);
                _logger.LogInformation("   └─ Moods - Happy: {Happy:F2} | Aggressive: {Aggressive:F2}", happyProb, aggressiveProb);

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
                    // Phase 13A: Improved Energy mapping
                    // Refined for DnB: Mean * 5 (down from 10) and shifted loudness thresholds
                    Energy = Math.Clamp(
                        data.LowLevel?.Rms?.Mean * 5 ?? (data.LowLevel?.AverageLoudness > -7 ? 0.85f : (data.LowLevel?.AverageLoudness > -11 ? 0.7f : 0.5f)),
                        0f, 1f),
                    Danceability = Math.Clamp(danceProb > 0 ? danceProb : (data.Rhythm?.Danceability ?? 0), 0f, 1f),
                    // NEW: Intensity metric (composite of onset rate + spectral complexity)
                    Intensity = Math.Clamp(
                        ((data.Rhythm?.OnsetRate ?? 0) / 15f * 0.5f) + ((data.LowLevel?.SpectralComplexity?.Mean ?? 0) / 100f * 0.5f),
                        0f, 1f),
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
                    // Phase 13C: AI Layer (Vocals & Mood)
                    // Fix: Use heuristic if voice model returned 0 (likely missing)
                    InstrumentalProbability = voiceProb > 0 
                        ? 1.0f - voiceProb
                        : EstimateInstrumentalProbability(data.LowLevel, data.Rhythm),
                    MoodTag = DetermineMoodTag(data.HighLevel),
                    MoodConfidence = CalculateMoodConfidence(data.HighLevel),
                    
                    // Phase 21: AI Brain
                    Sadness = data.HighLevel?.MoodSad?.All?.Sad, // Directly capture Sad probability
                    Valence = 0.5f, // Neutral default

                    // Advanced Harmonic Mixing
                    // ChordProgression = FormatChordProgression(data.Tonal?.ChordsKey), // Commented out - ChordsKey now JsonElement
                    
                    // Metadata
                    AnalysisVersion = ANALYSIS_VERSION,
                    AnalyzedAt = DateTime.UtcNow
                };

                // Phase 16.2: Extract & Cache AI Embeddings
                if (data.HighLevel?.ExtensionData != null)
                {
                    foreach (var kvp in data.HighLevel.ExtensionData)
                    {
                        // Look for the discogs-effnet model output - prioritize the 128D embedding (BS64-1)
                        if (kvp.Key.Contains("discogs", StringComparison.OrdinalIgnoreCase) && 
                            kvp.Key.Contains("effnet", StringComparison.OrdinalIgnoreCase))
                        {
                            var embedding = ExtractEmbeddingFromJson(kvp.Value);
                            if (embedding != null && embedding.Length > 0)
                            {
                                // Prioritize the 128-dimensional embedding from the bs64-1 model
                                if (entity.VectorEmbedding == null || embedding.Length == 128)
                                {
                                    entity.AiEmbeddingJson = JsonSerializer.Serialize(embedding);
                                    
                                    // Phase 21: AI Brain Vector Storage
                                    entity.VectorEmbedding = embedding;

                                    // Calculate L2 Norm (Magnitude) for efficient Cosine Similarity
                                    entity.EmbeddingMagnitude = (float)Math.Sqrt(embedding.Sum(x => x * x));
                                    
                                    if (embedding.Length == 128)
                                    {
                                        _logger.LogDebug("🧠 UNIVERSAL: Extracted 128D AI Embedding from {Key}", kvp.Key);
                                    }
                                }
                            }
                        }
                        
                        // Phase 17: EDM Specialist - genre_electronic
                        if (kvp.Key.Contains("genre_electronic", StringComparison.OrdinalIgnoreCase))
                        {
                            var (subgenre, confidence) = ExtractElectronicSubgenre(kvp.Value);
                            entity.ElectronicSubgenre = subgenre;
                            entity.ElectronicSubgenreConfidence = confidence;
                            _logger.LogDebug("🎵 EDM Subgenre: {Subgenre} ({Confidence:P0})", subgenre, confidence);
                        }
                        
                        // Phase 17: EDM Specialist - tonal_atonal (DJ Tool Detection)
                        if (kvp.Key.Contains("tonal_atonal", StringComparison.OrdinalIgnoreCase))
                        {
                            var (tonal, atonal) = ExtractTonalAtonal(kvp.Value);
                            entity.TonalProbability = tonal;
                            entity.IsDjTool = atonal > 0.8f; // High atonal = DJ Tool / Drum Loop
                            _logger.LogDebug("🎵 Tonal: {Tonal:P0}, IsDjTool: {IsDjTool}", tonal, entity.IsDjTool);
                        }
                        
                        // Phase 17: EDM Specialist - arousal_valence OR emomusic (2D Vibe Map)
                        // arousal_valence preferred, emomusic as fallback
                        if (kvp.Key.Contains("arousal_valence", StringComparison.OrdinalIgnoreCase) ||
                            kvp.Key.Contains("emomusic", StringComparison.OrdinalIgnoreCase))
                        {
                            var (arousal, valence) = ExtractArousalValence(kvp.Value);
                            entity.Arousal = arousal;
                            entity.Valence = valence;
                            
                            // Derive MoodTag from arousal_valence quadrant
                            entity.MoodTag = MapArousalValenceToMood(arousal, valence);
                            entity.MoodConfidence = 0.8f; // Fixed confidence for continuous model
                            
                            _logger.LogDebug("🎵 Vibe Map: Arousal={Arousal}, Valence={Valence} -> {Mood}", 
                                arousal, valence, entity.MoodTag);
                        }

                        // Tier 3: VGGish Embeddings
                        if (kvp.Key.Contains("vggish", StringComparison.OrdinalIgnoreCase))
                        {
                            var embedding = ExtractEmbeddingFromJson(kvp.Value);
                            if (embedding != null && embedding.Length > 0)
                            {
                                entity.VggishEmbeddingJson = JsonSerializer.Serialize(embedding);
                                _logger.LogDebug("🎵 VGGish Embedding extracted ({Size} elements)", embedding.Length);
                            }
                        }

                        // Tier 3: CREPE Pitch Detection
                        if (kvp.Key.Contains("crepe", StringComparison.OrdinalIgnoreCase))
                        {
                            var (avgPitch, confidence) = ExtractCrepePitch(kvp.Value);
                            entity.AvgPitch = avgPitch;
                            entity.PitchConfidence = confidence;
                            _logger.LogDebug("🎵 CREPE Pitch: {Pitch:F1} Hz (Conf: {Conf:P0})", avgPitch, confidence);
                        }

                        // Tier 3: Audio Visualization Metrics (deepsquare)
                        if (kvp.Key.Contains("deepsquare", StringComparison.OrdinalIgnoreCase))
                        {
                            var vector = ExtractVisualizationVector(kvp.Value);
                            if (vector != null && vector.Length > 0)
                            {
                                entity.VisualizationVectorJson = JsonSerializer.Serialize(vector);
                                _logger.LogDebug("🎵 Visualization Vector extracted ({Size} elements)", vector.Length);
                            }
                        }
                    }
                }
                
                stopwatch.Stop();
                _logger.LogInformation("⏱ SIDECAR PERFORMANCE: {Tier} analysis for {Hash} took {Elapsed}ms", 
                    tier, trackUniqueHash, stopwatch.ElapsedMilliseconds);

                _forensicLogger.Info(cid, ForensicStage.MusicalAnalysis, 
                    $"Extracted ({tier}): {entity.Bpm:F1} BPM | Key: {entity.Key} {entity.Scale} | Dance: {entity.Danceability:F1} | Time: {stopwatch.ElapsedMilliseconds}ms", trackUniqueHash);

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

    /// <summary>
    /// Removes invalid control characters from JSON string to prevent parsing errors.
    /// Preserves standard whitespace (Tab \t, LineFeed \n, CarriageReturn \r).
    /// </summary>
    private static string SanitizeJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;
        // Regex matches control characters (0-31) EXCEPT 9 (\t), 10 (\n), 13 (\r)
        return System.Text.RegularExpressions.Regex.Replace(json, @"[\u0000-\u0008\u000B\u000C\u000E-\u001F]", string.Empty);
    }

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

    /// <summary>
    /// Heuristic fallback for vocal/instrumental classification when TensorFlow models unavailable.
    /// Uses spectral characteristics that correlate with vocal presence:
    /// - Higher spectral centroid often indicates vocals (human voice is spectrally bright)
    /// - Lower dynamic complexity often indicates more instrumental content
    /// - Higher spectral complexity typically means more instrumental layering
    /// Returns value 0-1 where 0=fully vocal, 1=fully instrumental
    /// </summary>
    private static float EstimateInstrumentalProbability(LowLevelData? lowLevel, RhythmData? rhythm)
    {
        if (lowLevel == null) return 0.5f; // Default to uncertain
        
        float instrumental = 0.5f; // Start neutral
        
        // Spectral Centroid: Human voice typically has centroid 1000-4000 Hz
        // Higher values suggest more instrumental content (synths, percussion)
        var centroid = lowLevel.SpectralCentroid?.Mean ?? 0;
        if (centroid > 4000) instrumental += 0.15f;      // High = more instrumental
        else if (centroid > 2000) instrumental += 0.0f;  // Voice range = neutral
        else if (centroid > 500) instrumental -= 0.1f;   // Mid-low = possibly vocal
        
        // Dynamic Complexity: Vocals tend to have moderate dynamics
        // Very low dynamics (brickwalled) often = instrumental EDM
        var dynamics = lowLevel.DynamicComplexity;
        if (dynamics < 1.5f) instrumental += 0.1f;  // Crushed = likely instrumental
        else if (dynamics > 4.0f) instrumental -= 0.1f; // High dynamics = possibly vocal
        
        // Spectral Complexity: More layers often = instrumental
        var complexity = lowLevel.SpectralComplexity?.Mean ?? 0;
        if (complexity > 40) instrumental += 0.1f;
        else if (complexity < 15) instrumental -= 0.1f;
        
        // Onset Rate: Vocals have fewer distinct onsets than drums/synths
        // Increased threshold for DnB (usually 15-20)
        var onsets = rhythm?.OnsetRate ?? 0;
        if (onsets > 15) instrumental += 0.1f;   // Very Busy = instrumental
        else if (onsets < 3) instrumental -= 0.05f; // Sparse = possibly vocal ballad
        
        return Math.Clamp(instrumental, 0.0f, 1.0f);
    }

    private static string DetermineMoodTag(HighLevelData? highLevel)
    {
        if (highLevel == null) return string.Empty;

        // Aggregate mood model outputs to determine primary mood
        var moods = new Dictionary<string, float>
        {
            ["Happy"] = highLevel.MoodHappy?.All?.Happy ?? 0,
            ["Aggressive"] = highLevel.MoodAggressive?.All?.Aggressive ?? 0,
            ["Sad"] = highLevel.MoodSad?.All?.Sad ?? 0,
            ["Relaxed"] = highLevel.MoodRelaxed?.All?.Relaxed ?? 0,
            ["Party"] = highLevel.MoodParty?.All?.Party ?? 0,
            ["Electronic"] = highLevel.MoodElectronic?.All?.Electronic ?? 0
        };

        // Return highest confidence mood if above 0.4 threshold
        var topMood = moods.OrderByDescending(m => m.Value).FirstOrDefault();
        return topMood.Value > 0.4f ? topMood.Key : "Neutral";
    }

    private static float CalculateMoodConfidence(HighLevelData? highLevel)
    {
        if (highLevel == null) return 0f;

        // Return the highest probability among all mood predictions
        return new[]
        {
            highLevel.MoodHappy?.Probability ?? 0,
            highLevel.MoodAggressive?.Probability ?? 0,
            highLevel.MoodSad?.Probability ?? 0,
            highLevel.MoodRelaxed?.Probability ?? 0,
            highLevel.MoodParty?.Probability ?? 0,
            highLevel.MoodElectronic?.Probability ?? 0
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

    // ============================================
    // Phase 17: EDM Specialist Model Helpers
    // ============================================

    /// <summary>
    /// Extracts electronic subgenre from genre_electronic model output.
    /// </summary>
    private static (string subgenre, float confidence) ExtractElectronicSubgenre(JsonElement element)
    {
        try
        {
            // Structure: { "all": { "dnb": 0.95, "house": 0.02, "techno": 0.01, ... } }
            if (element.TryGetProperty("all", out var allProp))
            {
                var genres = new Dictionary<string, float>
                {
                    ["DnB"] = TryGetFloat(allProp, "dnb") ?? TryGetFloat(allProp, "drum_and_bass") ?? 0,
                    ["House"] = TryGetFloat(allProp, "house") ?? 0,
                    ["Techno"] = TryGetFloat(allProp, "techno") ?? 0,
                    ["Trance"] = TryGetFloat(allProp, "trance") ?? 0,
                    ["Ambient"] = TryGetFloat(allProp, "ambient") ?? 0
                };

                var top = genres.OrderByDescending(g => g.Value).First();
                return (top.Key, top.Value);
            }
        }
        catch { }
        return ("Unknown", 0);
    }

    /// <summary>
    /// Extracts tonal/atonal probabilities from tonal_atonal model output.
    /// </summary>
    private static (float tonal, float atonal) ExtractTonalAtonal(JsonElement element)
    {
        try
        {
            // Structure: { "all": { "tonal": 0.7, "atonal": 0.3 } }
            if (element.TryGetProperty("all", out var allProp))
            {
                float tonal = TryGetFloat(allProp, "tonal") ?? 0.5f;
                float atonal = TryGetFloat(allProp, "atonal") ?? 0.5f;
                return (tonal, atonal);
            }
        }
        catch { }
        return (0.5f, 0.5f);
    }

    /// <summary>
    /// Extracts arousal/valence values from arousal_valence model output.
    /// </summary>
    private static (float arousal, float valence) ExtractArousalValence(JsonElement element)
    {
        try
        {
            // Structure: { "all": { "arousal": 6.5, "valence": 3.2 } }
            // Range is typically 1-9
            if (element.TryGetProperty("all", out var allProp))
            {
                float arousal = TryGetFloat(allProp, "arousal") ?? 5f;
                float valence = TryGetFloat(allProp, "valence") ?? 5f;
                
                // Muse dataset uses 1-9 scale. Normalize to 0-1.
                // 1=Low, 5=Neutral, 9=High
                return ((arousal - 1f) / 8f, (valence - 1f) / 8f);
            }
        }
        catch { }
        return (0.5f, 0.5f); // Neutral default (Normalized)
    }

    /// <summary>
    /// Extracts pitch and confidence from CREPE model output.
    /// </summary>
    private static (float frequency, float confidence) ExtractCrepePitch(JsonElement element)
    {
        try
        {
            // CREPE often outputs time-varying arrays. We'll take the mean.
            if (element.TryGetProperty("all", out var allProp))
            {
                // Try to find frequency and confidence arrays
                if (allProp.TryGetProperty("frequency", out var freqProp) && freqProp.ValueKind == JsonValueKind.Array)
                {
                    var frequencies = freqProp.EnumerateArray().Select(x => x.GetSingle()).ToList();
                    var confidenceProp = allProp.TryGetProperty("confidence", out var confProp) && confProp.ValueKind == JsonValueKind.Array
                        ? confProp.EnumerateArray().Select(x => x.GetSingle()).ToList()
                        : null;

                    if (frequencies.Count > 0)
                    {
                        // Weighted average by confidence if available
                        if (confidenceProp != null && confidenceProp.Count == frequencies.Count)
                        {
                            float totalWeight = confidenceProp.Sum();
                            if (totalWeight > 0)
                            {
                                float weightedSum = 0;
                                for (int i = 0; i < frequencies.Count; i++)
                                    weightedSum += frequencies[i] * confidenceProp[i];
                                return (weightedSum / totalWeight, confidenceProp.Average());
                            }
                        }
                        return (frequencies.Average(), confidenceProp?.Average() ?? 0.5f);
                    }
                }
            }
        }
        catch { }
        return (0, 0);
    }

    /// <summary>
    /// Extracts visualization vector from deepsquare model output.
    /// </summary>
    private static float[]? ExtractVisualizationVector(JsonElement element)
    {
        try
        {
            // deepsquare outputs a 16-element vector
            if (element.TryGetProperty("all", out var allProp) && allProp.TryGetProperty("activations", out var actProp))
            {
                if (actProp.ValueKind == JsonValueKind.Array)
                {
                    return actProp.EnumerateArray().Select(x => x.GetSingle()).ToArray();
                }
            }
            
            // Fallback for different naming
            if (element.TryGetProperty("all", out var allFallback))
            {
                 var firstArray = allFallback.EnumerateObject()
                    .FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.Array).Value;
                 if (firstArray.ValueKind == JsonValueKind.Array)
                     return firstArray.EnumerateArray().Select(x => x.GetSingle()).ToArray();
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Maps arousal/valence coordinates to a mood tag.
    /// Based on Russell's Circumplex Model of Affect.
    /// </summary>
    private static string MapArousalValenceToMood(float arousal, float valence)
    {
        // Normalize to 0-1 range (assuming input is 1-9)
        float a = (arousal - 1) / 8f;
        float v = (valence - 1) / 8f;

        // Quadrant mapping:
        // High Arousal + High Valence = Energetic/Happy (Festival)
        // High Arousal + Low Valence = Tense/Aggressive (Dark Techno)
        // Low Arousal + High Valence = Calm/Peaceful (Chill/Sunset)
        // Low Arousal + Low Valence = Sad/Depressed (Downtempo)

        if (a > 0.6f && v > 0.6f) return "Festival";
        if (a > 0.6f && v < 0.4f) return "Dark";
        if (a < 0.4f && v > 0.6f) return "Chill";
        if (a < 0.4f && v < 0.4f) return "Melancholic";
        if (a > 0.5f) return "Energetic";
        if (v > 0.5f) return "Uplifting";
        return "Neutral";
    }

    /// <summary>
    /// Safely extracts a float from a JsonElement property.
    /// </summary>
    private static float? TryGetFloat(JsonElement element, string propertyName)
    {
        try
        {
            if (element.TryGetProperty(propertyName, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number)
                    return prop.GetSingle();
            }
        }
        catch { }
        return null;
    }


    /// <summary>
    /// Extracts probability of a specific class from a model output in ExtensionData.
    /// Used when model names don't match standard properties (e.g. danceability-discogs-effnet-1).
    /// </summary>
    private static float ExtractModelProbability(
        Dictionary<string, System.Text.Json.JsonElement>? extensionData, 
        string modelKeyFragment, 
        string classKey)
    {
        if (extensionData == null) return 0f;

        try
        {
            // Find key containing the fragment (e.g. "danceability-discogs-effnet-1")
            var match = extensionData.FirstOrDefault(k => k.Key.Contains(modelKeyFragment, StringComparison.OrdinalIgnoreCase));
            if (match.Key == null) return 0f;

            var element = match.Value;

            // Structure usually: { "all": { "classA": 0.X, "classB": 0.Y }, ... }
            if (element.TryGetProperty("all", out var allProp))
            {
                if (allProp.TryGetProperty(classKey, out var probProp) && probProp.TryGetSingle(out var prob))
                {
                    return prob;
                }
            }
            
            // Or maybe direct?
            if (element.TryGetProperty(classKey, out var directProp) && directProp.TryGetSingle(out var directProb))
            {
                return directProb;
            }
        }
        catch { }

        return 0f;
    }


    public void Dispose()
    {
        // _isDisposing = true;
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
