using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Features.LibrarySidebar.ViewModels;

public class TransitionProberViewModel : ReactiveObject, ISidebarContent
{
    private PlaylistTrackViewModel? _primaryTrack;
    private PlaylistTrackViewModel? _secondaryTrack;

    public PlaylistTrackViewModel? PrimaryTrack
    {
        get => _primaryTrack;
        set => this.RaiseAndSetIfChanged(ref _primaryTrack, value);
    }

    public PlaylistTrackViewModel? SecondaryTrack
    {
        get => _secondaryTrack;
        set => this.RaiseAndSetIfChanged(ref _secondaryTrack, value);
    }

    public Task ActivateAsync(PlaylistTrackViewModel track)
    {
        PrimaryTrack = track;
        return Task.CompletedTask;
    }

    public void SetSecondaryTrack(PlaylistTrackViewModel track)
    {
        SecondaryTrack = track;
    }

    public Task ActivateBulkAsync(IReadOnlyList<PlaylistTrackViewModel> tracks)
    {
        return Task.CompletedTask;
    }

    public void Deactivate()
    {
        PrimaryTrack = null;
        SecondaryTrack = null;
    }
}
