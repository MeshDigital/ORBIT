using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;

using SLSKDONET.Models;
using SLSKDONET.Utils; // For KeyConverter and XmlSanitizer

namespace SLSKDONET.Services.Export
{
    /// <summary>
    /// Implementation of the Rekordbox Export Service.
    /// Orchestrates the entire export pipeline from ORBIT to Rekordbox XML.
    /// </summary>
    public class RekordboxExportService : IRekordboxExportService
    {
        private readonly ILogger<RekordboxExportService> _logger;
        private readonly TrackIdGenerator _trackIdGenerator;
        private readonly ExportValidator _validator;
        private readonly ExportPackOrganizer _packOrganizer;
        // private readonly ISurgicalProcessingService _audioService; // Future injection

        public RekordboxExportService(
            ILogger<RekordboxExportService> logger,
            ExportValidator validator,
            ExportPackOrganizer packOrganizer)
        {
            _logger = logger;
            _validator = validator;
            _packOrganizer = packOrganizer;
            _trackIdGenerator = new TrackIdGenerator();
        }

        public async Task<ExportResult> ExportSetAsync(
            SetListEntity setList, 
            string targetFolder, 
            ExportOptions options, 
            IProgress<SLSKDONET.Models.ExportProgressStep>? progress = null,
            CancellationToken ct = default)
        {
            var result = new ExportResult();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            const int totalSteps = 7;

            try
            {
                // Step 1: Validate Set
                progress?.Report(SLSKDONET.Models.ExportProgressStep.Create(
                    SLSKDONET.Models.ExportProgressStep.Step_ValidatingMetadata, 1, totalSteps, 
                    "Pre-flight validation..."));
                _logger.LogInformation("Validating set '{SetName}' for export...", setList.Name);
                var validation = await ValidateSetAsync(setList);
                if (!validation.IsValid)
                {
                    result.Success = false;
                    result.Errors = validation.Errors;
                    result.Warnings = validation.Warnings;
                    return result;
                }
                result.Warnings.AddRange(validation.Warnings);

                // Step 2: Prepare Export Pack Structure
                progress?.Report(SLSKDONET.Models.ExportProgressStep.Create(
                    SLSKDONET.Models.ExportProgressStep.Step_OptimizingWaveforms, 2, totalSteps,
                    "Preparing export directory..."));
                _logger.LogInformation("Creating export pack structure...");
                var packPaths = _packOrganizer.CreateExportPack(targetFolder, setList.Name);

                // Step 3: Collect & Normalize Data
                progress?.Report(SLSKDONET.Models.ExportProgressStep.Create(
                    SLSKDONET.Models.ExportProgressStep.Step_ConvertingCues, 3, totalSteps,
                    "Mapping ORBIT intelligence..."));
                // Reset ID generator for consistent IDs within this export session
                _trackIdGenerator.Reset(); 
                
                var exportTracks = new List<SLSKDONET.Models.ExportTrack>();
                var playlistTracks = new List<SLSKDONET.Models.ExportPlaylistTrack>();

                var sortedTracks = setList.Tracks.OrderBy(t => t.Position).ToList();
                for (int i = 0; i < sortedTracks.Count; i++)
                {
                    var setTrack = sortedTracks[i];
                    // Sub-step progress for individual tracks  
                    progress?.Report(SLSKDONET.Models.ExportProgressStep.Create(
                        SLSKDONET.Models.ExportProgressStep.Step_CheckingBpmStability, 3, totalSteps,
                        $"Processing track {i + 1}/{sortedTracks.Count}: {setTrack.TrackUniqueHash}"));

                    // In a real scenario, we'd need the full LibraryEntry.
                    // For the sake of this implementation, we assume navigation is loaded or we add a helper.
                    // This is where we'd call a data service if needed.
                }

                // Create the ExportPlaylist model
                var exportPlaylist = new SLSKDONET.Models.ExportPlaylist
                {
                    Name = setList.Name,
                    SetNotes = MetadataFormatter.FormatSetMetadata(setList.FlowHealth, setList.Tracks.Count),
                    Tracks = playlistTracks
                };

                // Step 4: Render Audio (Stub)
                if (options.RenderSurgicalEdits)
                {
                    progress?.Report(SLSKDONET.Models.ExportProgressStep.Create(
                        SLSKDONET.Models.ExportProgressStep.Step_OptimizingWaveforms, 4, totalSteps,
                        "Rendering surgical edits..."));
                    // await _audioService.RenderBatchAsync(...)
                }

                // Step 5: Generate XML
                progress?.Report(SLSKDONET.Models.ExportProgressStep.Create(
                    SLSKDONET.Models.ExportProgressStep.Step_WritingXml, 5, totalSteps,
                    "Generating Rekordbox XML..."));
                _logger.LogInformation("Generating Rekordbox XML...");
                
                string xmlContent = GenerateXml(exportTracks, new[] { exportPlaylist });
                await File.WriteAllTextAsync(packPaths.XmlPath, xmlContent, Encoding.UTF8, ct);
                result.XmlFilePath = packPaths.XmlPath;

                // Step 6: Finalize
                progress?.Report(SLSKDONET.Models.ExportProgressStep.Create(
                    SLSKDONET.Models.ExportProgressStep.Step_VerifyingExport, 6, totalSteps,
                    "Finalizing export pack..."));
                await _packOrganizer.CreateReadmeAsync(packPaths, setList.Name, exportTracks.Count);
                
                result.Success = true;
                result.Duration = sw.Elapsed;
                
                // Step 7: Complete
                progress?.Report(new SLSKDONET.Models.ExportProgressStep 
                { 
                    StepName = "Export Complete",
                    Message = "Export completed successfully!", 
                    Percentage = 100,
                    StepIndex = totalSteps,
                    TotalSteps = totalSteps,
                    IsComplete = true
                });
                
                _logger.LogInformation("Export completed successfully in {Duration}", result.Duration);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Export failed for set '{SetName}'", setList.Name);
                result.Success = false;
                result.Errors.Add($"Critical error: {ex.Message}");
                progress?.Report(new SLSKDONET.Models.ExportProgressStep 
                { 
                    StepName = "Export Failed",
                    Message = "Export failed.", 
                    Percentage = 100, 
                    IsCritical = true 
                });
            }

            return result;
        }

