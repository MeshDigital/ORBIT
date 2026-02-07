using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Models.Musical;
using SLSKDONET.Services.Musical;

namespace SLSKDONET.Services.Analysis
{
    /// <summary>
    /// Phase 5.4: Setlist Stress-Test Service
    /// Deep-scans entire DJ setlist to identify dead-ends, energy plateaus, and vocal conflicts.
    /// Provides weighted severity scoring, rescue suggestions, and mentor-style narration.
    ///
    /// CRITICAL ALGORITHMS:
    /// 1. Energy Plateau Detection: Gradient-based with vocal-type awareness
    /// 2. Rescue Track Selection: Weighted scoring (Energy 30%, Tempo 30%, Harmonic 40%)
    /// 3. Severity Calculation: Composite weighted formula across all failure dimensions
    /// </summary>
    public class SetlistStressTestService
    {
        private readonly ILibraryService _libraryService;
        private readonly HarmonicMatchService _harmonicService;
        private readonly AppDbContext _dbContext;

        // Configurable thresholds
        private const double ENERGY_PLATEAU_THRESHOLD = 0.03;     // <3% gradient = plateau
        private const double ENERGY_PLATEAU_WINDOW = 4;            // Check over 4 consecutive tracks
        private const double HARMONIC_COMPATIBILITY_THRESHOLD = 0.7;
        private const double BPM_TOLERANCE_PERCENT = 0.06;         // ±6%
        private const double ENERGY_COMFORT_RANGE = 0.15;          // ±15% energy difference acceptable

        public SetlistStressTestService(
            ILibraryService libraryService,
            HarmonicMatchService harmonicService,
            AppDbContext dbContext)
        {
            _libraryService = libraryService;
            _harmonicService = harmonicService;
            _dbContext = dbContext;
        }

        /// <summary>
        /// Executes comprehensive stress-test on entire setlist.
        /// Scans all transitions, calculates severity, identifies weak links, suggests rescues.
        /// Returns full diagnostic report with mentoring narrative.
        /// </summary>
        public async Task<StressDiagnosticReport> RunDiagnosticAsync(SetListEntity setlist)
        {
            if (setlist?.Tracks == null || setlist.Tracks.Count < 2)
            {
                return new StressDiagnosticReport
                {
                    SetListId = setlist?.Id ?? Guid.Empty,
                    OverallHealthScore = 100,
                    QuickSummary = "Setlist too short to analyze (minimum 2 tracks required)."
                };
            }

            var report = new StressDiagnosticReport
            {
                SetListId = setlist.Id,
                AnalyzedAt = DateTime.UtcNow
            };

            var tracks = setlist.Tracks.OrderBy(t => t.Position).ToList();
            var libraryTracks = await _dbContext.LibraryEntries.AsNoTracking().ToListAsync();
            var trackMap = libraryTracks.ToDictionary(t => t.UniqueHash);

            // Scan all transitions
            for (int i = 0; i < tracks.Count - 1; i++)
            {
                var trackA = trackMap.GetValueOrDefault(tracks[i].TrackUniqueHash);
                var trackB = trackMap.GetValueOrDefault(tracks[i + 1].TrackUniqueHash);

                if (trackA == null || trackB == null)
                    continue;

                var stressPoint = await AnalyzeTransitionAsync(
                    trackA, trackB, i, i + 1, tracks, libraryTracks);

                report.StressPoints.Add(stressPoint);
                report.SetlistDurationSeconds += (trackA.DurationSeconds ?? 0);
            }

            // Calculate overall health
            if (report.StressPoints.Count > 0)
            {
                double avgSeverity = report.StressPoints.Average(s => s.SeverityScore);
                report.OverallHealthScore = Math.Max(0, 100 - (int)avgSeverity);
            }

            // Generate mentoring narrative
            report.SetlistNarrativeMentoring = GenerateSetlistNarrative(report, tracks, trackMap);
            report.QuickSummary = GenerateQuickSummary(report);

            return report;
        }

