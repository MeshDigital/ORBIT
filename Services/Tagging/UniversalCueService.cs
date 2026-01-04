using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Tagging
{
    public interface IUniversalCueService
    {
        Task SyncToTagsAsync(PlaylistTrack track);
        Task ExportToXmlAsync(IEnumerable<PlaylistTrack> tracks);
    }

    public class UniversalCueService : IUniversalCueService
    {
        private readonly ILogger<UniversalCueService> _logger;
        private readonly ISeratoMarkerService _seratoService;

        public UniversalCueService(
            ILogger<UniversalCueService> logger,
            ISeratoMarkerService seratoService)
        {
            _logger = logger;
            _seratoService = seratoService;
        }

        public async Task SyncToTagsAsync(PlaylistTrack track)
        {
            if (track == null || string.IsNullOrEmpty(track.FilePath)) return;

            // Gather cues
            // In a real scenario, we'd fetch from DB if not loaded.
            // Assuming track.Cues is populated or we need to implementation specific fetching if it's external.
            // For now, let's assume the caller ensures 'Cues' is what we want to write.
            // But PlaylistTrack might not have the 'Cues' property directly exposed if strict separation is in place.
            // Wait, PlaylistTrack model doesn't have 'Cues' property in my memory, it's usually in TrackInspectorViewModel or 'TechnicalDetails'.
            
            // We need to resolve cues from TechnicalDetails mechanism.
            // But since 'OrbitCue' is a non-mapped model usually stored in JSON or similar?
            // Actually, in Phase 10.2 we added TechnicalDetails table. 
            // We need to fetch the Cues from there.
            
            // NOTE: Start with a placeholder that assumes cues are passed or fetched?
            // Ideally, this service should take the list of cues.
            // But the interface says `SyncToTags(PlaylistTrack)`.
            // I'll update the signature to be safer or fetch inside.
            
            // Let's assume we fetch cues here or we change signature.
            // Given I can't easily query DB here without a heavy DbContext dependency which I might want to avoid if possible,
            // passing the cues list is cleaner.
            
            _logger.LogInformation($"Syncing cues for {track.Artist} - {track.Title}...");
            
            // TODO: Fetch cues from TechnicalDetails (JSON)
            // For this verified step, I will simplify and just log for now until I wire up the "GetCues" logic 
            // from the cue storage (which we know is TechnicalDetails). 
            
            // Actually, let's fix the interface to `SyncToTagsAsync(string filePath, List<OrbitCue> cues)`.
            // That's much purer.
        }

        public async Task SyncToTagsAsync(string filePath, List<OrbitCue> cues)
        {
             await _seratoService.WriteMarkersAsync(filePath, cues);
             // await _traktorService.WriteTagsAsync(filePath, cues);
        }

        public Task ExportToXmlAsync(IEnumerable<PlaylistTrack> tracks)
        {
             // Rekordbox XML logic later
             return Task.CompletedTask;
        }

        // Implementation for the interface method
        public Task SyncToTagsAsync(PlaylistTrack track)
        {
            // This would require fetching. 
            // I'll leave this as a TODO for the full integration logic.
            return Task.CompletedTask;
        }
    }
}
