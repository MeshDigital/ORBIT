using System.Collections.Generic;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Features.LibrarySidebar.ViewModels;

public class SimilaritySidebarViewModel : ReactiveObject, ISidebarContent
{
    private PlaylistTrackViewModel? _activeTrack;

    public async Task ActivateAsync(PlaylistTrackViewModel track)
    {
        _activeTrack = track;
        // Logic for loading matches will be implemented in Phase 3
        await Task.CompletedTask;
    }

    public async Task ActivateBulkAsync(IReadOnlyList<PlaylistTrackViewModel> tracks)
    {
        // Not applicable for similarity
        await Task.CompletedTask;
    }

    public void Deactivate()
    {
        _activeTrack = null;
    }
}
