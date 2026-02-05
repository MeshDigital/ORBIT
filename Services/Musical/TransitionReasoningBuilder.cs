using System;
using System.Collections.Generic;
using System.Text;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models.Musical;

namespace SLSKDONET.Services.Musical
{
    /// <summary>
    /// Generates DJ-mentor-style forensic reasoning for transition advice.
    /// Uses the fluent MentorReasoningBuilder to create structured output.
    /// </summary>
    public static class TransitionReasoningBuilder
    {
        public static string BuildReasoning(
            SLSKDONET.Data.LibraryEntryEntity trackA, 
            SLSKDONET.Data.LibraryEntryEntity trackB, 
            TransitionSuggestion suggestion,
            VocalOverlapReport vocalReport)
        {
            var builder = new MentorReasoningBuilder();
            var verdictParts = new List<string>();

            // ═══════════════════════════════════════════
            // SECTION 1: BREAKDOWN ANALYSIS
            // ═══════════════════════════════════════════
            builder.AddSection("Breakdown Analysis");
            AddArchetypeAnalysis(builder, suggestion.Archetype, trackA, verdictParts);

            // ═══════════════════════════════════════════
            // SECTION 2: HARMONIC VERDICT
            // ═══════════════════════════════════════════
            builder.AddSection("Harmonic Verdict");
            AddHarmonicAnalysis(builder, trackA, trackB, suggestion, verdictParts);

            // ═══════════════════════════════════════════
            // SECTION 3: VOCAL SAFETY
            // ═══════════════════════════════════════════
            builder.AddSection("Vocal Safety");
            AddVocalAnalysis(builder, trackA, trackB, vocalReport, verdictParts);

            // ═══════════════════════════════════════════
            // SECTION 4: ENERGY & TIMING
            // ═══════════════════════════════════════════
            builder.AddSection("Energy & Timing");
            AddEnergyAnalysis(builder, trackA, trackB, suggestion, verdictParts);

            // Add optimal moment if available
            if (suggestion.OptimalTransitionTime.HasValue)
            {
                builder.AddOptimalMoment(
                    suggestion.OptimalTransitionTime.Value,
                    suggestion.OptimalTransitionReason ?? "structural alignment");
            }

            // ═══════════════════════════════════════════
            // FINAL VERDICT
            // ═══════════════════════════════════════════
            var verdictText = BuildFinalVerdict(suggestion, vocalReport, verdictParts);
            builder.AddVerdict(verdictText);

            return builder.ToString();
        }

        private static void AddArchetypeAnalysis(MentorReasoningBuilder builder, TransitionArchetype archetype, SLSKDONET.Data.LibraryEntryEntity trackA, List<string> verdictParts)
        {
            var (description, advice, verdict) = archetype switch
            {
                TransitionArchetype.BuildToDrop => (
                    "Build-to-Drop detected",
                    "Track A builds tension → Track B delivers the drop",
                    "build-to-drop slam"),
                TransitionArchetype.DropSwap => (
                    "Drop-Swap detected",
                    "High-energy swap: cut A at phrase end, slam B's drop",
                    "drop-swap power move"),
                TransitionArchetype.VocalToInstrumental => (
                    "Vocal → Instrumental transition",
                    $"{trackA.VocalType} ending safely into instrumental",
                    "vocal-to-inst safety"),
                TransitionArchetype.LongBlend => (
                    "Long Blend recommended",
                    "Locked keys + tight BPM = smooth 32-bar mix",
                    "long blend opportunity"),
                TransitionArchetype.EnergyReset => (
                    "Energy Reset detected",
                    "Bringing the floor down for a break or atmosphere change",
                    "energy reset"),
                TransitionArchetype.QuickCut => (
                    "Quick Cut recommended",
                    "Standard transition—focus on phrasing, 4–8 bar max",
                    "quick cut"),
                _ => ("Standard transition", "Use your ears.", "standard mix")
            };

            builder.AddBullet(description);
            builder.AddDetail(advice);
            verdictParts.Add(verdict);
        }

