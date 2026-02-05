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
        TransitionSuggestion AdviseTransition(LibraryEntryEntity trackA, LibraryEntryEntity trackB, FlowWeightSettings? settings = null);
        VocalOverlapReport CheckVocalConflict(LibraryEntryEntity trackA, LibraryEntryEntity trackB, double transitionOffset);
    }

    public class TransitionAdvisorService : ITransitionAdvisorService
    {
        private readonly ILogger<TransitionAdvisorService> _logger;
        private readonly HarmonicMatchService _harmonicService;
        private readonly VocalIntelligenceService _vocalService;

        public TransitionAdvisorService(
            ILogger<TransitionAdvisorService> logger, 
            HarmonicMatchService harmonicService,
            VocalIntelligenceService vocalService)
        {
            _logger = logger;
            _harmonicService = harmonicService;
            _vocalService = vocalService;
        }

        public double CalculateFlowContinuity(SetListEntity setList, FlowWeightSettings? settings = null)
        {
            settings ??= new FlowWeightSettings();
            if (setList.Tracks == null || setList.Tracks.Count < 2) return 1.0;

            double totalScore = 0;
            int pairsCalculated = 0;
            var tracks = setList.Tracks.OrderBy(t => t.Position).ToList();

            for (int i = 0; i < tracks.Count - 1; i++)
            {
                // In a production app, the entities should be pre-loaded with features.
                // We'll perform the logic on the data we have.
                // Placeholder scoring if features are missing
                totalScore += 0.85; 
                pairsCalculated++;
            }

            return pairsCalculated > 0 ? totalScore / pairsCalculated : 1.0;
        }

        public TransitionSuggestion AdviseTransition(LibraryEntryEntity trackA, LibraryEntryEntity trackB, FlowWeightSettings? settings = null)
        {
            settings ??= new FlowWeightSettings();
            var suggestion = new TransitionSuggestion
            {
                BpmDrift = Math.Abs((trackA.Bpm ?? 0) - (trackB.Bpm ?? 0)),
                HarmonicCompatibility = CalculateKeyScore(trackA, trackB)
            };

            // Heuristics for Archetype & Reasoning
            if (trackB.VocalType == VocalType.Instrumental && trackA.VocalType != VocalType.Instrumental)
            {
                suggestion.Archetype = TransitionArchetype.VocalToInstrumental;
                suggestion.Reasoning = $"Track A is '{trackA.VocalType}' ending at {FormatTime(trackA.VocalEndSeconds)}. Track B is '{trackB.VocalType}' — safe to blend without overlap hazard.";
            }
            else if (suggestion.HarmonicCompatibility >= 0.9 && suggestion.BpmDrift < (trackA.Bpm * 0.03))
            {
                suggestion.Archetype = TransitionArchetype.LongBlend;
                suggestion.Reasoning = $"Strong harmonic match ({trackA.MusicalKey} → {trackB.MusicalKey}) and tight BPM alignment supports a surgical 32-bar blend.";
            }
            else if (trackA.Energy > 0.8 && trackB.Energy < 0.4)
            {
                suggestion.Archetype = TransitionArchetype.EnergyReset;
                suggestion.Reasoning = "High-to-Low energy transition detected. Recommend a structural reset or atmospheric break to settle the floor.";
            }
            else if (trackA.VocalType >= VocalType.HookOnly && trackB.VocalType >= VocalType.HookOnly)
            {
                suggestion.Archetype = TransitionArchetype.QuickCut;
                suggestion.Reasoning = $"CAUTION: Both tracks have significant vocal presence ({trackA.VocalType} / {trackB.VocalType}). Use a hard 'Drop-Swap' at the phrase change.";
            }
            else
            {
                suggestion.Archetype = TransitionArchetype.QuickCut;
                suggestion.Reasoning = "Standard transition. Focus on rhythmic alignment at the percussion-only sections.";
            }

            return suggestion;
        }

        private double CalculateKeyScore(LibraryEntryEntity trackA, LibraryEntryEntity trackB)
        {
            if (string.IsNullOrEmpty(trackA.MusicalKey) || string.IsNullOrEmpty(trackB.MusicalKey)) return 0.5;
            if (trackA.MusicalKey == trackB.MusicalKey) return 1.0;
            // Simplified Camelot proximity check
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

    public class TransitionSuggestion
    {
        public TransitionArchetype Archetype { get; set; }
        public string Reasoning { get; set; } = string.Empty;
        public double BpmDrift { get; set; }
        public double HarmonicCompatibility { get; set; }
    }

    public class VocalOverlapReport
    {
        public bool HasConflict { get; set; }
        public double ConflictIntensity { get; set; }
        public double VocalSafetyScore { get; set; }
        public string? WarningMessage { get; set; }
        public List<double> ConflictPoints { get; set; } = new();
    }
}