        /// <summary>
        /// Analyzes a single transition (Track[i] → Track[i+1])
        /// Calculates severity score, identifies failure type, suggests rescues.
        /// </summary>
        public Task<TransitionStressPoint> AnalyzeTransitionAsync(LibraryEntryEntity trackA, LibraryEntryEntity trackB)
        {
            return AnalyzeTransitionAsync(trackA, trackB, 0, 1, new List<SetTrackEntity>(), new List<LibraryEntryEntity>());
        }

        /// <summary>
        /// Analyzes a single transition (Track[i] → Track[i+1])
        /// Calculates severity score, identifies failure type, suggests rescues.
        /// </summary>
        public async Task<TransitionStressPoint> AnalyzeTransitionAsync(
            LibraryEntryEntity trackA,
            LibraryEntryEntity trackB,
            int idxA,
            int idxB,
            List<SetTrackEntity> fullSetlist,
            List<LibraryEntryEntity> libraryTracks)
        {
            var stressPoint = new TransitionStressPoint
            {
                FromTrackIndex = idxA,
                ToTrackIndex = idxB
            };

            // Dimension 1: Energy Analysis (25% weight)
            var energyScore = AnalyzeEnergyCompatibility(trackA, trackB, fullSetlist, idxA);

            // Dimension 2: Harmonic Analysis (35% weight)
            var harmonicScore = AnalyzeHarmonicCompatibility(trackA, trackB);

            // Dimension 3: Vocal Safety (30% weight)
            var vocalScore = AnalyzeVocalCompatibility(trackA, trackB);

            // Dimension 4: Tempo Compatibility (10% weight)
            var tempoScore = AnalyzeTempoCompatibility(trackA, trackB);

            // Composite Severity Score (inverse: higher = worse)
            stressPoint.SeverityScore = (int)Math.Round(
                (energyScore * 0.25) +
                (harmonicScore * 0.35) +
                (vocalScore * 0.30) +
                (tempoScore * 0.10)
            );

            // Determine Primary Failure Type
            (stressPoint.PrimaryFailure, stressPoint.PrimaryProblem) = IdentifyPrimaryFailure(
                energyScore, harmonicScore, vocalScore, tempoScore);

            // Generate failure reasoning
            stressPoint.FailureReasoning = GenerateTransitionReasoning(
                trackA, trackB, energyScore, harmonicScore, vocalScore, tempoScore);

            // Find rescue suggestions (only if severity > 40)
            if (stressPoint.SeverityScore >= 40)
            {
                stressPoint.RescueSuggestions = await FindRescueTracksAsync(
                    trackA, trackB, libraryTracks, stressPoint.PrimaryFailure);
            }

            return stressPoint;
        }

        /// <summary>
        /// ALGORITHM 1: Energy Compatibility Analysis
        /// Gradient-based plateau detection with vocal-type awareness
        /// Returns severity score (0-100): 0 = perfect, 100 = critical
        /// </summary>
        private int AnalyzeEnergyCompatibility(
            LibraryEntryEntity trackA,
            LibraryEntryEntity trackB,
            List<SetTrackEntity> fullSetlist,
            int currentIndex)
        {
            double energyDelta = Math.Abs((trackB.Energy ?? 0) - (trackA.Energy ?? 0));

            // Check if we're in an energy plateau (gradient < 3% over 4 tracks)
            bool isEnergyPlateau = false;
            if (currentIndex >= 1 && currentIndex + 2 < fullSetlist.Count)
            {
                isEnergyPlateau = DetectEnergyPlateau(fullSetlist, currentIndex);
            }

            // Determine tolerance based on vocal type
            // Vocal plateaus are acceptable; instrumental plateaus signal boring flow
            double tolerance = ENERGY_COMFORT_RANGE;
            if (trackA.VocalType != VocalType.Instrumental &&
                trackB.VocalType != VocalType.Instrumental)
            {
                // Both vocal: higher tolerance (soulful sections are intentional)
                tolerance = 0.25;
            }

            // Score calculation
            if (energyDelta < 0.05 && !isEnergyPlateau)
            {
                // Subtle smooth transition (ideal)
                return 0;
            }
            else if (energyDelta < tolerance)
            {
                // Acceptable range
                return 15;
            }
            else if (isEnergyPlateau && trackA.VocalType == VocalType.Instrumental)
            {
                // Boring instrumental plateau = medium warning
                return 50;
            }
            else if (energyDelta > 0.4)
            {
                // Abrupt energy jump (requires careful mixing)
                return 65;
            }

            return 30;
        }

