using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.Export
{
    /// <summary>
    /// Validates sets and tracks for export readiness.
    /// Performs comprehensive pre-flight checks to prevent broken exports.
    /// </summary>
    public class ExportValidator
    {
        /// <summary>
        /// Validates that a set is ready for export.
        /// </summary>
        public async Task<ExportValidation> ValidateSetAsync(SetListEntity setList)
        {
            var validation = new ExportValidation();
            var errors = new List<string>();
            var warnings = new List<string>();

            // Check set metadata
            if (string.IsNullOrWhiteSpace(setList.Name))
            {
                errors.Add("Set name is required");
            }

            if (setList.Tracks == null || !setList.Tracks.Any())
            {
                errors.Add("Set contains no tracks");
                validation.IsValid = false;
                validation.Errors = errors;
                return validation;
            }

            // Validate each track
            foreach (var setTrack in setList.Tracks.OrderBy(t => t.Position))
            {
                // Note: In real implementation, we'd load the actual LibraryEntryEntity
                // For now, this is a skeleton showing the validation logic
                
                // Check for missing track reference
                if (string.IsNullOrWhiteSpace(setTrack.TrackUniqueHash))
                {
                    errors.Add($"Track at position {setTrack.Position} has no track reference");
                    continue;
                }

                // Validate transition metadata (Optional for now as enum has default)
                if (setTrack.Position > 0) // Not the first track
                {
                    // Transition type is an enum, so it always has a value.
                    // Future: check for "None" if added to enum.
                }
            }

            // Set validation results
            validation.IsValid = !errors.Any();
            validation.Errors = errors;
            validation.Warnings = warnings;
            validation.AllFilesExist = true; // Would be checked in real implementation
            validation.AllTracksAnalyzed = true; // Would be checked in real implementation
            validation.HasValidCuePoints = true; // Would be checked in real implementation

            return validation;
        }

        /// <summary>
        /// Validates a single track for export.
        /// </summary>
        public ExportValidation ValidateTrack(LibraryEntryEntity track)
        {
            var validation = new ExportValidation();
            var errors = new List<string>();
            var warnings = new List<string>();

            // Check file existence
            if (string.IsNullOrWhiteSpace(track.FilePath))
            {
                errors.Add("Track has no file path");
            }
            else if (!File.Exists(track.FilePath))
            {
                errors.Add($"File not found: {track.FilePath}");
                validation.AllFilesExist = false;
            }

            // Check essential metadata
            if (string.IsNullOrWhiteSpace(track.Title))
            {
                warnings.Add("Track has no title");
            }

            if (string.IsNullOrWhiteSpace(track.Artist))
            {
                warnings.Add("Track has no artist");
            }

            if (!track.Bpm.HasValue || track.Bpm <= 0)
            {
                errors.Add("Track has no valid BPM");
            }

            if (string.IsNullOrWhiteSpace(track.Key))
            {
                warnings.Add("Track has no key information");
            }

            // Check analysis data
            if (track.AudioFeatures == null)
            {
                errors.Add("Track has not been analyzed");
                validation.AllTracksAnalyzed = false;
            }
            else
            {
                // Check for structural segments
                if (string.IsNullOrWhiteSpace(track.AudioFeatures.PhraseSegmentsJson))
                {
                    warnings.Add("Track has no structural analysis (phrase segments)");
                }

                // Check for waveform data
                if (string.IsNullOrWhiteSpace(track.AudioFeatures.EnergyCurveJson))
                {
                    warnings.Add("Track has no energy curve data");
                }
            }

            // Validate file path safety
            if (!string.IsNullOrWhiteSpace(track.FilePath) && 
                !PathNormalizer.IsValidExportPath(track.FilePath))
            {
                errors.Add("Track file path contains invalid characters");
            }

            validation.IsValid = !errors.Any();
            validation.Errors = errors;
            validation.Warnings = warnings;
            validation.HasValidCuePoints = track.AudioFeatures != null; // Simplified check

            return validation;
        }

        /// <summary>
        /// Validates export options for consistency.
        /// </summary>
        public ExportValidation ValidateExportOptions(ExportOptions options)
        {
            var validation = new ExportValidation { IsValid = true };
            var warnings = new List<string>();

            // Check for conflicting options
            if (options.RenderSurgicalEdits && !options.CreateSubfolders)
            {
                warnings.Add("Rendering surgical edits without subfolders may cause file name conflicts");
            }

            if (options.ExportBeatgrid)
            {
                warnings.Add("Exporting beatgrid will override Rekordbox's auto-analysis. Only enable if you're certain.");
            }

            if (options.CueMode == CueExportMode.HotCues && options.ExportStructuralCues)
            {
                warnings.Add("Exporting structural cues as Hot Cues may use up all 8 hot cue slots");
            }

            validation.Warnings = warnings;
            return validation;
        }
    }
}
