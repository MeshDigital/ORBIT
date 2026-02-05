using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using QuestPDF.Fluent;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Library;

namespace SLSKDONET.Services.Export
{
    /// <summary>
    /// Gig Bag Service: Creates a complete "no panic" export package for DJs.
    /// Generates Main USB, Backup USB, Emergency Card, and Set Autopsy File.
    /// </summary>
    public interface IGigBagService
    {
        Task<GigBagResult> CreateGigBagAsync(SetListEntity setList, string mainUsbPath, GigBagOptions options);
    }

    public class GigBagOptions
    {
        public bool IncludeBackup { get; set; } = true;
        public bool IncludeEmergencyCard { get; set; } = true;
        public bool IncludeAutopsy { get; set; } = true;
        public string BackupSubfolder { get; set; } = "ORBIT_BACKUP";
        public string EmergencyCardFilename { get; set; } = "EMERGENCY_CARD.pdf";  // Changed from .md to .pdf
        public string AutopsyFilename { get; set; } = ".orbit_autopsy.json";
    }

    public class GigBagResult
    {
        public bool MainUsbComplete { get; set; }
        public bool BackupComplete { get; set; }
        public bool EmergencyCardComplete { get; set; }
        public bool AutopsyComplete { get; set; }
        public List<string> Errors { get; set; } = new();

        public bool AllComplete => MainUsbComplete && BackupComplete && EmergencyCardComplete && AutopsyComplete;
    }

    public class GigBagService : IGigBagService
    {
        private readonly ILogger<GigBagService> _logger;
        private readonly IRekordboxExportService _exportService;
        private readonly ILibraryService _libraryService;

        public GigBagService(
            ILogger<GigBagService> logger, 
            IRekordboxExportService exportService,
            ILibraryService libraryService)
        {
            _logger = logger;
            _exportService = exportService;
            _libraryService = libraryService;
        }

