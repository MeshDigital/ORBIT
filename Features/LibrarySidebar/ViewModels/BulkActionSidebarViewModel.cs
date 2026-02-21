using System.Collections.Generic;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Features.LibrarySidebar.ViewModels;

public class BulkActionSidebarViewModel : ReactiveObject, ISidebarContent
{
    private IReadOnlyList<PlaylistTrackViewModel> _activeTracks = System.Array.Empty<PlaylistTrackViewModel>();

    public async Task ActivateAsync(PlaylistTrackViewModel track)
    {
        await ActivateBulkAsync(new[] { track });
    }

    public async Task ActivateBulkAsync(IReadOnlyList<PlaylistTrackViewModel> tracks)
    {
        _activeTracks = tracks;
        // logic will be implemented in Phase 3
        await Task.CompletedTask;
    }

    public void Deactivate()
    {
        _activeTracks = System.Array.Empty<PlaylistTrackViewModel>();
    }
}