        /// <summary>
        /// Detects energy plateau: gradient < 3% per track over 4-track window
        /// </summary>
        private bool DetectEnergyPlateau(List<SetTrackEntity> fullSetlist, int centerIndex)
        {
            const int window = 4;
            int start = Math.Max(0, centerIndex - window / 2);
            int end = Math.Min(fullSetlist.Count - 1, centerIndex + window / 2);

            if (end - start < window - 1) return false;

            double maxEnergy = -1.0, minEnergy = 2.0;
            bool foundData = false;

            for (int i = start; i <= end; i++)
            {
                var trackHash = fullSetlist[i].TrackUniqueHash;
                // In a production environment, we'd pre-load this or use a cache
                var entry = _dbContext.LibraryEntries.AsNoTracking().FirstOrDefault(e => e.UniqueHash == trackHash);
                if (entry != null && entry.Energy.HasValue)
                {
                    double e = entry.Energy.Value;
                    if (e > maxEnergy) maxEnergy = e;
                    if (e < minEnergy) minEnergy = e;
                    foundData = true;
                }
            }

            if (!foundData) return false;

            double gradient = (maxEnergy - minEnergy) / (end - start);
            return gradient < ENERGY_PLATEAU_THRESHOLD;
        }

        /// <summary>
        /// ALGORITHM 2: Harmonic Compatibility
        /// Uses Camelot wheel relationships via HarmonicMatchService
        /// Returns severity score (0-100)
        /// </summary>
        private int AnalyzeHarmonicCompatibility(LibraryEntryEntity trackA, LibraryEntryEntity trackB)
        {
            if (trackA?.MusicalKey == null || trackB?.MusicalKey == null)
                return 30; // Unknown keys = moderate risk

            var keyA = trackA.MusicalKey;
            var keyB = trackB.MusicalKey;

            // Perfect match
            if (keyA == keyB)
                return 0;

            int dist = ComputeKeyDistance(keyA, keyB);

            // Pillar A: Define Key Clash (threshold jump > 2 on Camelot wheel)
            if (dist > 2)
            {
                return 85; // Key Clash
            }

            // Within relative or adjacent
            if (dist <= 1)
                return 15;

            // Within relative minor/major
            if (HarmonicallyRelated(keyA, keyB))
                return 25;

            return 50;
        }

        /// <summary>
        /// ALGORITHM 3: Vocal Compatibility
        /// Checks for vocal clashes and unsafe overlaps
        /// Returns severity score (0-100)
        /// </summary>
        private int AnalyzeVocalCompatibility(LibraryEntryEntity trackA, LibraryEntryEntity trackB)
        {
            // Vocal → Vocal overlap = high risk
            if (trackA.VocalType == VocalType.FullLyrics &&
                trackB.VocalType == VocalType.FullLyrics)
            {
                return 75; // Requires careful mixing or quick cut
            }

            // Instrumental → Instrumental = safe
            if (trackA.VocalType == VocalType.Instrumental &&
                trackB.VocalType == VocalType.Instrumental)
            {
                return 0;
            }

            // Vocal → Instrumental = excellent
            if (trackA.VocalType != VocalType.Instrumental &&
                trackB.VocalType == VocalType.Instrumental)
            {
                return 5; // Vocals clear smoothly
            }

            // Instrumental → Vocal = acceptable
            return 20;
        }