        public async Task<GigBagResult> CreateGigBagAsync(SetListEntity setList, string mainUsbPath, GigBagOptions options)
        {
            var result = new GigBagResult();
            _logger.LogInformation("🎒 Creating Gig Bag for set: {SetName}", setList.Name);

            // 1. Main USB is handled by RekordboxExportService (already called before this)
            result.MainUsbComplete = true;

            // 2. Backup USB
            if (options.IncludeBackup)
            {
                try
                {
                    var backupPath = Path.Combine(mainUsbPath, options.BackupSubfolder);
                    await CreateBackupAsync(setList, backupPath);
                    result.BackupComplete = true;
                    _logger.LogInformation("✓ Backup USB created at {BackupPath}", backupPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Backup USB creation failed");
                    result.Errors.Add($"Backup failed: {ex.Message}");
                }
            }

            // 3. Emergency Card
            if (options.IncludeEmergencyCard)
            {
                try
                {
                    var cardPath = Path.Combine(mainUsbPath, options.EmergencyCardFilename);
                    await GenerateEmergencyCardAsync(setList, cardPath);
                    result.EmergencyCardComplete = true;
                    _logger.LogInformation("✓ Emergency Card created at {CardPath}", cardPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Emergency Card generation failed");
                    result.Errors.Add($"Emergency Card failed: {ex.Message}");
                }
            }

            // 4. Set Autopsy File
            if (options.IncludeAutopsy)
            {
                try
                {
                    var autopsyPath = Path.Combine(mainUsbPath, options.AutopsyFilename);
                    await GenerateAutopsyFileAsync(setList, autopsyPath);
                    result.AutopsyComplete = true;
                    _logger.LogInformation("✓ Set Autopsy created at {AutopsyPath}", autopsyPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Set Autopsy generation failed");
                    result.Errors.Add($"Autopsy failed: {ex.Message}");
                }
            }

            return result;
        }

        /// <summary>
        /// Creates a backup copy of the set in an alternate directory structure.
        /// </summary>
        private async Task CreateBackupAsync(SetListEntity setList, string backupPath)
        {
            Directory.CreateDirectory(backupPath);

            // Create a simple tracklist backup (not full XML, just references)
            var tracklistPath = Path.Combine(backupPath, "TRACKLIST.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"ORBIT BACKUP: {setList.Name}");
            sb.AppendLine($"Created: {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine(new string('=', 50));
            sb.AppendLine();

            int position = 1;
            foreach (var track in setList.Tracks.OrderBy(t => t.Position))
            {
                sb.AppendLine($"{position:D2}. {track.TrackUniqueHash}");
                position++;
            }

            await File.WriteAllTextAsync(tracklistPath, sb.ToString());

            // Copy emergency metadata
            await File.WriteAllTextAsync(
                Path.Combine(backupPath, "SET_INFO.txt"),
                $"Set: {setList.Name}\nTracks: {setList.Tracks.Count}\nFlowHealth: {setList.FlowHealth:P0}\nExported: {DateTime.Now:O}");
        }

        /// <summary>
        /// Generates a DJ-friendly cheat sheet PDF with key, BPM, and timing info.
        /// Uses QuestPDF with dark-mode design optimized for booth legibility.
        /// </summary>
        private async Task GenerateEmergencyCardAsync(SetListEntity setList, string cardPath)
        {
            // Resolve track metadata from library
            var trackHashes = setList.Tracks
                .OrderBy(t => t.Position)
                .Select(t => t.TrackUniqueHash)
                .ToList();
            
            var libraryTracks = await _libraryService.GetLibraryEntriesByHashesAsync(trackHashes);
            var trackLookup = libraryTracks.ToDictionary(t => t.UniqueHash ?? string.Empty, t => t);

            // Build EmergencyCard track list
            var emergencyTracks = new List<EmergencyCardTrack>();
            int position = 1;
            
            foreach (var setTrack in setList.Tracks.OrderBy(t => t.Position))
            {
                var track = new EmergencyCardTrack { Index = position };
                
                if (trackLookup.TryGetValue(setTrack.TrackUniqueHash, out var libraryTrack))
                {
                    track.Title = libraryTrack.Title ?? "Unknown";
                    track.Artist = libraryTrack.Artist ?? "Unknown";
                    track.Bpm = libraryTrack.BPM ?? 0;
                    track.Key = libraryTrack.MusicalKey ?? "—";
                    track.Energy = (float)(libraryTrack.Energy ?? 0.5);
                    
                    // Map vocal type to tag
                    track.VocalTag = libraryTrack.VocalType switch
                    {
                        SLSKDONET.Models.VocalType.Instrumental => "INST",
                        SLSKDONET.Models.VocalType.SparseVocals => "SPARSE",
                        SLSKDONET.Models.VocalType.HookOnly => "HOOK",
                        SLSKDONET.Models.VocalType.FullLyrics => "DENSE",
                        _ => "—"
                    };
                    
                    // Generate mentor advice from transition type
                    track.MentorVerdict = setTrack.TransitionType.ToString() switch
                    {
                        "DropSwap" => "Cut at phrase end, slam the drop",
                        "SmoothBlend" => "32-bar overlap, let it breathe",
                        "EchoOut" => "Echo filter, energy reset",
                        "ColdCut" => "Sharp cut on downbeat",
                        _ => "Standard blend"
                    };
                }
                else
                {
                    track.Title = setTrack.TrackUniqueHash.Substring(0, 8) + "...";
                    track.Artist = "Unknown";
                    track.MentorVerdict = "Metadata not found";
                }
                
                emergencyTracks.Add(track);
                position++;
            }

            // Generate PDF using QuestPDF
            var document = new EmergencyCardDocument(setList.Name, emergencyTracks);
            document.GeneratePdf(cardPath);
            
            await Task.CompletedTask; // Satisfy async signature
        }

        /// <summary>
        /// Generates a machine-readable JSON file for post-gig analysis.
        /// Can be re-imported into ORBIT for learning and improvement.
        /// </summary>
        private async Task GenerateAutopsyFileAsync(SetListEntity setList, string autopsyPath)
        {
            var autopsy = new SetAutopsy
            {
                SetName = setList.Name,
                ExportedAt = DateTime.UtcNow,
                FlowHealth = setList.FlowHealth,
                TrackCount = setList.Tracks.Count,
                Tracks = setList.Tracks.OrderBy(t => t.Position).Select(t => new AutopsyTrack
                {
                    Position = t.Position,
                    TrackHash = t.TrackUniqueHash,
                    TransitionType = t.TransitionType.ToString(),
                    ManualOffset = t.ManualOffset
                }).ToList()
            };

            var json = JsonSerializer.Serialize(autopsy, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(autopsyPath, json);
        }
    }

    // Internal models for autopsy serialization
    public class SetAutopsy
    {
        public string SetName { get; set; } = string.Empty;
        public DateTime ExportedAt { get; set; }
        public double FlowHealth { get; set; }
        public int TrackCount { get; set; }
        public List<AutopsyTrack> Tracks { get; set; } = new();
    }

    public class AutopsyTrack
    {
        public int Position { get; set; }
        public string TrackHash { get; set; } = string.Empty;
        public string TransitionType { get; set; } = string.Empty;
        public double ManualOffset { get; set; }
    }
}
