using System;
using System.ComponentModel;
using SLSKDONET.Models;
using SLSKDONET.Views;
using SLSKDONET.Data;

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
                
                // Auto-dock player to bottom when sidepanel opens
                if (value && _playerViewModel != null)
                {
                    _playerViewModel.CurrentDockLocation = PlayerDockLocation.BottomBar;
                    _playerViewModel.IsPlayerVisible = true; // Ensure player is visible
                }
            }
        }
    }

    private LibraryWorkspace _currentWorkspace = LibraryWorkspace.Selector;
    public LibraryWorkspace CurrentWorkspace
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

    public bool IsRightPanelVisible => IsMixHelperVisible;

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

    private bool _isUpgradeScoutVisible;
    public bool IsUpgradeScoutVisible
    {
        get => _isUpgradeScoutVisible;
        set { _isUpgradeScoutVisible = value; OnPropertyChanged(); }
    }

    private bool _isUpdatingState;

    private void UpdateWorkspaceFromState()
    {
        if (_isUpdatingState) return;
        _isUpdatingState = true;

        if (IsMixHelperVisible)
        {
            CurrentWorkspace = LibraryWorkspace.Preparer;
        }
        else if (IsDiscoveryLaneVisible)
        {
             CurrentWorkspace = LibraryWorkspace.Preparer;
        }
        else
        {
            CurrentWorkspace = LibraryWorkspace.Selector;
        }

        _isUpdatingState = false;
    }

    private void ApplyWorkspaceState()
    {
        if (_isUpdatingState) return;
        _isUpdatingState = true;

        switch (CurrentWorkspace)
        {
            case LibraryWorkspace.Selector:
                IsMixHelperVisible = false;
                IsDiscoveryLaneVisible = false;
                IsForensicLabVisible = false;
                break;
            case LibraryWorkspace.Analyst:
                IsMixHelperVisible = false;
                IsDiscoveryLaneVisible = true; // Enabled for Analyst too
                IsForensicLabVisible = false;
                break;
            case LibraryWorkspace.Preparer:
                IsMixHelperVisible = true;
                IsDiscoveryLaneVisible = true;
                IsForensicLabVisible = false;
                break;
            case LibraryWorkspace.Forensic:
                IsMixHelperVisible = false;
                IsDiscoveryLaneVisible = false;
                
                // [Operation Glass Console] Opening Forensic workspace now summons the global console
                if (Tracks.SelectedTracks.FirstOrDefault() is { } selectedTrack)
                {
                    IsForensicLabVisible = true;
                    _ = _intelligenceCenter.OpenAsync(selectedTrack.GlobalId, IntelligenceViewState.Console);
                }
                else
                {
                    IsForensicLabVisible = false;
                    _notificationService.Show("Forensic Lab", "Select a track to enter forensic mode", NotificationType.Warning);
                }
                break;
            case LibraryWorkspace.Industrial:
                IsMixHelperVisible = false;
                IsDiscoveryLaneVisible = false;
                IsForensicLabVisible = false;
                break;
        }

        _isUpdatingState = false;
    }
}
