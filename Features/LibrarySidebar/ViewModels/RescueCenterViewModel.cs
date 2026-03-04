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

    public RescueCenterViewModel(ILibraryService libraryService, IEventBus eventBus)
    {
        _libraryService = libraryService;
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
        StatusText = "Scanning library for missing files...";
        _missingTracksSource.Clear();

        try
        {
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
        StatusText = $"Attempting to relink: {trackVm.Title}";
        
        // Phase 6 Note: In a real implementation this would scan commonly configured 'LibraryFolders'
        // For now, let's pretend we look in the top level of the user's Music directory
        try
        {
            await Task.Run(async () => 
            {
                var musicDir = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                var orbitDir = Path.Combine(musicDir, "ORBIT");
                
                if (Directory.Exists(orbitDir))
                {
                    var files = Directory.GetFiles(orbitDir, "*.mp3", SearchOption.AllDirectories);
                    
                    foreach (var file in files)
                    {
                        var filename = Path.GetFileNameWithoutExtension(file);
                        
                        // Extremely naive "Smart Match": If the filename contains both artist and title
                        // Note: To truly match, we should read tags or use AcoustID 
                        if (filename.Contains(trackVm.Artist, StringComparison.OrdinalIgnoreCase) && 
                            filename.Contains(trackVm.Title, StringComparison.OrdinalIgnoreCase))
                        {
                            // Update database record immediately
                            trackVm.Model.ResolvedFilePath = file;
                            trackVm.Model.Status = SLSKDONET.Models.TrackStatus.Downloaded;
                            
                            // Remove from missing list
                            Dispatcher.UIThread.Post(() => {
                                _missingTracksSource.Remove(trackVm);
                                StatusText = $"Relinked {trackVm.Title} successfully!";
                            });
                            
                            // Emit global event to update UI
                            _eventBus.Publish(new TrackUpdatedEvent(trackVm));
                            return;
                        }
                    }
                }
                
                Dispatcher.UIThread.Post(() => {
                    StatusText = $"Relink failed: Could not find '{trackVm.Title}'";
                });
            });
        }
        catch (Exception)
        {
            StatusText = "Relink encountered an error";
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
