using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Tagging
{
    public interface IUniversalCueService
    {
        Task SyncToTagsAsync(string filePath, List<OrbitCue> cues);
        Task ExportToXmlAsync(IEnumerable<PlaylistTrack> tracks);
    }

    public class UniversalCueService : IUniversalCueService
    {
        private readonly ILogger<UniversalCueService> _logger;
        private readonly ISeratoMarkerService _seratoService;
        private readonly RekordboxService _rekordboxService; // Phase 11.5

        public UniversalCueService(
            ILogger<UniversalCueService> logger,
            ISeratoMarkerService seratoService,
            RekordboxService rekordboxService)
        {
            _logger = logger;
            _seratoService = seratoService;
            _rekordboxService = rekordboxService;
        }

        public async Task SyncToTagsAsync(string filePath, List<OrbitCue> cues)
        {
             await _seratoService.WriteMarkersAsync(filePath, cues);
             // await _traktorService.WriteTagsAsync(filePath, cues);
        }

        public async Task ExportToXmlAsync(IEnumerable<PlaylistTrack> tracks)
        {
             try
             {
                 _logger.LogInformation("Exporting {Count} tracks to Rekordbox XML via Universal Bridge", tracks.Count());
                 
                 var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                 var outputPath = System.IO.Path.Combine(desktop, $"ORBIT_Export_{DateTime.Now:yyyyMMdd_HHmmss}.xml");
                 
                 await _rekordboxService.ExportPlaylistAsync(tracks.ToList(), "ORBIT Universal Export", outputPath);
                 
                 _logger.LogInformation("Export successful: {Path}", outputPath);
             }
             catch (Exception ex)
             {
                 _logger.LogError(ex, "Universal XML Export failed");
                 throw;
             }
        }
    }
}
