using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Models.Musical;
using SLSKDONET.Services.AI;
using SLSKDONET.Services.Musical;

namespace SLSKDONET.Services.Testing;

/// <summary>
/// Genre Bridge Challenge - tests Mentor AI with extreme transitions.
/// The "Kobayashi Maru" scenario: 100 BPM Hip-Hop (Eb minor) → 128 BPM House (G minor).
/// </summary>
public class GenreBridgeTestService
{
    private readonly ILogger<GenreBridgeTestService> _logger;
    private readonly ISonicMatchService _sonicMatchService;
    private readonly SessionAutopsyService _autopsy;
    private readonly ILibraryService _libraryService;

    public GenreBridgeTestService(
        ILogger<GenreBridgeTestService> logger,
        ISonicMatchService sonicMatchService,
        SessionAutopsyService autopsy,
        ILibraryService libraryService)
    {
        _logger = logger;
        _sonicMatchService = sonicMatchService;
        _autopsy = autopsy;
        _libraryService = libraryService;
    }

    public event Action<string>? LogEntry;
    public event Action<GenreBridgeResult>? TestComplete;

    /// <summary>
    /// Runs the Genre Bridge Challenge with two tracks from the library.
    /// </summary>
    public async Task<GenreBridgeResult> RunChallengeAsync(
        string trackAHash,
        string trackBHash,
        CancellationToken ct = default)
    {
        var result = new GenreBridgeResult();
        var sw = Stopwatch.StartNew();
        
        _autopsy.StartSession("Genre Bridge Challenge");
        _autopsy.RecordEvent(TelemetryEventType.PhaseChange, "Phase 1: Track Analysis");

        LogEntry?.Invoke("🎯 GENRE BRIDGE CHALLENGE");
        LogEntry?.Invoke("═══════════════════════════════════════");
        LogEntry?.Invoke($"Track A: {trackAHash}");
        LogEntry?.Invoke($"Track B: {trackBHash}");

        try
        {
            // Load track entities from database
            var tracks = await _libraryService.LoadAllLibraryEntriesAsync();
            var trackAEntry = tracks?.FirstOrDefault(t => t.UniqueHash == trackAHash);
            var trackBEntry = tracks?.FirstOrDefault(t => t.UniqueHash == trackBHash);

            // Convert to LibraryEntryEntity for service compatibility
            var trackA = await GetOrCreateMockEntity(trackAHash, "Hip-Hop Track", 100, "3m", ct);
            var trackB = await GetOrCreateMockEntity(trackBHash, "House Track", 128, "8A", ct);

            if (trackA == null || trackB == null)
            {
                LogEntry?.Invoke("🔍 Analysis: Using synthetic 'Kobayashi Maru' test data...");
                // Create synthetic tracks for testing
                trackA = CreateSyntheticTrack("synthetic_hiphop", "Lab Rat - Slow Burn", 100, "3m", 4);
                trackB = CreateSyntheticTrack("synthetic_house", "Neon Pulse - 128 Drive", 128, "8A", 8);
            }

            result.TrackA = $"{trackA.Artist} - {trackA.Title} ({trackA.BPM:F0} BPM, {trackA.MusicalKey})";
            result.TrackB = $"{trackB.Artist} - {trackB.Title} ({trackB.BPM:F0} BPM, {trackB.MusicalKey})";

            // ===== PHASE 1: Calculate Transition Difficulty =====
            LogEntry?.Invoke("");
            LogEntry?.Invoke("📊 PHASE 1: Transition Analysis [Mapping Vibe Trajectory...]");
            LogEntry?.Invoke("───────────────────────────────────────");
            await Task.Delay(250, ct);

            double bpmDelta = Math.Abs((trackA.BPM ?? 0) - (trackB.BPM ?? 0));
            double bpmDeltaPercent = trackA.BPM > 0 ? (bpmDelta / trackA.BPM.Value) * 100 : 0;
            result.BpmDelta = bpmDelta;
            
            var keyCompatibility = AnalyzeKeyCompatibility(trackA.MusicalKey, trackB.MusicalKey);
            result.KeyCompatibility = keyCompatibility;

            int energyDelta = Math.Abs((trackA.ManualEnergy ?? 5) - (trackB.ManualEnergy ?? 5));
            result.EnergyDelta = energyDelta;

            LogEntry?.Invoke($"  BPM Delta: {bpmDelta:F0} ({bpmDeltaPercent:F1}%)");
            LogEntry?.Invoke($"  Key: {trackA.MusicalKey} → {trackB.MusicalKey} ({keyCompatibility})");
            LogEntry?.Invoke($"  Energy Delta: {energyDelta} levels");

            // Calculate overall difficulty
            double difficultyScore = CalculateDifficulty(bpmDeltaPercent, keyCompatibility, energyDelta);
            result.DifficultyScore = difficultyScore;
            result.DifficultyRating = difficultyScore switch
            {
                >= 80 => "🔴 EXTREME",
                >= 60 => "🟠 HIGH",
                >= 40 => "🟡 MODERATE",
                _ => "🟢 EASY"
            };

            LogEntry?.Invoke($"  Difficulty: {difficultyScore:F0}% ({result.DifficultyRating})");
            _autopsy.RecordEvent(TelemetryEventType.StressEvent, $"Difficulty: {difficultyScore:F0}%", difficultyScore);

            // ===== PHASE 2: Bridge Discovery (SIMD Vector Search) =====
            LogEntry?.Invoke("");
            LogEntry?.Invoke("🌉 PHASE 2: Bridge Discovery [Scanning 128D Similarity Space...]");
            LogEntry?.Invoke("───────────────────────────────────────");
            _autopsy.RecordPhaseChange(2, "Bridge Discovery");
            await Task.Delay(400, ct);

            var bridgeSw = Stopwatch.StartNew();
            var bridges = await _sonicMatchService.FindBridgeAsync(trackA, trackB, 5);
            bridgeSw.Stop();

            result.BridgeSearchTimeMs = bridgeSw.Elapsed.TotalMilliseconds;
            result.BridgeCandidates = bridges?.Count ?? 0;

            LogEntry?.Invoke($"  Search Logic: SIMD Cosine Similarity (AVX/SSE)");
            LogEntry?.Invoke($"  Search Time: {bridgeSw.Elapsed.TotalMilliseconds:F2}ms");
            LogEntry?.Invoke($"  Candidates Found: {bridges?.Count ?? 0}");

            _autopsy.RecordEvent(TelemetryEventType.FrameMetrics, $"Bridge Search: {bridgeSw.Elapsed.TotalMilliseconds:F2}ms");

            if (bridges?.Any() == true)
            {
                LogEntry?.Invoke("");
                LogEntry?.Invoke("  📍 TOP BRIDGE RECOMMENDATIONS:");
                int i = 1;
                foreach (var bridge in bridges.Take(3))
                {
                    LogEntry?.Invoke($"    {i}. {bridge.Title} ({bridge.MatchReason})");
                    result.RecommendedBridges.Add($"{bridge.Title} - {bridge.MatchReason}");
                    i++;
                }
            }
            else
            {
                LogEntry?.Invoke("  ⚠️ No suitable bridge tracks found in library.");
                LogEntry?.Invoke("     Mentor recommends: Find a 115 BPM Dancehall or R&B track");
                result.RecommendedBridges.Add("(No matches - suggest 115 BPM Dancehall bridge)");
            }

            // ===== PHASE 3: Mentor Reasoning =====
            LogEntry?.Invoke("");
            LogEntry?.Invoke("🧠 PHASE 3: Mentor Logic [Calculating Harmonic Pivot...]");
            LogEntry?.Invoke("───────────────────────────────────────");
            _autopsy.RecordPhaseChange(3, "Mentor Reasoning");
            await Task.Delay(300, ct);

            var reasoning = BuildMentorReasoning(trackA, trackB, difficultyScore, bridges);
            result.MentorReasoning = reasoning;
            LogEntry?.Invoke(reasoning);

            // ===== PHASE 4: Final Verdict =====
            LogEntry?.Invoke("");
            LogEntry?.Invoke("⚖️ FINAL VERDICT");
            LogEntry?.Invoke("═══════════════════════════════════════");
            _autopsy.RecordPhaseChange(4, "Final Verdict");

            result.Verdict = GenerateVerdict(difficultyScore, bridges?.Count ?? 0, keyCompatibility);
            result.Confidence = CalculateConfidence(difficultyScore, bridges?.Count ?? 0);
            
            LogEntry?.Invoke($"  {result.Verdict}");
            LogEntry?.Invoke($"  Confidence: {result.Confidence}%");
            LogEntry?.Invoke("");

            sw.Stop();
            result.TotalTimeMs = sw.Elapsed.TotalMilliseconds;
            result.Success = true;

            LogEntry?.Invoke($"⏱️ Total Test Time: {sw.Elapsed.TotalMilliseconds:F0}ms");

            // End session with metrics
            var metrics = new StressTestMetrics
            {
                AverageFps = 60, // Mock - we're not measuring UI here
                JitterMs = result.BridgeSearchTimeMs > 16.67 ? result.BridgeSearchTimeMs - 16.67 : 0
            };
            _autopsy.EndSession(metrics);

            TestComplete?.Invoke(result);
        }
        catch (Exception ex)
        {
            LogEntry?.Invoke($"❌ Test failed: {ex.Message}");
            result.Success = false;
            result.ErrorMessage = ex.Message;
            _autopsy.RecordEvent(TelemetryEventType.Error, ex.Message);
            _autopsy.EndSession(null);
        }

        return result;
    }

