using System.Globalization;
using System.Text;

namespace SLSKDONET.Services.Export
{
    /// <summary>
    /// Formats ORBIT intelligence metadata for Rekordbox comment fields.
    /// Creates structured, CDJ-readable metadata blocks.
    /// </summary>
    public static class MetadataFormatter
    {
        /// <summary>
        /// Formats track-level ORBIT metadata for Rekordbox comments.
        /// </summary>
        public static string FormatTrackMetadata(
            double? energyLevel = null,
            double? vocalDensity = null,
            string? forensicNotes = null,
            string? structuralHash = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[ORBIT]");

            if (energyLevel.HasValue)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Energy={0:F2}", energyLevel.Value));
            }

            if (vocalDensity.HasValue)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "VocalDensity={0:F2}", vocalDensity.Value));
            }

            if (!string.IsNullOrWhiteSpace(structuralHash))
            {
                sb.AppendLine($"StructuralHash={structuralHash}");
            }

            if (!string.IsNullOrWhiteSpace(forensicNotes))
            {
                sb.AppendLine($"Notes={forensicNotes}");
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Formats transition metadata for Rekordbox comments.
        /// </summary>
        public static string FormatTransitionMetadata(
            string transitionType,
            double? transitionOffset = null,
            string? reasoning = null,
            string? djNotes = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[ORBIT TRANSITION]");
            sb.AppendLine($"Type={transitionType}");

            if (transitionOffset.HasValue)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Offset={0:F2}s", transitionOffset.Value));
            }

            if (!string.IsNullOrWhiteSpace(reasoning))
            {
                // Clean reasoning for single-line format
                string cleanReasoning = reasoning.Replace("\n", "; ").Replace("\r", "");
                sb.AppendLine($"Reason={cleanReasoning}");
            }

            if (!string.IsNullOrWhiteSpace(djNotes))
            {
                string cleanNotes = djNotes.Replace("\n", "; ").Replace("\r", "");
                sb.AppendLine($"DjNotes={cleanNotes}");
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Formats set-level flow health metadata for playlist comments.
        /// </summary>
        public static string FormatSetMetadata(
            double flowHealth,
            int trackCount,
            string? setNotes = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[ORBIT SET]");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "FlowHealth={0:P0}", flowHealth));
            sb.AppendLine($"Tracks={trackCount}");

            if (!string.IsNullOrWhiteSpace(setNotes))
            {
                string cleanNotes = setNotes.Replace("\n", "; ").Replace("\r", "");
                sb.AppendLine($"Notes={cleanNotes}");
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Combines multiple metadata blocks into a single comment field.
        /// </summary>
        public static string CombineMetadata(params string[] metadataBlocks)
        {
            var sb = new StringBuilder();
            
            foreach (var block in metadataBlocks)
            {
                if (!string.IsNullOrWhiteSpace(block))
                {
                    sb.AppendLine(block);
                    sb.AppendLine(); // Blank line between blocks
                }
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Formats a cue point name with ORBIT intelligence.
        /// Example: "DROP (Energy: 0.92)"
        /// </summary>
        public static string FormatCueName(string structuralLabel, double? energy = null)
        {
            if (energy.HasValue)
            {
                return $"{structuralLabel} (E:{energy.Value:F2})";
            }
            return structuralLabel;
        }
    }
}
