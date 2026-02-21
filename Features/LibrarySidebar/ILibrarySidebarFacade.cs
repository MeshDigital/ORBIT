using SLSKDONET.Services;
using SLSKDONET.ViewModels.Library;

namespace SLSKDONET.Features.LibrarySidebar;

/// <summary>
/// Encapsulates the various services needed by the sidebar to keep it decoupled.
/// </summary>
public interface ILibrarySidebarFacade
{
    // These will be mapped to existing services
    HarmonicMatchService HarmonicMatch { get; }
    TrackOperationsViewModel Operations { get; }
    // Add other services as needed by the sub-VMs
}