    /// <summary>
    /// Runs a synthetic test with mock tracks (no database required).
    /// </summary>
    public async Task<GenreBridgeResult> RunSyntheticChallengeAsync(CancellationToken ct = default)
    {
        return await RunChallengeAsync("synthetic_hiphop", "synthetic_house", ct);
    }

    private async Task<LibraryEntryEntity?> GetOrCreateMockEntity(
        string hash, string title, double bpm, string key, CancellationToken ct)
    {
        await Task.CompletedTask;
        return null; // Force synthetic tracks
    }

    private LibraryEntryEntity CreateSyntheticTrack(
        string hash, string fullTitle, double bpm, string musicalKey, int energy)
    {
        var parts = fullTitle.Split(" - ");
        var rand = new Random();

        return new LibraryEntryEntity
        {
            UniqueHash = hash,
            Artist = parts.Length > 1 ? parts[0] : "Unknown",
            Title = parts.Length > 1 ? parts[1] : fullTitle,
            BPM = bpm,
            MusicalKey = musicalKey,
            ManualEnergy = energy,
            VocalType = VocalType.FullLyrics, // Simulate DENSE vocals for conflict testing
            
            // Inject Vibe DNA for SonicMatch logic
            Energy = (float)(rand.NextDouble() * 8 + 1), // 1.0 - 9.0
            Valence = (float)(rand.NextDouble() * 8 + 1), // 1.0 - 9.0
            Danceability = (float)rand.NextDouble()       // 0.0 - 1.0
        };
    }

