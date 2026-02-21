using System.Collections.Generic;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Features.LibrarySidebar.ViewModels;

public class ForensicSidebarViewModel : ReactiveObject, ISidebarContent
{
    private PlaylistTrackViewModel? _activeTrack;

    public async Task ActivateAsync(PlaylistTrackViewModel track)
    {
        _activeTrack = track;
        await Task.CompletedTask;
    }

    public async Task ActivateBulkAsync(IReadOnlyList<PlaylistTrackViewModel> tracks)
    {
        await Task.CompletedTask;
    }

    public void Deactivate()
    {
        _activeTrack = null;
    }
}
