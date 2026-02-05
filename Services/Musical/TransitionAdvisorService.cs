using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Models.Musical;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.Musical
{
    public interface ITransitionAdvisorService
    {
        double CalculateFlowContinuity(SetListEntity setList, FlowWeightSettings? settings = null);
        double CalculateFlowContinuity(IEnumerable<(LibraryEntryEntity Feature, SetTrackEntity Config)> trackSequence, FlowWeightSettings? settings = null);
        TransitionSuggestion AdviseTransition(LibraryEntryEntity trackA, LibraryEntryEntity trackB, FlowWeightSettings? settings = null);
        VocalOverlapReport CheckVocalConflict(LibraryEntryEntity trackA, LibraryEntryEntity trackB, double transitionOffset);
    }

    public class TransitionAdvisorService : ITransitionAdvisorService
    {
        private readonly ILogger<TransitionAdvisorService> _logger;
        private readonly HarmonicMatchService _harmonicService;
        private readonly VocalIntelligenceService _vocalService;
        private readonly IPhraseAlignmentService _phraseAlignmentService;

        public TransitionAdvisorService(
            ILogger<TransitionAdvisorService> logger, 
            HarmonicMatchService harmonicService,
            VocalIntelligenceService vocalService,
            IPhraseAlignmentService phraseAlignmentService)
        {
            _logger = logger;
            _harmonicService = harmonicService;
            _vocalService = vocalService;
            _phraseAlignmentService = phraseAlignmentService;
        }

        public double CalculateFlowContinuity(SetListEntity setList, FlowWeightSettings? settings = null)
        {
            // Fallback for when we don't have enriched data
            // In a real scenario, this should likely throw or fetch data, but for now we return a neutral score
            // to avoid breaking existing callers that haven't been updated.
            return 0.85;
        }

        public double CalculateFlowContinuity(IEnumerable<(LibraryEntryEntity Feature, SetTrackEntity Config)> trackSequence, FlowWeightSettings? settings = null)
        {
            settings ??= new FlowWeightSettings();
            var tracks = trackSequence.ToList();
            if (tracks.Count < 2) return 1.0;

            double totalScore = 0;
            int pairsCalculated = 0;

            for (int i = 0; i < tracks.Count - 1; i++)
            {
                var current = tracks[i];
                var next = tracks[i + 1];

                totalScore += CalculateTransitionScore(current, next, settings);
                pairsCalculated++;
            }

            return pairsCalculated > 0 ? totalScore / pairsCalculated : 1.0;
        }

        private double CalculateTransitionScore((LibraryEntryEntity Feature, SetTrackEntity Config) current, (LibraryEntryEntity Feature, SetTrackEntity Config) next, FlowWeightSettings settings)
        {
            double score = 1.0;

            // 1. Harmonic Compatibility
            double keyScore = CalculateKeyScore(current.Feature, next.Feature);
            score -= (1.0 - keyScore) * settings.HarmonicWeight;

            // 2. BPM Compatibility
            if (current.Feature.BPM.HasValue && next.Feature.BPM.HasValue && current.Feature.BPM > 0)
            {
                double drift = Math.Abs(current.Feature.BPM.Value - next.Feature.BPM.Value);
                double driftPercent = drift / current.Feature.BPM.Value;
                
                // Penalty kicks in significantly after 3% drift
                if (driftPercent > 0.03)
                {
                    double penalty = Math.Min(1.0, (driftPercent - 0.03) * 5); // Rapid falloff
                    score -= penalty * settings.BpmWeight;
                }
            }

            // 3. Vocal Intelligence
            double vocalPenalty = 0.0;
            
            // A. Type-Based Rules (Heuristic)
            if (current.Feature.VocalType == VocalType.FullLyrics && next.Feature.VocalType == VocalType.FullLyrics)
            {
                vocalPenalty += 0.3; // High risk of lyrical clash
            }
            else if (current.Feature.VocalType >= VocalType.HookOnly && next.Feature.VocalType >= VocalType.HookOnly)
            {
                vocalPenalty += 0.15; // Moderate risk
            }

            // B. Overlap-Based Rules (Data Driven)
            // Use the transition offset from the 'next' track configuration
            var vocalReport = CheckVocalConflict(current.Feature, next.Feature, next.Config.ManualOffset);
            if (vocalReport.HasConflict)
            {
                vocalPenalty += (vocalReport.ConflictIntensity * 0.5); // Add intensity-based penalty
            }

            score -= Math.Min(1.0, vocalPenalty * settings.VocalOverlapPenalty);

            return Math.Clamp(score, 0.0, 1.0);
        }

        public TransitionSuggestion AdviseTransition(LibraryEntryEntity trackA, LibraryEntryEntity trackB, FlowWeightSettings? settings = null)
        {
            settings ??= new FlowWeightSettings();
            var suggestion = new TransitionSuggestion
            {
                Archetype = TransitionArchetype.QuickCut, // Default to avoid false positive on DropSwap (0)
                BpmDrift = Math.Abs((trackA.Bpm ?? 0) - (trackB.Bpm ?? 0)),
                HarmonicCompatibility = CalculateKeyScore(trackA, trackB)
            };

            // Heuristics for Archetype & Reasoning
            
            // 1. Drop-Swap Detection (High Energy Transfer)
            // Logic: Track A is high energy, Track B has a defined Drop, and we are transitioning near a phrase boundary.
            bool trackAHighEnergy = (trackA.Energy ?? trackA.AudioFeatures?.Energy ?? 0) > 0.7;
            bool trackBHasDrop = (trackB.AudioFeatures?.DropTimeSeconds > 0) || ((trackB.Energy ?? trackB.AudioFeatures?.Energy ?? 0) > 0.7);
            
            // Check if user has aligned significantly (transition offset is handled in UI/SetTrack, 
            // but here we advise based on potential).
            // Simplification: If both are high energy and vocally active, suggest Drop-Swap to minimize clash.
            
            if (trackAHighEnergy && trackBHasDrop)
            {
                 // Vocal clash check for Drop-Swap necessity
                 if (trackA.VocalType >= VocalType.HookOnly && trackB.VocalType >= VocalType.HookOnly)
                 {
                     suggestion.Archetype = TransitionArchetype.DropSwap;
                 }
                 // If not clashing but high energy, still a good candidate
                 else if (trackA.VocalType == VocalType.Instrumental && trackB.VocalType >= VocalType.HookOnly) 
                 {
                     suggestion.Archetype = TransitionArchetype.DropSwap;
                 }
                 else if (suggestion.Archetype == TransitionArchetype.QuickCut && suggestion.HarmonicCompatibility < 0.7)
                 {
                      suggestion.Archetype = TransitionArchetype.DropSwap; // Upgrade QuickCut to DropSwap for high energy
                 }
            }

            // 2. Build-to-Drop Detection
            // Logic: Track A has a 'Build' or 'Rise' segment, Track B has a Drop.
            if (suggestion.Archetype != TransitionArchetype.DropSwap)
            {
                bool trackAHasBuild = false;
                if (!string.IsNullOrEmpty(trackA.AudioFeatures?.PhraseSegmentsJson) && trackA.AudioFeatures.PhraseSegmentsJson != "[]")
                {
                    try
                    {
                        var segments = JsonSerializer.Deserialize<List<PhraseSegment>>(trackA.AudioFeatures.PhraseSegmentsJson);
                        // Check if we have a Build segment
                        trackAHasBuild = segments?.Any(s => s.Label.Contains("Build", StringComparison.OrdinalIgnoreCase) 
                                                         || s.Label.Contains("Rise", StringComparison.OrdinalIgnoreCase)
                                                         || s.Label.Contains("Uplift", StringComparison.OrdinalIgnoreCase)) ?? false;
                    }
                    catch { /* Ignore parsing errors */ }
                }

                if (trackAHasBuild && trackBHasDrop)
                {
                    suggestion.Archetype = TransitionArchetype.BuildToDrop;
                }
            }

            if (suggestion.Archetype != TransitionArchetype.DropSwap && suggestion.Archetype != TransitionArchetype.BuildToDrop)
            {
                if (trackB.VocalType == VocalType.Instrumental && trackA.VocalType != VocalType.Instrumental)
                {
                    suggestion.Archetype = TransitionArchetype.VocalToInstrumental;
                }
                else if (suggestion.HarmonicCompatibility >= 0.9 && suggestion.BpmDrift < (trackA.Bpm * 0.03))
                {
                    suggestion.Archetype = TransitionArchetype.LongBlend;
                }
                else if (trackA.Energy > 0.8 && trackB.Energy < 0.4)
                {
                    suggestion.Archetype = TransitionArchetype.EnergyReset;
                }
                else if (trackA.VocalType >= VocalType.HookOnly && trackB.VocalType >= VocalType.HookOnly)
                {
                    suggestion.Archetype = TransitionArchetype.QuickCut;
                }
                else
                {
                    suggestion.Archetype = TransitionArchetype.QuickCut;
                }
            }

            // Generate full forensic reasoning
            // Calculate vocal conflict once here to pass to builder
            var vocalReport = CheckVocalConflict(trackA, trackB, 0); 
            var simpleVocalCheck = CheckVocalConflict(trackA, trackB, 0); 
            
            // Calculate optimal transition time using Phrase Alignment Service
            var optimization = _phraseAlignmentService.DetermineOptimalTransitionTime(trackA, trackB, suggestion.Archetype);
            suggestion.OptimalTransitionTime = optimization?.Time;
            suggestion.OptimalTransitionReason = optimization?.Reason;

            suggestion.Reasoning = TransitionReasoningBuilder.BuildReasoning(trackA, trackB, suggestion, simpleVocalCheck);

            return suggestion;
        }

        private double CalculateKeyScore(LibraryEntryEntity trackA, LibraryEntryEntity trackB)
        {
            if (string.IsNullOrEmpty(trackA.MusicalKey) || string.IsNullOrEmpty(trackB.MusicalKey)) return 0.5;
            if (trackA.MusicalKey == trackB.MusicalKey) return 1.0;
            // Simplified Camelot proximity check: +/- 1 is OK
            // In full implementation, delegate to HarmonicMatchService.GetKeyRelationship
            // For now, assume simple string matching or close enough
            // TODO: Use HarmonicMatchService logic here
            return 0.7; 
        }

        private string FormatTime(double? seconds)
        {
            if (!seconds.HasValue) return "end";
            var span = TimeSpan.FromSeconds(seconds.Value);
            return $"{(int)span.TotalMinutes}:{span.Seconds:D2}";
        }

        public VocalOverlapReport CheckVocalConflict(LibraryEntryEntity trackA, LibraryEntryEntity trackB, double transitionOffset)
        {
            var report = new VocalOverlapReport();
            
            if (trackA.AudioFeatures == null || trackB.AudioFeatures == null) return report;

            float[] curveA = JsonSerializer.Deserialize<float[]>(trackA.AudioFeatures.VocalDensityCurveJson ?? "[]") ?? Array.Empty<float>();
            float[] curveB = JsonSerializer.Deserialize<float[]>(trackB.AudioFeatures.VocalDensityCurveJson ?? "[]") ?? Array.Empty<float>();

            if (curveA.Length == 0 || curveB.Length == 0) return report;

            double hazard = _vocalService.CalculateOverlapHazard(
                curveA, 
                curveB, 
                transitionOffset, 
                (double)(trackA.CanonicalDuration ?? trackA.DurationSeconds ?? 0),
                (double)(trackB.CanonicalDuration ?? trackB.DurationSeconds ?? 0));

            report.HasConflict = hazard > 0.4;
            report.ConflictIntensity = hazard;
            report.VocalSafetyScore = 1.0 - hazard;

            if (report.HasConflict)
            {
                report.WarningMessage = (trackA.VocalType == VocalType.FullLyrics && trackB.VocalType == VocalType.FullLyrics)
                    ? "CRITICAL: Multiple lyrical sections overlapping. High risk of clashing vocals."
                    : $"CAUTION: Overlapping vocal density ({hazard:P0}). Consider shifting Track B by {(hazard > 0.7 ? "16" : "8")} bars.";
            }

            return report;
        }
    }



}