        /// <summary>
        /// ALGORITHM 4: Tempo Compatibility
        /// ±6% beatmatching tolerance check
        /// Returns severity score (0-100)
        /// </summary>
        private int AnalyzeTempoCompatibility(LibraryEntryEntity trackA, LibraryEntryEntity trackB)
        {
            if (trackA?.Bpm == null || trackB?.Bpm == null)
                return 25;

            double ratio = trackB.Bpm.Value / trackA.Bpm.Value;
            double percentDiff = Math.Abs(ratio - 1.0) * 100;

            if (percentDiff < 2) return 0;      // Perfect
            if (percentDiff < 6) return 10;     // Standard beatmatch range
            if (percentDiff < 12) return 35;    // Requires tempo sync
            return 65;                           // Major speed adjustment needed
        }

        /// <summary>
        /// Determines primary failure type based on dimension scores
        /// </summary>
        private (TransitionFailureType, string) IdentifyPrimaryFailure(
            int energyScore,
            int harmonicScore,
            int vocalScore,
            int tempoScore)
        {
            if (vocalScore >= 70)
                return (TransitionFailureType.VocalConflict, "Vocal Clash");
            if (harmonicScore >= 80)
                return (TransitionFailureType.HarmonicClash, "Harmonic Clash");
            if (tempoScore >= 60)
                return (TransitionFailureType.TempoJump, "Tempo Jump");
            if (energyScore >= 60)
                return (TransitionFailureType.EnergyPlateau, "Energy Plateau");

            return (TransitionFailureType.DeadEnd, "Dead-End");
        }

        /// <summary>
        /// ALGORITHM 5: Rescue Track Suggestion (Weighted Scoring Approach B)
        /// Finds optimal bridges for a failing transition
        /// Scores on: EnergyFit (30%), TempoFit (30%), HarmonicFit (40%)
        /// </summary>
        private async Task<List<RescueSuggestion>> FindRescueTracksAsync(
            LibraryEntryEntity trackA,
            LibraryEntryEntity trackB,
            List<LibraryEntryEntity> libraryTracks,
            TransitionFailureType failureType)
        {
            var rescues = new List<RescueSuggestion>();

            // Calculate ideal midpoint
            double idealEnergy = ((trackA.Energy ?? 0) + (trackB.Energy ?? 0)) / 2.0;
            double idealBpm = ((trackA.Bpm ?? 120) + (trackB.Bpm ?? 120)) / 2.0;

            // Score all library tracks
            var candidates = libraryTracks
                .Where(t => t.UniqueHash != trackA.UniqueHash &&
                           t.UniqueHash != trackB.UniqueHash)
                .Select(t => new
                {
                    Track = t,
                    EnergyScore = 1.0 - Math.Abs((t.Energy ?? 0) - idealEnergy),
                    TempoScore = 1.0 - (Math.Abs((t.Bpm ?? 120) - idealBpm) / idealBpm),
                    HarmonicScore = ComputeHarmonicBridgeScore(trackA, t, trackB),
                    ProximityBonus = (t.VocalType == trackA.VocalType ||
                                     t.VocalType == trackB.VocalType) ? 0.05 : 0.0
                })
                .Select(x => new
                {
                    x.Track,
                    CompositeScore = (x.EnergyScore * 0.30) +
                                    (x.TempoScore * 0.30) +
                                    (x.HarmonicScore * 0.40) +
                                    x.ProximityBonus
                })
                .OrderByDescending(x => x.CompositeScore)
                .Take(3)
                .ToList();

            // Build rescue suggestions
            foreach (var candidate in candidates)
            {
                var score = (int)(candidate.CompositeScore * 100);
                var rescue = new RescueSuggestion
                {
                    TargetTrack = candidate.Track,
                    BridgeQualityScore = score,
                    WhyItFitsFull = BuildRescueReasoning(trackA, candidate.Track, trackB, failureType),
                    WhyItFitsShort = BuildRescueShort(failureType, score),
                    ProblemsAddressed = failureType.ToString(),
                    OptimalCutSeconds = (candidate.Track.DurationSeconds ?? 180) * 0.75
                };
                rescues.Add(rescue);
            }

            return rescues;
        }