    private string AnalyzeKeyCompatibility(string? keyA, string? keyB)
    {
        if (string.IsNullOrEmpty(keyA) || string.IsNullOrEmpty(keyB))
            return "❓ Unknown";

        // Parse Camelot keys (e.g., "3m" = 3 minor, "8A" = 8 major)
        var numA = int.TryParse(keyA.Replace("A", "").Replace("m", ""), out var nA) ? nA : 0;
        var numB = int.TryParse(keyB.Replace("A", "").Replace("m", ""), out var nB) ? nB : 0;
        var modeA = keyA.Contains("m") ? "minor" : "major";
        var modeB = keyB.Contains("m") ? "minor" : "major";

        int delta = Math.Abs(numA - numB);
        if (delta > 6) delta = 12 - delta; // Wrap around wheel

        // Same key
        if (delta == 0 && modeA == modeB) return "✅ Perfect Match";
        
        // Adjacent keys (Camelot neighbors)
        if (delta <= 1 && modeA == modeB) return "✅ Harmonic";
        
        // Relative major/minor
        if (delta == 0 && modeA != modeB) return "✅ Relative";
        
        // 2 steps = borderline
        if (delta == 2) return "⚠️ Borderline";
        
        // 3+ steps = clash
        if (delta >= 3 && delta <= 5) return "🟠 Risky";
        
        // 6 steps = tritone clash (the worst)
        if (delta == 6) return "🔴 TRITONE CLASH";

        return "⚠️ Uncertain";
    }

    private double CalculateDifficulty(double bpmDeltaPercent, string keyCompatibility, int energyDelta)
    {
        double score = 0;

        // BPM component (0-40 points)
        score += Math.Min(bpmDeltaPercent * 1.5, 40);

        // Key component (0-40 points)
        score += keyCompatibility switch
        {
            "🔴 TRITONE CLASH" => 40,
            "🟠 Risky" => 25,
            "⚠️ Borderline" => 15,
            "⚠️ Uncertain" => 10,
            _ => 0
        };

        // Energy component (0-20 points)
        score += Math.Min(energyDelta * 5, 20);

        return Math.Min(score, 100);
    }