        private static void AddHarmonicAnalysis(MentorReasoningBuilder builder, SLSKDONET.Data.LibraryEntryEntity trackA, SLSKDONET.Data.LibraryEntryEntity trackB, TransitionSuggestion suggestion, List<string> verdictParts)
        {
            var keyA = trackA.MusicalKey ?? "?";
            var keyB = trackB.MusicalKey ?? "?";

            if (suggestion.HarmonicCompatibility >= 1.0)
            {
                builder.AddSuccess($"Perfect match: {keyA} → {keyB}");
                builder.AddDetail("Supports long, musical blends (32+ bars)");
                verdictParts.Add("harmonic perfection");
            }
            else if (suggestion.HarmonicCompatibility >= 0.7)
            {
                builder.AddBullet($"Compatible: {keyA} → {keyB}");
                builder.AddDetail("Best for quick cuts or rhythmic mixing");
                verdictParts.Add("harmonic compatibility");
            }
            else
            {
                builder.AddWarning($"Key Clash: {keyA} → {keyB}");
                builder.AddDetail("Avoid long melodic overlaps—use phrase boundaries");
                verdictParts.Add("⚠ key clash risk");
            }
        }

        private static void AddVocalAnalysis(MentorReasoningBuilder builder, SLSKDONET.Data.LibraryEntryEntity trackA, SLSKDONET.Data.LibraryEntryEntity trackB, VocalOverlapReport vocalReport, List<string> verdictParts)
        {
            if (vocalReport.HasConflict)
            {
                builder.AddWarning(vocalReport.WarningMessage ?? "Vocal clash detected");
                builder.AddDetail($"Conflict Intensity: {vocalReport.ConflictIntensity:P0} — Safety Score: {vocalReport.VocalSafetyScore:P0}");
                verdictParts.Add("⚠ vocal clash");
            }
            else if (trackA.VocalType != SLSKDONET.Models.VocalType.Instrumental && trackB.VocalType == SLSKDONET.Models.VocalType.Instrumental)
            {
                builder.AddSuccess("Vocals clearing into instrumental");
                builder.AddDetail("Excellent spot to let the beat ride");
                verdictParts.Add("vocals clear");
            }
            else
            {
                builder.AddSuccess("No vocal conflict detected");
                verdictParts.Add("vocals safe");
            }
        }

        private static void AddEnergyAnalysis(MentorReasoningBuilder builder, SLSKDONET.Data.LibraryEntryEntity trackA, SLSKDONET.Data.LibraryEntryEntity trackB, TransitionSuggestion suggestion, List<string> verdictParts)
        {
            double energyA = trackA.Energy ?? trackA.AudioFeatures?.Energy ?? 0;
            double energyB = trackB.Energy ?? trackB.AudioFeatures?.Energy ?? 0;

            if (Math.Abs(energyA - energyB) > 0.3)
            {
                string direction = energyB > energyA ? "LIFT" : "RESET";
                builder.AddBullet($"Energy {direction}: {energyA:P0} → {energyB:P0}");
                verdictParts.Add($"energy {direction.ToLower()}");
            }
            else
            {
                builder.AddSuccess($"Energy stable: {energyA:P0} → {energyB:P0}");
            }

            if (suggestion.BpmDrift > (trackA.Bpm * 0.03))
            {
                builder.AddWarning($"Wide BPM gap: {suggestion.BpmDrift:F1} BPM delta");
                builder.AddDetail("Use Sync or a hard cut to avoid drift");
            }
            else
            {
                builder.AddSuccess($"BPM compatible: {trackA.Bpm:F0} → {trackB.Bpm:F0}");
            }
        }

        private static string BuildFinalVerdict(TransitionSuggestion suggestion, VocalOverlapReport vocalReport, List<string> parts)
        {
            var hasWarning = vocalReport.HasConflict || suggestion.HarmonicCompatibility < 0.7;
            var summary = string.Join(" + ", parts);

            // Build actionable mentor advice
            if (vocalReport.HasConflict)
            {
                return $"⚠ PROCEED WITH CAUTION — {summary}\n  Use a short power mix and cut the outgoing track before the next vocal phrase.";
            }

            if (suggestion.HarmonicCompatibility < 0.7)
            {
                return $"⚠ PROCEED WITH CAUTION — {summary}\n  Keep the blend short or use a rhythmic/percussive transition.";
            }

            if (suggestion.HarmonicCompatibility >= 1.0 && !vocalReport.HasConflict)
            {
                return $"✓ CLEAR TO MIX — {summary}\n  Let this transition breathe over a full phrase—it will feel intentional and powerful.";
            }

            return $"✓ CLEAR TO MIX — {summary}\n  Safe, musical transition—you can ride this one without stress.";
        }
    }
}
