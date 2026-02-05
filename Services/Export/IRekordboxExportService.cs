using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;


namespace SLSKDONET.Services.Export
{
    /// <summary>
    /// Service for exporting ORBIT data to Rekordbox XML format.
    /// Bridges ORBIT's intelligence (structural analysis, set-prep, surgical edits)
    /// to Rekordbox's performance ecosystem (CDJs, playlists, cue points).
    /// </summary>
    public interface IRekordboxExportService
    {
        /// <summary>
        /// Exports a complete set (SetListEntity) to Rekordbox XML format.
        /// Includes all tracks, cue points, transition metadata, and flow intelligence.
        /// </summary>
        /// <param name="setList">The set to export</param>
        /// <param name="targetFolder">Destination folder for XML and rendered audio</param>
        /// <param name="options">Export configuration options</param>
        /// <param name="progress">Progress reporter for granular steps</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>Export result with file paths and validation status</returns>
        Task<ExportResult> ExportSetAsync(
            SetListEntity setList, 
            string targetFolder, 
            ExportOptions options,
            IProgress<SLSKDONET.Models.ExportProgressStep>? progress = null,
            CancellationToken ct = default);

        /// <summary>
        /// Exports individual tracks to Rekordbox XML format.
        /// </summary>
        Task<ExportResult> ExportTracksAsync(
            IEnumerable<LibraryEntryEntity> tracks,
            string targetFolder,
            ExportOptions options,
            IProgress<SLSKDONET.Models.ExportProgressStep>? progress = null,
            CancellationToken ct = default);

        /// <summary>
        /// Generates Rekordbox XML from export models without writing to disk.
        /// </summary>
        string GenerateXml(IEnumerable<SLSKDONET.Models.ExportTrack> tracks, IEnumerable<SLSKDONET.Models.ExportPlaylist> playlists);

        /// <summary>
        /// Validates that a set is ready for export.
        /// </summary>
        Task<ExportValidation> ValidateSetAsync(SetListEntity setList);

        /// <summary>
        /// Generates a pre-flight preview for the specified set.
        /// </summary>
        Task<SLSKDONET.Models.ExportPreviewModel> GetExportPreviewAsync(SetListEntity setList);

        /// <summary>
        /// Maps a high-level DJ Intent to specific technical export options.
        /// </summary>
        ExportOptions GetOptionsFromIntent(SLSKDONET.Models.ExportIntent intent);
    }

    /// <summary>
    /// Configuration options for Rekordbox export.
    /// </summary>
    public class ExportOptions
    {
        // Cue Point Options
        public bool ExportStructuralCues { get; set; } = true; // Intro, Drop, Breakdown, etc.
        public bool ExportTransitionCues { get; set; } = true; // Transition points between tracks
        public CueExportMode CueMode { get; set; } = CueExportMode.MemoryCues;
        
        // Audio Rendering Options
        public bool RenderSurgicalEdits { get; set; } = true; // Export edited audio files
        public AudioExportFormat AudioFormat { get; set; } = AudioExportFormat.WAV;
        public int AudioBitDepth { get; set; } = 24;
        public int AudioSampleRate { get; set; } = 44100;
        
        // Metadata Options
        public bool IncludeForensicNotes { get; set; } = true; // Add ORBIT intelligence to comments
        public bool IncludeTransitionMetadata { get; set; } = true; // Add transition reasoning
        public bool IncludeFlowHealth { get; set; } = true; // Add set-level flow score
        
        // File Organization
        public bool CreateSubfolders { get; set; } = true; // Organize by set/playlist
        public bool CopyOriginalFiles { get; set; } = false; // Copy source audio to export folder
        
        // Beatgrid Options
        public bool ExportBeatgrid { get; set; } = false; // Include beatgrid data (future)
    }

    public enum CueExportMode
    {
        HotCues,      // Export as Hot Cues (0-7, performance-oriented)
        MemoryCues,   // Export as Memory Cues (navigation-oriented)
        Both          // Export both types
    }

    public enum AudioExportFormat
    {
        WAV,
        AIFF,
        FLAC
    }

    /// <summary>
    /// Result of an export operation.
    /// </summary>
    public class ExportResult
    {
        public bool Success { get; set; }
        public string XmlFilePath { get; set; } = string.Empty;
        public List<string> RenderedAudioFiles { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// Validation result for export readiness.
    /// </summary>
    public class ExportValidation
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        
        // Specific validation checks
        public bool AllFilesExist { get; set; }
        public bool AllTracksAnalyzed { get; set; }
        public bool HasValidCuePoints { get; set; }
    }
}