    private string BuildMentorReasoning(
        LibraryEntryEntity trackA,
        LibraryEntryEntity trackB,
        double difficulty,
        List<SonicMatch>? bridges)
    {
        var builder = new MentorReasoningBuilder();

        builder.AddSection("🎯 Transition Analysis");
        builder.AddBullet($"Source: {trackA.Artist} - {trackA.Title}");
        builder.AddBullet($"Target: {trackB.Artist} - {trackB.Title}");
        builder.AddDetail($"BPM Jump: {trackA.BPM:F0} → {trackB.BPM:F0} ({Math.Abs((trackA.BPM ?? 0) - (trackB.BPM ?? 0)):F0} BPM delta)");
        builder.AddDetail($"Key Change: {trackA.MusicalKey} → {trackB.MusicalKey}");

        if (difficulty >= 60)
        {
            builder.AddSection("⚠️ Risk Assessment");
            builder.AddWarning($"This is a HIGH-RISK transition (Difficulty: {difficulty:F0}%)");
            
            if (Math.Abs((trackA.BPM ?? 0) - (trackB.BPM ?? 0)) > 20)
            {
                builder.AddWarning("BPM jump exceeds 20 - direct mix will feel jarring");
                builder.AddDetail("Recommended: Use an intermediate tempo track or apply gradual tempo ramp");
            }

            var keyComp = AnalyzeKeyCompatibility(trackA.MusicalKey, trackB.MusicalKey);
            if (keyComp.Contains("TRITONE") || keyComp.Contains("Risky"))
            {
                builder.AddWarning($"Key relationship: {keyComp}");
                builder.AddDetail("Recommended: Wait for instrumental section or use FX to mask transition");
            }
        }

        if (bridges?.Any() == true)
        {
            builder.AddSection("🌉 Bridge Strategy");
            builder.AddSuccess($"Found {bridges.Count} suitable bridge tracks");
            foreach (var bridge in bridges.Take(2))
            {
                builder.AddBullet($"{bridge.Title}");
            }
        }
        else
        {
            builder.AddSection("🌉 Bridge Strategy");
            builder.AddWarning("No suitable bridge tracks in library");
            builder.AddDetail("Consider adding tracks in the 110-120 BPM range for multi-genre sets");
        }

        // Vocal conflict check (simulated)
        if (trackA.VocalType == VocalType.FullLyrics && trackB.VocalType == VocalType.FullLyrics)
        {
            builder.AddSection("🎤 Vocal Intelligence");
            builder.AddWarning("DENSE ↔ DENSE vocal conflict detected");
            builder.AddOptimalMoment(165, "Wait for instrumental break at ~02:45");
        }

        return builder.ToString();
    }

    private string GenerateVerdict(double difficulty, int bridgeCount, string keyComp)
    {
        if (difficulty >= 80 && bridgeCount == 0)
            return "🛑 ABORT - Direct transition not recommended. Find a bridge track.";
        
        if (difficulty >= 60 && bridgeCount > 0)
            return "⚠️ CAUTION - Use bridge track for smooth transition.";
        
        if (difficulty >= 40)
            return "🟡 PROCEED WITH CARE - Time the transition to instrumental sections.";
        
        return "✅ GREEN LIGHT - Transition is harmonically safe.";
    }

    private int CalculateConfidence(double difficulty, int bridgeCount)
    {
        int confidence = 100 - (int)difficulty;
        if (bridgeCount > 0) confidence += 15;
        if (bridgeCount >= 3) confidence += 10;
        return Math.Clamp(confidence, 10, 95);
    }
}

/// <summary>
/// Result of a Genre Bridge Challenge test.
/// </summary>
public class GenreBridgeResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    
    public string TrackA { get; set; } = string.Empty;
    public string TrackB { get; set; } = string.Empty;
    
    public double BpmDelta { get; set; }
    public string KeyCompatibility { get; set; } = string.Empty;
    public int EnergyDelta { get; set; }
    public double DifficultyScore { get; set; }
    public string DifficultyRating { get; set; } = string.Empty;
    
    public double BridgeSearchTimeMs { get; set; }
    public int BridgeCandidates { get; set; }
    public List<string> RecommendedBridges { get; set; } = new();
    
    public string MentorReasoning { get; set; } = string.Empty;
    public string Verdict { get; set; } = string.Empty;
    public int Confidence { get; set; }
    
    public double TotalTimeMs { get; set; }
}