        /// <summary>
        /// Computes harmonic bridge score: how well does Track C connect A→B harmonically?
        /// </summary>
        private double ComputeHarmonicBridgeScore(
            LibraryEntryEntity trackA,
            LibraryEntryEntity trackC,
            LibraryEntryEntity trackB)
        {
            // Ideal: A → C → B forms a harmonic progression
            int distanceAtoC = ComputeKeyDistance(trackA.MusicalKey ?? string.Empty, trackC.MusicalKey ?? string.Empty);
            int distanceCtoB = ComputeKeyDistance(trackC.MusicalKey ?? string.Empty, trackB.MusicalKey ?? string.Empty);

            // Prefer balanced progressions
            int totalDistance = distanceAtoC + distanceCtoB;
            if (totalDistance == 0) return 1.0;        // Perfect match
            if (totalDistance <= 2) return 0.95;       // Excellent bridge
            if (totalDistance <= 4) return 0.80;       // Good bridge
            if (totalDistance <= 6) return 0.60;       // Acceptable
            return 0.30;                               // Awkward but possible
        }

        /// <summary>
        /// Calculates distance between two Camelot keys
        /// </summary>
        private int ComputeKeyDistance(string keyA, string keyB)
        {
            if (string.IsNullOrEmpty(keyA) || string.IsNullOrEmpty(keyB))
                return 12; // Unknown = maximum distance

            int numA = ExtractCamelotNumber(keyA);
            int numB = ExtractCamelotNumber(keyB);

            int distance = Math.Abs(numA - numB);
            return Math.Min(distance, 12 - distance); // Circular distance
        }

        /// <summary>
        /// Extracts numeric component from Camelot notation (e.g., "8A" → 8)
        /// </summary>
        private int ExtractCamelotNumber(string camelotKey)
        {
            if (string.IsNullOrEmpty(camelotKey))
                return 0;

            var numStr = new string(camelotKey.Where(char.IsDigit).ToArray());
            return int.TryParse(numStr, out int num) ? num : 0;
        }

        /// <summary>
        /// Checks if keys are harmonically related (same wheel rotation, different major/minor)
        /// </summary>
        private bool HarmonicallyRelated(string keyA, string keyB)
        {
            if (string.IsNullOrEmpty(keyA) || string.IsNullOrEmpty(keyB))
                return false;

            int numA = ExtractCamelotNumber(keyA);
            int numB = ExtractCamelotNumber(keyB);

            return numA == numB; // Same wheel number = relative major/minor
        }

        /// <summary>
        /// Generates detailed rescue reasoning string
        /// </summary>
        private string BuildRescueReasoning(
            LibraryEntryEntity trackA,
            LibraryEntryEntity trackC,
            LibraryEntryEntity trackB,
            TransitionFailureType failureType)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"✓ Rescue: {trackC.Artist} - {trackC.Title}");
            sb.AppendLine();

            // Energy narrative
            sb.AppendLine($"  Energy Bridge: {trackA.Energy:F2} → {trackC.Energy:F2} → {trackB.Energy:F2}");
            sb.AppendLine($"    Lifts from {(trackA.Energy < trackB.Energy ? "low" : "high")} to {(trackC.Energy > trackA.Energy ? "rising" : "stable")}");
            sb.AppendLine();

