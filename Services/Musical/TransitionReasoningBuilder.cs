using System;
using System.Collections.Generic;
using System.Text;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models.Musical;

namespace SLSKDONET.Services.Musical
{
    /// <summary>
    /// Generates human-readable forensic reasoning for transition advice.
    /// Turns raw metrics (BPM, Key, Energy) into DJ-friendly explanations.
    /// </summary>
    public static class TransitionReasoningBuilder
    {
        public static string BuildReasoning(
            SLSKDONET.Data.LibraryEntryEntity trackA, 
            SLSKDONET.Data.LibraryEntryEntity trackB, 
            TransitionSuggestion suggestion,
            VocalOverlapReport vocalReport)
        {
            var sb = new StringBuilder();

            // 1. Archetype Explanation
            sb.AppendLine(GetArchetypeReasoning(suggestion.Archetype, trackA, trackB));

            // 2. Harmonic Reasoning
            if (suggestion.HarmonicCompatibility >= 1.0)
            {
                sb.AppendLine($"✅ harmonic match ({trackA.MusicalKey ?? "?"} → {trackB.MusicalKey ?? "?"}) supports a long, musical blend.");
            }
            else if (suggestion.HarmonicCompatibility >= 0.7)
            {
                sb.AppendLine($"⚠️ Compatible key ({trackA.MusicalKey ?? "?"} → {trackB.MusicalKey ?? "?"}). Good for quick cuts or rhythmic mixing.");
            }
            else
            {
                sb.AppendLine($"❌ Key Clash ({trackA.MusicalKey ?? "?"} → {trackB.MusicalKey ?? "?"}). Avoid long melodic overlaps.");
            }

            // 3. Vocal Reasoning
            if (vocalReport.HasConflict)
            {
                sb.AppendLine($"🎙️ {vocalReport.WarningMessage}");
            }
            else if (trackA.VocalType != SLSKDONET.Models.VocalType.Instrumental && trackB.VocalType == SLSKDONET.Models.VocalType.Instrumental)
            {
                sb.AppendLine("✨ Vocals clearing: Excellent spot to let the beat ride.");
            }

            // 4. Energy/BPM Reasoning
            if (suggestion.BpmDrift > (trackA.Bpm * 0.03))
            {
                sb.AppendLine("⚠️ Wide BPM gap (>3%). Use Sync or a hard cut to avoid drift.");
            }

            double energyA = trackA.Energy ?? trackA.AudioFeatures?.Energy ?? 0;
            double energyB = trackB.Energy ?? trackB.AudioFeatures?.Energy ?? 0;
            if (Math.Abs(energyA - energyB) > 0.3)
            {
                string direction = energyB > energyA ? "Lift" : "Reset";
                sb.AppendLine($"⚡ Energy {direction} detected ({energyA:F2} → {energyB:F2}).");
            }

            // 5. Optimal Time
            if (suggestion.OptimalTransitionTime.HasValue)
            {
                sb.AppendLine($"🎯 Optimal transition moment: {suggestion.OptimalTransitionTime.Value:F1}s — {suggestion.OptimalTransitionReason ?? "structural alignment"}.");
            }

            return sb.ToString().TrimEnd();
        }

        private static string GetArchetypeReasoning(TransitionArchetype archetype, SLSKDONET.Data.LibraryEntryEntity trackA, SLSKDONET.Data.LibraryEntryEntity trackB)
        {
            return archetype switch
            {
                TransitionArchetype.BuildToDrop => 
                    "🚀 Build-to-Drop: Track A is building tension while Track B delivers the drop. Double-drop potential.",
                
                TransitionArchetype.DropSwap => 
                    "🔥 Drop-Swap: High-energy swap detected. Cut A at the phrase end and slam B's drop.",
                
                TransitionArchetype.VocalToInstrumental => 
                    $"🗣️ Vocal → Inst: {trackA.VocalType} ending. Safe to blend into the instrumental B.",
                
                TransitionArchetype.LongBlend => 
                    "🎚️ Long Blend: Locked keys and tight RPM. Smooth 32-bar mix recommended.",
                
                TransitionArchetype.EnergyReset => 
                    "📉 Energy Reset: Bringing the floor down. Good for a break or atmosphere change.",
                
                TransitionArchetype.QuickCut => 
                    "✂️ Quick Cut: Standard transition. Focus on phrasing alignment.",
                
                _ => "Standard transition."
            };
        }
    }
}
