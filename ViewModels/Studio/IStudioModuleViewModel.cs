using System.Threading;
using System.Threading.Tasks;

namespace SLSKDONET.ViewModels.Studio;

/// <summary>
/// Standardized interface for ORBIT Studio modules that respond to track selection.
/// Ensures all hydration is cancellable to prevent UI freezes during rapid navigation.
/// </summary>
public interface IStudioModuleViewModel
{
    /// <summary>
    /// Asynchronously hydrates the module with the provided track context.
    /// </summary>
    Task LoadTrackContextAsync(IDisplayableTrack track, CancellationToken cancellationToken);

    /// <summary>
    /// Clears the current track context (e.g. when selection is null).
    /// </summary>
    void ClearContext();
}