            // Harmonic narrative
            int dist = ComputeKeyDistance(trackA.MusicalKey ?? string.Empty, trackC.MusicalKey ?? string.Empty);
            sb.AppendLine($"  Harmonic Path: {trackA.MusicalKey} → {trackC.MusicalKey} → {trackB.MusicalKey}");
            sb.AppendLine($"    {(dist <= 1 ? "Compatible" : "Safe transition")} key cluster");
            sb.AppendLine();

            // Problem-specific
            sb.AppendLine($"  Solves: {failureType}");

            return sb.ToString();
        }

        /// <summary>
        /// Single-line rescue summary
        /// </summary>
        private string BuildRescueShort(TransitionFailureType failureType, int score)
        {
            return failureType switch
            {
                TransitionFailureType.EnergyPlateau => $"Energy bridge ({score}%)",
                TransitionFailureType.VocalConflict => $"Vocal safe zone ({score}%)",
                TransitionFailureType.HarmonicClash => $"Harmonic bridge ({score}%)",
                TransitionFailureType.TempoJump => $"Tempo compromise ({score}%)",
                _ => $"Bridges gap ({score}%)"
            };
        }

        /// <summary>
        /// Generates mentor-style forensic reasoning for a single transition
        /// </summary>
        private string GenerateTransitionReasoning(
            LibraryEntryEntity trackA,
            LibraryEntryEntity trackB,
            int energyScore,
            int harmonicScore,
            int vocalScore,
            int tempoScore)
        {
            var builder = new MentorReasoningBuilder();

            builder.AddSection("Transition Analysis");
            builder.AddBullet($"{trackA.Artist} → {trackB.Artist}");
            builder.AddDetail($"Energy: {trackA.Energy:F2} → {trackB.Energy:F2}");
            builder.AddDetail($"Key: {trackA.MusicalKey} → {trackB.MusicalKey}");

            if (vocalScore > 50)
            {
                builder.AddWarning("Vocal overlap risk detected");
            }

            if (energyScore > 50)
            {
                builder.AddWarning("Energy plateau or jump");
            }

            if (harmonicScore > 70)
            {
                builder.AddWarning("Harmonic incompatibility");
            }

            builder.AddSection("Recommendation");
            if (energyScore + harmonicScore + vocalScore > 100)
            {
                builder.AddBullet("Consider rescue track or rearrangement");
            }
            else
            {
                builder.AddSuccess("Transition is acceptable with care");
            }

            return builder.ToString();
        }

        /// <summary>
        /// Generates overall setlist narrative
        /// </summary>
        private string GenerateSetlistNarrative(
            StressDiagnosticReport report,
            List<SetTrackEntity> tracks,
            Dictionary<string, LibraryEntryEntity> trackMap)
        {
            var builder = new MentorReasoningBuilder();

            builder.AddSection("Setlist Overview");
            builder.AddBullet($"Total Transitions: {report.StressPoints.Count}");
            builder.AddBullet($"Critical Issues: {report.CriticalCount}");
            builder.AddBullet($"Warnings: {report.WarningCount}");
            builder.AddBullet($"Healthy Flows: {report.HealthyCount}");

            builder.AddSection("Energy Arc");
            if (report.StressPoints.Any(s => s.PrimaryFailure == TransitionFailureType.EnergyPlateau))
            {
                builder.AddWarning("Energy plateau detected in middle section");
                builder.AddDetail("Consider adding a build-up or energy spike");
            }
            else
            {
                builder.AddSuccess("Energy progression is dynamic");
            }

            builder.AddSection("Key Journey");
            builder.AddBullet("Overall harmonic coherence: " +
                (report.StressPoints.Average(s => s.SeverityScore) < 40 ? "Excellent" : "Requires attention"));

            builder.AddVerdict(
                report.OverallHealthScore >= 80
                    ? "Setlist is ready for performance! Flows naturally."
                    : report.OverallHealthScore >= 50
                    ? "Setlist is usable with careful mixing at weak points."
                    : "Major rework recommended before performance.");

            return builder.ToString();
        }

        /// <summary>
        /// Generates quick summary for UI display
        /// </summary>
        private string GenerateQuickSummary(StressDiagnosticReport report)
        {
            if (report.CriticalCount == 0 && report.WarningCount == 0)
            {
                return $"✓ Perfect flow! All {report.HealthyCount} transitions are smooth.";
            }

            var parts = new List<string>();
            if (report.CriticalCount > 0)
                parts.Add($"⚠ {report.CriticalCount} critical issue{(report.CriticalCount > 1 ? "s" : "")}");
            if (report.WarningCount > 0)
                parts.Add($"⚠ {report.WarningCount} warning{(report.WarningCount > 1 ? "s" : "")}");

            return string.Join(" + ", parts) + " — Click red segments to see rescue suggestions.";
        }

        /// <summary>
        /// Phase 6: Applies a rescue track to the setlist at optimal position.
        /// Determines whether to:
        /// 1. REPLACE: Swap problematic track with rescue track
        /// 2. INSERT: Place rescue track as bridge between problematic transition
        ///
        /// Then recalculates severity scores for affected transitions.
        /// </summary>
        public async Task<ApplyRescueResult> ApplyRescueTrackAsync(
            SetListEntity setlist,
            TransitionStressPoint stressPoint,
            RescueSuggestion rescueSuggestion)
        {
            if (setlist == null || stressPoint == null || rescueSuggestion == null)
            {
                return new ApplyRescueResult
                {
                    Success = false,
                    Message = "Invalid parameters for rescue application.",
                    UpdatedSetlist = setlist
                };
            }

            try
            {
                // Get the actual tracks from the setlist
                var setTracks = setlist.Tracks?.ToList() ?? new List<SetTrackEntity>();
                if (setTracks.Count == 0)
                {
                    return new ApplyRescueResult
                    {
                        Success = false,
                        Message = "Setlist is empty.",
                        UpdatedSetlist = setlist
                    };
                }

                var fromTrack = setTracks.ElementAtOrDefault(stressPoint.FromTrackIndex);
                var toTrack = setTracks.ElementAtOrDefault(stressPoint.ToTrackIndex);

                if (fromTrack == null || toTrack == null)
                {
                    return new ApplyRescueResult
                    {
                        Success = false,
                        Message = "Invalid transition indices.",
                        UpdatedSetlist = setlist
                    };
                }

                var rescueTarget = rescueSuggestion.TargetTrack as LibraryEntryEntity;
                var fromEntry = await _dbContext.LibraryEntries
                    .AsNoTracking()
                    .FirstOrDefaultAsync(e => e.UniqueHash == fromTrack.TrackUniqueHash);
                var toEntry = await _dbContext.LibraryEntries
                    .AsNoTracking()
                    .FirstOrDefaultAsync(e => e.UniqueHash == toTrack.TrackUniqueHash);

                if (rescueTarget == null || fromEntry == null || toEntry == null)
                {
                    return new ApplyRescueResult
                    {
                        Success = false,
                        Message = "Unable to resolve tracks for rescue application.",
                        UpdatedSetlist = setlist
                    };
                }

                // Decision: Replace vs Insert
                // INSERT if: rescue track bridges the gap much better than replacement
                // REPLACE if: rescue track is better standalone on one side
                var qualityGainFromReplace =
                    Math.Max(
                        CalculateTransitionQuality(fromEntry, rescueTarget),
                        CalculateTransitionQuality(rescueTarget, toEntry)
                    );

                var qualityGainFromBridge =
                    CalculateTransitionQuality(fromEntry, rescueTarget) +
                    CalculateTransitionQuality(rescueTarget, toEntry);

                bool shouldInsertBridge = qualityGainFromBridge > (qualityGainFromReplace * 1.3);

                if (shouldInsertBridge)
                {
                    // INSERT at optimal position (after fromTrack)
                    int insertPosition = stressPoint.ToTrackIndex;
                    var newSetTrack = new SetTrackEntity
                    {
                        SetListId = setlist.Id,
                        LibraryId = rescueTarget.Id,
                        Library = rescueTarget,
                        TrackUniqueHash = rescueTarget.UniqueHash,
                        Position = insertPosition,
                        IsRescueTrack = true, // Mark as applied rescue
                        RescueReason = $"Bridge: {stressPoint.PrimaryProblem}"
                    };

                    // Adjust positions of tracks after insertion
                    foreach (var track in setTracks.Where(t => t.Position >= insertPosition))
                    {
                        track.Position++;
                    }

                    setTracks.Insert(insertPosition, newSetTrack);
                    setlist.Tracks = setTracks;

                    return new ApplyRescueResult
                    {
                        Success = true,
                        Message = $"✓ Rescue track '{rescueTarget.Title}' inserted as bridge.",
                        UpdatedSetlist = setlist,
                        Action = "INSERT",
                        AffectedTransitions = 2 // Bridge affects both new transitions
                    };
                }
                else
                {
                    // REPLACE the problematic track
                    // Prefer replacing fromTrack if it's lower quality
                    int replaceIndex = stressPoint.FromTrackIndex;
                    if (CalculateTransitionQuality(rescueTarget, toEntry) >
                        CalculateTransitionQuality(fromEntry, rescueTarget))
                    {
                        replaceIndex = stressPoint.ToTrackIndex;
                    }

                    var oldTrack = setTracks[replaceIndex];
                    var newSetTrack = new SetTrackEntity
                    {
                        SetListId = setlist.Id,
                        LibraryId = rescueTarget.Id,
                        Library = rescueTarget,
                        TrackUniqueHash = rescueTarget.UniqueHash,
                        Position = replaceIndex,
                        IsRescueTrack = true,
                        RescueReason = $"Replaced: {oldTrack.Library?.Title ?? "Unknown"} due to {stressPoint.PrimaryProblem}"
                    };

                    setTracks[replaceIndex] = newSetTrack;
                    setlist.Tracks = setTracks;

                    return new ApplyRescueResult
                    {
                        Success = true,
                        Message = $"✓ Track replaced with '{rescueTarget.Title}'.",
                        UpdatedSetlist = setlist,
                        Action = "REPLACE",
                        AffectedTransitions = 2 // Affects transitions before and after
                    };
                }
            }
            catch (Exception ex)
            {
                return new ApplyRescueResult
                {
                    Success = false,
                    Message = $"Error applying rescue track: {ex.Message}",
                    UpdatedSetlist = setlist
                };
            }
        }

        /// <summary>
        /// Calculates transition quality between two tracks (0-100, higher is better).
        /// Uses existing analysis methods.
        /// </summary>
        private int CalculateTransitionQuality(LibraryEntryEntity trackA, LibraryEntryEntity trackB)
        {
            if (trackA == null || trackB == null) return 30;

            var energyScore = 100 - AnalyzeEnergyFlow(trackA, trackB);
            var harmonicScore = 100 - AnalyzeHarmonicCompatibility(trackA, trackB);
            var vocalScore = 100 - AnalyzeVocalCompatibility(trackA, trackB);
            var tempoScore = 100 - AnalyzeTempoCompatibility(trackA, trackB);

            return (int)((energyScore * 0.25) + (harmonicScore * 0.35) +
                         (vocalScore * 0.30) + (tempoScore * 0.10));
        }

        private int AnalyzeEnergyFlow(LibraryEntryEntity trackA, LibraryEntryEntity trackB)
        {
            // Reuse compatibility analysis without full setlist context
            return AnalyzeEnergyCompatibility(trackA, trackB, new List<SetTrackEntity>(), 0);
        }
    }
}

