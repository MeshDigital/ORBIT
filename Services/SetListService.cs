using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Musical;

namespace SLSKDONET.Services
{
    public class SetListService
    {
        private readonly ILogger<SetListService> _logger;
        private readonly DatabaseService _databaseService;
        private readonly ITransitionAdvisorService _advisor;

        public SetListService(ILogger<SetListService> logger, DatabaseService databaseService, ITransitionAdvisorService advisor)
        {
            _logger = logger;
            _databaseService = databaseService;
            _advisor = advisor;
        }

        public async Task<SetListEntity> CreateSetListAsync(string name)
        {
            using var context = new AppDbContext();
            var setList = new SetListEntity { Name = name };
            context.SetLists.Add(setList);
            await context.SaveChangesAsync();
            return setList;
        }

        public async Task<List<SetListEntity>> GetAllSetListsAsync()
        {
            using var context = new AppDbContext();
            return await context.SetLists
                .Include(s => s.Tracks)
                .OrderByDescending(s => s.LastModifiedAt)
                .ToListAsync();
        }

        public async Task AddTrackToSetAsync(Guid setListId, string trackUniqueHash, int? position = null)
        {
            using var context = new AppDbContext();
            var setList = await context.SetLists.Include(s => s.Tracks).FirstOrDefaultAsync(s => s.Id == setListId);
            if (setList == null) return;

            int pos = position ?? (setList.Tracks.Any() ? setList.Tracks.Max(t => t.Position) + 1 : 0);

            var setTrack = new SetTrackEntity
            {
                SetListId = setListId,
                TrackUniqueHash = trackUniqueHash,
                Position = pos
            };

            context.SetTracks.Add(setTrack);
            setList.LastModifiedAt = DateTime.UtcNow;
            
            // Re-calculate Flow Health
            setList.FlowHealth = _advisor.CalculateFlowContinuity(setList);

            await context.SaveChangesAsync();
        }

        public async Task RemoveTrackFromSetAsync(Guid setTrackId)
        {
            using var context = new AppDbContext();
            var track = await context.SetTracks.FindAsync(setTrackId);
            if (track == null) return;

            context.SetTracks.Remove(track);
            
            var setList = await context.SetLists.Include(s => s.Tracks).FirstOrDefaultAsync(s => s.Id == track.SetListId);
            if (setList != null)
            {
                setList.LastModifiedAt = DateTime.UtcNow;
                setList.FlowHealth = _advisor.CalculateFlowContinuity(setList);
            }

            await context.SaveChangesAsync();
        }
    }
}
