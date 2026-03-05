using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using DynamicData;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Features.LibrarySidebar.ViewModels;

public class RescueCenterViewModel : ReactiveObject, ISidebarContent, IDisposable
{
    private readonly ILibraryService _libraryService;
    private readonly ILibraryFolderScannerService _scannerService;
    private readonly IEventBus _eventBus;
    private readonly CompositeDisposable _disposables = new();

    private readonly SourceList<PlaylistTrackViewModel> _missingTracksSource = new();
    private readonly ReadOnlyObservableCollection<PlaylistTrackViewModel> _missingTracks;
    public ReadOnlyObservableCollection<PlaylistTrackViewModel> MissingTracks => _missingTracks;

    public string Title => "Rescue Center";
    public string Icon => "HeartPulse";

    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        set => this.RaiseAndSetIfChanged(ref _isScanning, value);
    }

    private string _statusText = "Ready";
    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public System.Windows.Input.ICommand ScanLibraryCommand { get; }
    public System.Windows.Input.ICommand SmartRelinkCommand { get; }
    public System.Windows.Input.ICommand RescueViaSoulseekCommand { get; }

    public RescueCenterViewModel(
        ILibraryService libraryService, 
        ILibraryFolderScannerService scannerService,
        IEventBus eventBus)
    {
        _libraryService = libraryService;
        _scannerService = scannerService;
        _eventBus = eventBus;

        _missingTracksSource.Connect()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _missingTracks)
            .Subscribe()
            .DisposeWith(_disposables);

        ScanLibraryCommand = ReactiveCommand.CreateFromTask(ScanForMissingTracksAsync);
        
        SmartRelinkCommand = ReactiveCommand.CreateFromTask<PlaylistTrackViewModel>(async track => 
        {
            await AttemptSmartRelinkAsync(track);
        });

        RescueViaSoulseekCommand = ReactiveCommand.Create<PlaylistTrackViewModel>(track =>
        {
            // Trigger auto-search in the main Soulseek tab
            _eventBus.Publish(new SLSKDONET.Events.ManualSearchRequestEvent(new SLSKDONET.Models.PlaylistTrack { Artist = track.Artist, Title = track.Title }));
        });

        // Auto-run scan on initialization
        Dispatcher.UIThread.InvokeAsync(ScanForMissingTracksAsync);
    }

    private async Task ScanForMissingTracksAsync()
    {
        if (IsScanning) return;
        
        IsScanning = true;
        StatusText = "Scanning library and building shadow index...";
        _missingTracksSource.Clear();

        try
        {
            // Phase 6: Initialize Shadow Index from commonly checked folders
            var musicDir = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            var orbitDir = Path.Combine(musicDir, "ORBIT");
            var folders = new List<string> { musicDir };
            if (Directory.Exists(orbitDir)) folders.Add(orbitDir);
            
            await _scannerService.InitializeIndexAsync(folders);

            var allTracks = await _libraryService.GetAllPlaylistTracksAsync();
            var missing = new List<PlaylistTrackViewModel>();

            await Task.Run(() =>
            {
                foreach (var track in allTracks)
                {
                    if (!File.Exists(track.ResolvedFilePath))
                    {
                        var vm = new PlaylistTrackViewModel(track, _eventBus, _libraryService);
                        // Note: FileExists doesn't have a public setter, so this needs to be re-evaluated
                        // For now, since we know it doesn't exist, the ViewModel will derive this naturally
                        missing.Add(vm);
                    }
                }
            });

            _missingTracksSource.AddRange(missing);
            StatusText = missing.Count > 0 ? $"Found {missing.Count} missing tracks" : "Library is healthy";
        }
        catch (Exception)
        {
            StatusText = "Error during scan";
            // log error
        }
        finally
        {
            IsScanning = false;
        }
    }

    private async Task AttemptSmartRelinkAsync(PlaylistTrackViewModel trackVm)
    {
        StatusText = $"Searching Shadow Index for: {trackVm.Title}";
        
        try
        {
            // Use Shadow Index for O(1) Relink
            string? foundPath = await _scannerService.AttemptSmartRelinkAsync(
                Path.GetFileName(trackVm.Model.ResolvedFilePath), 
                0);

            if (foundPath != null && File.Exists(foundPath))
            {
                double confidence = _scannerService.CalculateConfidence(
                    foundPath, 
                    Path.GetFileName(trackVm.Model.ResolvedFilePath), 
                    0);

                if (confidence >= 0.9)
                {
                    // Level 1/2 Match: Auto-Apply
                    trackVm.Model.ResolvedFilePath = foundPath;
                    trackVm.Model.Status = SLSKDONET.Models.TrackStatus.Downloaded;
                    
                    Dispatcher.UIThread.Post(() => {
                        _missingTracksSource.Remove(trackVm);
                        StatusText = $"Relinked {trackVm.Title} (Confidence: {confidence:P0})";
                    });
                    
                    _eventBus.Publish(new TrackUpdatedEvent(trackVm));
                }
                else
                {
                    Dispatcher.UIThread.Post(() => {
                        StatusText = $"Candidate found for {trackVm.Title} but low confidence ({confidence:P0})";
                    });
                }
            }
            else
            {
                Dispatcher.UIThread.Post(() => {
                    StatusText = $"Relink failed: {trackVm.Title} not found in checked folders";
                });
            }
        }
        catch (Exception ex)
        {
            StatusText = "Relink encountered an error";
            Serilog.Log.Error(ex, "Relink failed for {Track}", trackVm.Title);
        }
    }

    public Task ActivateAsync(PlaylistTrackViewModel track)
    {
        return Task.CompletedTask;
    }

    public Task ActivateBulkAsync(IReadOnlyList<PlaylistTrackViewModel> tracks)
    {
        return Task.CompletedTask;
    }

    public void Deactivate()
    {
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }
}