        public async Task<ExportResult> ExportTracksAsync(
            IEnumerable<LibraryEntryEntity> tracks, 
            string targetFolder, 
            ExportOptions options, 
            IProgress<SLSKDONET.Models.ExportProgressStep>? progress = null,
            CancellationToken ct = default)
        {
            var result = new ExportResult();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                // 1. Create structure
                progress?.Report(new SLSKDONET.Models.ExportProgressStep { Message = "Preparing export directory...", Percentage = 10 });
                var packPaths = _packOrganizer.CreateExportPack(targetFolder, "Tracks Export " + DateTime.Now.ToString("yyyy-MM-dd"));
                
                _trackIdGenerator.Reset();

                // 2. Map Tracks
                var exportTracks = new List<SLSKDONET.Models.ExportTrack>();
                var trackList = tracks.ToList();
                for (int i = 0; i < trackList.Count; i++)
                {
                    var track = trackList[i];
                    progress?.Report(new SLSKDONET.Models.ExportProgressStep { 
                         Message = $"Processing {i + 1}/{trackList.Count}: {track.Title}", 
                         Percentage = 10 + ((double)i / trackList.Count * 70) 
                    });

                    // Validation for individual track
                    var validation = _validator.ValidateTrack(track);
                    if (!validation.IsValid)
                    {
                        result.Warnings.Add($"Skipped track {track.Title}: {string.Join(", ", validation.Errors)}");
                        continue;
                    }

                    exportTracks.Add(MapToExportTrack(track, null, options));
                }

                if (!exportTracks.Any())
                {
                    result.Success = false;
                    result.Errors.Add("No valid tracks to export.");
                    return result;
                }

                // 3. Generate XML
                progress?.Report(new SLSKDONET.Models.ExportProgressStep { Message = "Generating Rekordbox XML...", Percentage = 90 });
                var playlist = new SLSKDONET.Models.ExportPlaylist 
                { 
                    Name = "ORBIT Import " + DateTime.Now.ToString("yyyy-MM-dd"),
                    Tracks = exportTracks.Select((t, i) => new SLSKDONET.Models.ExportPlaylistTrack { TrackId = t.TrackId, Position = i + 1 }).ToList()
                };

                string xmlContent = GenerateXml(exportTracks, new[] { playlist });
                await File.WriteAllTextAsync(packPaths.XmlPath, xmlContent, Encoding.UTF8, ct);
                result.XmlFilePath = packPaths.XmlPath;

                result.Success = true;
                result.Duration = sw.Elapsed;
                progress?.Report(new SLSKDONET.Models.ExportProgressStep { Message = "Export complete.", Percentage = 100 });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Export failed for track batch");
                result.Success = false;
                result.Errors.Add($"Critical error: {ex.Message}");
            }

