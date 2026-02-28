using System;
using System.Collections.ObjectModel;
using ReactiveUI;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels;

/// <summary>
/// State Orchestrator for the Unified DAW Workspace (2026 Alignment).
/// Tracks current context (Library, Search, etc.) and manages the shared track list.
/// </summary>
public partial class ActiveWorkspace : ReactiveObject
{
    private WorkspaceContext _currentContext = WorkspaceContext.LocalLibrary;
    public WorkspaceContext CurrentContext
    {
        get => _currentContext;
        set => this.RaiseAndSetIfChanged(ref _currentContext, value);
    }

    private ObservableCollection<IDisplayableTrack> _currentGridItems = new();
    public ObservableCollection<IDisplayableTrack> CurrentGridItems
    {
        get => _currentGridItems;
        set => this.RaiseAndSetIfChanged(ref _currentGridItems, value);
    }

    private IDisplayableTrack? _selectedTrack;
    public IDisplayableTrack? SelectedTrack
    {
        get => _selectedTrack;
        set => this.RaiseAndSetIfChanged(ref _selectedTrack, value);
    }

    public void SetContext(WorkspaceContext context)
    {
        CurrentContext = context;
        // In the future, this will trigger the logic to repopulate CurrentGridItems
    }

    public void SyncItems(System.Collections.Generic.IEnumerable<IDisplayableTrack> items)
    {
        CurrentGridItems.Clear();
        foreach (var item in items)
        {
            CurrentGridItems.Add(item);
        }
    }
}
