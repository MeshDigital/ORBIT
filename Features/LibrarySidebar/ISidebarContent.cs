using System.Threading.Tasks;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Features.LibrarySidebar;

public interface ISidebarContent
{
    Task ActivateAsync(PlaylistTrackViewModel track);
    Task ActivateBulkAsync(System.Collections.Generic.IReadOnlyList<PlaylistTrackViewModel> tracks);
    void Deactivate();
}