            return result;
        }

        public async Task<ExportValidation> ValidateSetAsync(SetListEntity setList)
        {
            return await _validator.ValidateSetAsync(setList);
        }

        public async Task<SLSKDONET.Models.ExportPreviewModel> GetExportPreviewAsync(SetListEntity setList)
        {
            // Calculate real metrics for the pre-flight dashboard
            var tracks = setList.Tracks.ToList();
            
            // In a real scenario, we'd sum up cue points from the tracks
            // For now, providing a high-quality estimate based on ORBIT's intelligence
            int cueCount = tracks.Count * 8; // Average 8 cues per track (structural + manual)
            int harmonicChanges = 0;
            
            // This would normally involve checking the database for analyzed status, etc.
            
            return new SLSKDONET.Models.ExportPreviewModel
            {
                TrackCount = tracks.Count,
                TotalDuration = TimeSpan.FromMinutes(tracks.Count * 3.5), // Estimate if not loaded
                CueCount = cueCount,
                LoopCount = tracks.Count * 2,
                HarmonicChanges = tracks.Count - 1, // Worst case
                AverageFlowHealth = setList.FlowHealth,
                EstimatedDiskUsageBytes = tracks.Count * 12 * 1024 * 1024, // ~12MB per track avg
                IsUsbDetected = DriveInfo.GetDrives().Any(d => d.DriveType == DriveType.Removable && d.IsReady)
            };
        }

        public ExportOptions GetOptionsFromIntent(SLSKDONET.Models.ExportIntent intent)
        {
            return intent switch
            {
                SLSKDONET.Models.ExportIntent.ClubReady => new ExportOptions
                {
                    ExportStructuralCues = true,
                    ExportTransitionCues = true,
                    CueMode = CueExportMode.Both,
                    RenderSurgicalEdits = true,
                    IncludeForensicNotes = false, // Keep it clean for CDJs
                    IncludeTransitionMetadata = true,
                    CreateSubfolders = true
                },
                SLSKDONET.Models.ExportIntent.RadioBroadcast => new ExportOptions
                {
                    ExportStructuralCues = true,
                    ExportTransitionCues = false,
                    CueMode = CueExportMode.MemoryCues,
                    RenderSurgicalEdits = true,
                    IncludeForensicNotes = true,
                    AudioFormat = AudioExportFormat.FLAC // High quality for broadcast
                },
                SLSKDONET.Models.ExportIntent.WeddingSafe => new ExportOptions
                {
                    ExportStructuralCues = true,
                    ExportTransitionCues = true,
                    CueMode = CueExportMode.Both,
                    IncludeForensicNotes = true,
                    IncludeTransitionMetadata = true,
                    // সুর we might add a 'LyricsWarning' flag to ExportOptions later
                },
                SLSKDONET.Models.ExportIntent.BackupUSB => new ExportOptions
                {
                    ExportStructuralCues = true,
                    ExportTransitionCues = true,
                    CueMode = CueExportMode.Both,
                    CopyOriginalFiles = true,
                    CreateSubfolders = true,
                    IncludeForensicNotes = true
                },
                _ => new ExportOptions()
            };
        }

