using System;
using System.ComponentModel;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels;

public partial class LibraryViewModel
{
    private bool _isMixHelperVisible;
    public bool IsMixHelperVisible
    {
        get => _isMixHelperVisible;
        set 
        { 
            if (SetProperty(ref _isMixHelperVisible, value))
            {
                UpdateWorkspaceFromState();
                OnPropertyChanged(nameof(IsRightPanelVisible));
            }
        }
    }

    private ActiveWorkspace _currentWorkspace = ActiveWorkspace.Selector;
    public ActiveWorkspace CurrentWorkspace
    {
        get => _currentWorkspace;
        set
        {
            if (SetProperty(ref _currentWorkspace, value))
            {
                ApplyWorkspaceState();
            }
        }
    }

    private bool _isForensicLabVisible;
    public bool IsForensicLabVisible
    {
        get => _isForensicLabVisible;
        set 
        {
             if (SetProperty(ref _isForensicLabVisible, value))
             {
                 UpdateWorkspaceFromState();
             }
        }
    }

    private bool _isInspectorOpen;
    public bool IsInspectorOpen
    {
        get => _isInspectorOpen;
        set 
        {
            if (SetProperty(ref _isInspectorOpen, value))
            {
                UpdateWorkspaceFromState();
                OnPropertyChanged(nameof(IsRightPanelVisible));
            }
        }
    }
    
    public bool IsRightPanelVisible => IsMixHelperVisible || IsInspectorOpen;

    private bool _isDiscoveryLaneVisible;
    public bool IsDiscoveryLaneVisible
    {
        get => _isDiscoveryLaneVisible;
        set { _isDiscoveryLaneVisible = value; OnPropertyChanged(); }
    }

    private Avalonia.Controls.GridLength _discoveryLaneHeight = new(350);
    public Avalonia.Controls.GridLength DiscoveryLaneHeight
    {
        get => _discoveryLaneHeight;
        set { _discoveryLaneHeight = value; OnPropertyChanged(); }
    }

    private bool _isQuickLookVisible;
    public bool IsQuickLookVisible
    {
        get => _isQuickLookVisible;
        set { _isQuickLookVisible = value; OnPropertyChanged(); }
    }

    private bool _isUpdatingState;

    private void UpdateWorkspaceFromState()
    {
        if (_isUpdatingState) return;
        _isUpdatingState = true;

        if (IsForensicLabVisible)
        {
            CurrentWorkspace = ActiveWorkspace.Forensic;
        }
        else if (IsMixHelperVisible && IsInspectorOpen)
        {
            CurrentWorkspace = ActiveWorkspace.Preparer;
        }
        else if (IsInspectorOpen)
        {
            CurrentWorkspace = ActiveWorkspace.Analyst;
        }
        else if (IsDiscoveryLaneVisible)
        {
             CurrentWorkspace = ActiveWorkspace.Preparer;
        }
        else
        {
            CurrentWorkspace = ActiveWorkspace.Selector;
        }

        _isUpdatingState = false;
    }

    private void ApplyWorkspaceState()
    {
        if (_isUpdatingState) return;
        _isUpdatingState = true;

        switch (CurrentWorkspace)
        {
            case ActiveWorkspace.Selector:
                IsMixHelperVisible = false;
                IsInspectorOpen = false;
                IsForensicLabVisible = false;
                IsDiscoveryLaneVisible = false;
                break;
            case ActiveWorkspace.Analyst:
                IsMixHelperVisible = false;
                IsInspectorOpen = true;
                IsForensicLabVisible = false;
                IsDiscoveryLaneVisible = true; // Enabled for Analyst too
                break;
            case ActiveWorkspace.Preparer:
                IsMixHelperVisible = true;
                IsInspectorOpen = true;
                IsForensicLabVisible = false;
                IsDiscoveryLaneVisible = true;
                break;
            case ActiveWorkspace.Forensic:
                IsMixHelperVisible = false;
                IsInspectorOpen = false;
                IsForensicLabVisible = true;
                IsDiscoveryLaneVisible = false;
                
                // [NEW] Load currently selected track into Forensic Lab
                if (Tracks.SelectedTracks.FirstOrDefault() is { } selectedTrack)
                {
                    _ = _forensicLab.LoadTrackAsync(selectedTrack.UniqueHash);
                }
                break;
            case ActiveWorkspace.Industrial:
                IsMixHelperVisible = false;
                IsInspectorOpen = false;
                IsForensicLabVisible = false;
                IsDiscoveryLaneVisible = false;
                break;
        }

        _isUpdatingState = false;
    }
}