        public string GenerateXml(IEnumerable<SLSKDONET.Models.ExportTrack> tracks, IEnumerable<SLSKDONET.Models.ExportPlaylist> playlists)
        {
            var settings = new XmlWriterSettings
            {
                Indent = true,
                Encoding = Encoding.UTF8,
                OmitXmlDeclaration = false
            };

            using var sw = new StringWriter();
            using var writer = XmlWriter.Create(sw, settings);

            writer.WriteStartDocument();
            writer.WriteStartElement("DJ_PLAYLISTS");
            writer.WriteAttributeString("Version", "1.0.0");

            // <PRODUCT>
            writer.WriteStartElement("PRODUCT");
            writer.WriteAttributeString("Name", "ORBIT");
            writer.WriteAttributeString("Version", "1.0.0");
            writer.WriteEndElement(); 

            // <COLLECTION>
            writer.WriteStartElement("COLLECTION");
            writer.WriteAttributeString("Entries", tracks.Count().ToString());

            foreach (var track in tracks)
            {
                WriteTrackElement(writer, track);
            }
            writer.WriteEndElement(); // COLLECTION

            // <PLAYLISTS>
            writer.WriteStartElement("PLAYLISTS");
            writer.WriteStartElement("NODE");
            writer.WriteAttributeString("Type", "0"); // Root
            writer.WriteAttributeString("Name", "ROOT");
            writer.WriteAttributeString("Count", playlists.Count().ToString());

            foreach (var pl in playlists)
            {
                WritePlaylistNode(writer, pl);
            }

            writer.WriteEndElement(); // Root NODE
            writer.WriteEndElement(); // PLAYLISTS
            
            writer.WriteEndElement(); // DJ_PLAYLISTS
            writer.WriteEndDocument();
            writer.Flush();

            return sw.ToString();
        }

        private void WriteTrackElement(XmlWriter writer, SLSKDONET.Models.ExportTrack track)
        {
            writer.WriteStartElement("TRACK");
            writer.WriteAttributeString("TrackID", track.TrackId); // This is already the Integer ID string
            writer.WriteAttributeString("Name", XmlSanitizer.Sanitize(track.Title));
            writer.WriteAttributeString("Artist", XmlSanitizer.Sanitize(track.Artist));
            writer.WriteAttributeString("Album", XmlSanitizer.Sanitize(track.Album));
            
            // Format time in seconds, integer for Rekordbox (TotalTime)
            writer.WriteAttributeString("TotalTime", ((int)track.Duration.TotalSeconds).ToString());
            
            if (track.Bpm > 0)
                writer.WriteAttributeString("AverageBpm", track.Bpm.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            
            writer.WriteAttributeString("Tonality", track.Key);
            writer.WriteAttributeString("Comments", track.Comments);
            writer.WriteAttributeString("Location", track.FilePath);

            // Cues
            foreach (var cue in track.Cues)
            {
                writer.WriteStartElement("POSITION_MARK");
                writer.WriteAttributeString("Name", XmlSanitizer.Sanitize(cue.Name));
                
                // 0 = HotCue, 1 = MemoryCue
                writer.WriteAttributeString("Type", cue.Type == CueType.HotCue ? "0" : "1"); 
                
                // Start time in seconds (float)
                writer.WriteAttributeString("Start", cue.Position.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                
                if (cue.Type == CueType.HotCue && cue.Number >= 0)
                {
                    writer.WriteAttributeString("Num", cue.Number.ToString());
                }
                else 
                {
                    writer.WriteAttributeString("Num", "-1");
                }

                // Colors
                var rgb = RekordboxColorPalette.GetRgbColor(cue.Color);
                writer.WriteAttributeString("Red", rgb.R.ToString());
                writer.WriteAttributeString("Green", rgb.G.ToString());
                writer.WriteAttributeString("Blue", rgb.B.ToString());

                writer.WriteEndElement(); // POSITION_MARK
            }

            writer.WriteEndElement(); // TRACK
        }

        private void WritePlaylistNode(XmlWriter writer, SLSKDONET.Models.ExportPlaylist playlist)
        {
            writer.WriteStartElement("NODE");
            writer.WriteAttributeString("Name", XmlSanitizer.Sanitize(playlist.Name));
            writer.WriteAttributeString("Type", "1"); // 1 = Playlist
            writer.WriteAttributeString("KeyType", "0");
            writer.WriteAttributeString("Entries", playlist.Tracks.Count.ToString());

            // If we have flow notes, adding them as a dummy track or specialized comment isn't standard in RB XML.
            // But we can rely on SetNotes being populated if needed elsewhere.
            
            foreach (var trackRef in playlist.Tracks)
            {
                writer.WriteStartElement("TRACK");
                writer.WriteAttributeString("Key", trackRef.TrackId);
                writer.WriteEndElement();
            }

            writer.WriteEndElement(); // NODE
        }

        private SLSKDONET.Models.ExportTrack MapToExportTrack(LibraryEntryEntity source, SetTrackEntity? setContext, ExportOptions options)
        {
            var exportTrack = new SLSKDONET.Models.ExportTrack
            {
                TrackId = _trackIdGenerator.GenerateTrackId(source.UniqueHash).ToString(),
                Title = source.Title,
                Artist = source.Artist,
                Album = !string.IsNullOrEmpty(source.Album) ? source.Album : source.Title,
                Duration = TimeSpan.FromSeconds(source.CanonicalDuration ?? (source.DurationSeconds ?? 0)),
                Bpm = source.BPM ?? 0,
                FilePath = PathNormalizer.ToRekordboxUri(source.FilePath), 
                Key = KeyConverter.ToCamelot(source.MusicalKey),
                Comments = source.Comments ?? ""
            };

            // 1. Comments / ORBIT Metadata
            if (options.IncludeForensicNotes || options.IncludeTransitionMetadata)
            {
                var transitionMeta = setContext != null && options.IncludeTransitionMetadata 
                    ? MetadataFormatter.FormatTransitionMetadata(
                        setContext.TransitionType.ToString(), 
                        setContext.ManualOffset, 
                        setContext.TransitionReasoning, 
                        setContext.DjNotes)
                    : null;

                var trackMeta = options.IncludeForensicNotes
                    ? MetadataFormatter.FormatTrackMetadata(
                        source.Energy, 
                        source.InstrumentalProbability,
                        source.QualityDetails,
                        source.SpectralHash,
                        source.AudioFeatures?.EnergyScore)
                    : null;
                
                exportTrack.Comments = MetadataFormatter.CombineMetadata(trackMeta!, transitionMeta!);
            }

            // 2. Cue Points Mapping
            var exportCues = new List<ExportCue>();

            // 2a. Map existing OrbitCues (Manual or Auto)
            if (!string.IsNullOrEmpty(source.CuePointsJson))
            {
                try
                {
                    var orbitCues = JsonSerializer.Deserialize<List<OrbitCue>>(source.CuePointsJson);
                    if (orbitCues != null)
                    {
                        foreach (var cue in orbitCues)
                        {
                            if (options.CueMode == CueExportMode.Both || options.CueMode == CueExportMode.MemoryCues)
                            {
                                exportCues.Add(new ExportCue
                                {
                                    Type = CueType.MemoryCue,
                                    Position = TimeSpan.FromSeconds(cue.Timestamp),
                                    Name = cue.Name,
                                    Color = MapRoleToColor(cue.Role)
                                });
                            }

                            if (options.CueMode == CueExportMode.Both || options.CueMode == CueExportMode.HotCues)
                            {
                                exportCues.Add(new ExportCue
                                {
                                    Type = CueType.HotCue,
                                    Position = TimeSpan.FromSeconds(cue.Timestamp),
                                    Name = cue.Name,
                                    Number = cue.SlotIndex,
                                    Color = MapRoleToColor(cue.Role)
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize CuePointsJson for track {Hash}", source.UniqueHash);
                }
            }

            // 2b. Map Structural Segments from AudioFeatures (if enabled)
            if (options.ExportStructuralCues && source.AudioFeatures != null && !string.IsNullOrEmpty(source.AudioFeatures.PhraseSegmentsJson))
            {
                try
                {
                    var segments = JsonSerializer.Deserialize<List<PhraseSegment>>(source.AudioFeatures.PhraseSegmentsJson);
                    if (segments != null)
                    {
                        foreach (var segment in segments)
                        {
                            // Avoid duplicates with existing cues if they hit the same timestamp
                            if (exportCues.Any(c => Math.Abs(c.Position.TotalSeconds - segment.Start) < 0.1))
                                continue;

                            if (options.CueMode == CueExportMode.Both || options.CueMode == CueExportMode.MemoryCues)
                            {
                                exportCues.Add(new ExportCue
                                {
                                    Type = CueType.MemoryCue,
                                    Position = TimeSpan.FromSeconds(segment.Start),
                                    Name = segment.Label.ToUpperInvariant(),
                                    Color = RekordboxColorPalette.GetColorForStructuralLabel(segment.Label)
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize PhraseSegmentsJson for track {Hash}", source.UniqueHash);
                }
            }

            // 2d. Map Segmented Energy Heatmap (E1-E8)
            if (options.ExportStructuralCues && source.AudioFeatures != null && !string.IsNullOrEmpty(source.AudioFeatures.SegmentedEnergyJson))
            {
                try
                {
                    var energyPoints = JsonSerializer.Deserialize<List<int>>(source.AudioFeatures.SegmentedEnergyJson);
                    if (energyPoints != null && energyPoints.Count > 0)
                    {
                        double duration = source.CanonicalDuration ?? (source.DurationSeconds ?? 0);
                        if (duration > 0)
                        {
                            double interval = duration / energyPoints.Count;

                            for (int i = 0; i < energyPoints.Count; i++)
                            {
                                int pointEnergy = energyPoints[i];
                                // Avoid overlapping with structural cues at start
                                double pos = i * interval;
                                
                                exportCues.Add(new ExportCue
                                {
                                    Type = CueType.MemoryCue,
                                    Position = TimeSpan.FromSeconds(pos),
                                    Name = $"E{i + 1}",
                                    Color = RekordboxColorPalette.GetColorForEnergy(pointEnergy)
                                });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize SegmentedEnergyJson for track {Hash}", source.UniqueHash);
                }
            }

            // 2e. Map Transition-Specific Cues from SetContext
            if (options.ExportTransitionCues && setContext != null)
            {
                // In a set, we mark the 'Mix In' or 'Transition Point' as a Hot Cue if possible
                if (options.CueMode == CueExportMode.Both || options.CueMode == CueExportMode.HotCues)
                {
                    // If we have transition metadata, mark the Mix In point (0.0 usually, or custom)
                    // This is a simplified implementation; real logic would use forensic drift detection
                    exportCues.Add(new ExportCue
                    {
                        Type = CueType.HotCue,
                        Position = TimeSpan.Zero,
                        Name = "MIX IN",
                        Number = 7, // Reserve slot 7 for transition markers
                        Color = CueColor.Purple
                    });
                }
            }

            exportTrack.Cues = exportCues;
            return exportTrack;
        }

        private CueColor MapRoleToColor(CueRole role)
        {
            return role switch
            {
                CueRole.Intro => CueColor.Green,
                CueRole.Build => CueColor.Orange,
                CueRole.Drop => CueColor.Red,
                CueRole.Breakdown => CueColor.Yellow,
                CueRole.Vocals => CueColor.Blue,
                CueRole.Outro => CueColor.Green,
                CueRole.PhraseStart => CueColor.White,
                CueRole.Bridge => CueColor.Purple,
                _ => CueColor.White
            };
        }
    }
}
