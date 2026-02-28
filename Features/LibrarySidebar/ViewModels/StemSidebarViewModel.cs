using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ReactiveUI;
using SLSKDONET.Models.Stem;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using SLSKDONET.ViewModels;
using SLSKDONET.Views;

namespace SLSKDONET.Features.LibrarySidebar.ViewModels;

public enum StemSidebarState
{
    Unprocessed,
    Processing,
    Ready
}

/// <summary>
/// Orchestrator for the STEMS Manipulation Sandbox.
/// Manages separation workflow and 4-channel mixing logic.
/// </summary>
public class StemSidebarViewModel : ReactiveObject, ISidebarContent, IDisposable
{
    private readonly StemSeparationService _separationService;
    private readonly RealTimeStemEngine _stemEngine;
    private readonly IAudioPlayerService _playerService;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;
    private readonly CompositeDisposable _disposables = new();

    private PlaylistTrackViewModel? _currentTrack;

    private StemSidebarState _state = StemSidebarState.Unprocessed;
    private bool _anySoloActive;
    private double _processingProgress;

    public ObservableCollection<StemChannelViewModel> Stems { get; } = new();

    public StemSidebarState State
    {
        get => _state;
        private set => this.RaiseAndSetIfChanged(ref _state, value);
    }

    public bool AnySoloActive
    {
        get => _anySoloActive;
        private set => this.RaiseAndSetIfChanged(ref _anySoloActive, value);
    }

    public double ProcessingProgress
    {
        get => _processingProgress;
        private set => this.RaiseAndSetIfChanged(ref _processingProgress, value);
    }

    public ICommand GenerateStemsCommand { get; }
    public ICommand ExportStemFilesCommand { get; }

    public StemSidebarViewModel(
        StemSeparationService separationService,
        RealTimeStemEngine stemEngine,
        IAudioPlayerService playerService,
        IDialogService dialogService,
        INotificationService notificationService)
    {
        _separationService = separationService;
        _stemEngine = stemEngine;
        _playerService = playerService;
        _dialogService = dialogService;
        _notificationService = notificationService;

        GenerateStemsCommand = ReactiveCommand.CreateFromTask(
            GenerateStemsAsync,
            this.WhenAnyValue(x => x.State, s => s == StemSidebarState.Unprocessed));

        ExportStemFilesCommand = ReactiveCommand.CreateFromTask(
            ExportStemsAsync,
            this.WhenAnyValue(x => x.State, s => s == StemSidebarState.Ready));

        // Initialize 4 DJ channels as per prompt
        Stems.Add(new StemChannelViewModel(this, StemType.Vocals, "Vocals", "#FF4081"));
        Stems.Add(new StemChannelViewModel(this, StemType.Drums, "Drums", "#FFFF00"));
        Stems.Add(new StemChannelViewModel(this, StemType.Bass, "Bass", "#00B0FF"));
        Stems.Add(new StemChannelViewModel(this, StemType.Other, "Melody", "#9C27B0")); // Mapping Other to Melody

        // Task 1: Logic Matrix - Propagate EffectiveVolume to the Audio Engine
        foreach (var stem in Stems)
        {
            stem.WhenAnyValue(x => x.EffectiveVolume)
                .Subscribe(vol => _stemEngine.SetVolume(stem.Type, (float)vol))
                .DisposeWith(_disposables);
        }
    }

    public async Task ActivateAsync(PlaylistTrackViewModel track)
    {
        _currentTrack = track;
        
        // Task 2: State Machine - Check Cache immediately
        if (_separationService.HasStems(track.GlobalId))
        {
            await LoadStemsAsync(track);
        }
        else
        {
            State = StemSidebarState.Unprocessed;
        }
    }

    public Task ActivateBulkAsync(IReadOnlyList<PlaylistTrackViewModel> tracks) => Task.CompletedTask;

    public void Deactivate()
    {
        _currentTrack = null;
        State = StemSidebarState.Unprocessed;
        _stemEngine.Pause(); // Stop engine when leaving
    }

    public void UpdateSoloStates()
    {
        AnySoloActive = Stems.Any(s => s.IsSoloed);
    }

    private async Task GenerateStemsAsync()
    {
        if (_currentTrack == null) return;

        State = StemSidebarState.Processing;
        ProcessingProgress = 0;

        try
        {
            // Use ResolvedFilePath from the track model
            var trackPath = _currentTrack.Model.ResolvedFilePath;
            
            if (string.IsNullOrEmpty(trackPath) || !File.Exists(trackPath))
            {
                // Fallback search if path is missing but we have GlobalId
                // (In a real DJ app, library service would resolve this)
                State = StemSidebarState.Unprocessed;
                return;
            }

            // Task 2: Trigger StemSeparationService.SeparateAsync
            var result = await _separationService.SeparateTrackAsync(trackPath, _currentTrack.GlobalId);
            
            if (result != null && result.Count >= 4)
            {
                await LoadStemsAsync(_currentTrack);
            }
            else
            {
                State = StemSidebarState.Unprocessed;
            }
        }
        catch (Exception)
        {
            State = StemSidebarState.Unprocessed;
        }
    }

    private async Task LoadStemsAsync(PlaylistTrackViewModel track)
    {
        var stemPaths = _separationService.GetStemPaths(track.GlobalId);
        if (stemPaths.Any())
        {
            // Load into RealTimeStemEngine for playback
            _stemEngine.LoadStems(stemPaths);
            
            // Task 2: Playback Hook - Sync with main player
            if (_playerService.IsPlaying)
            {
                double positionSeconds = _playerService.Time / 1000.0;
                _stemEngine.Seek(positionSeconds);
                _stemEngine.Play();
            }

            State = StemSidebarState.Ready;
        }
    }

    private async Task ExportStemsAsync()
    {
        if (_currentTrack == null) return;

        var stemPaths = _separationService.GetStemPaths(_currentTrack.GlobalId);
        if (!stemPaths.Any())
        {
            _notificationService.Show("Export Failed", "No separated stems found for this track.", SLSKDONET.Views.NotificationType.Error);
            return;
        }

        var destinationFolder = await _dialogService.OpenFolderDialogAsync("Select Destination Folder for Stems");
        if (string.IsNullOrEmpty(destinationFolder)) return;

        try
        {
            string baseFileName = $"{_currentTrack.Artist} - {_currentTrack.Title}";
            // Sanitize filename
            foreach (char c in Path.GetInvalidFileNameChars()) baseFileName = baseFileName.Replace(c, '_');

            int count = 0;
            foreach (var kvp in stemPaths)
            {
                string stemType = kvp.Key.ToString();
                string sourcePath = kvp.Value;
                string extension = Path.GetExtension(sourcePath);
                string destinationPath = Path.Combine(destinationFolder, $"{baseFileName} ({stemType}){extension}");

                File.Copy(sourcePath, destinationPath, true);
                count++;
            }

            _notificationService.Show("Export Successful", $"Exported {count} stem files to {destinationFolder}", SLSKDONET.Views.NotificationType.Success);
        }
        catch (Exception ex)
        {
            _notificationService.Show("Export Failed", ex.Message, SLSKDONET.Views.NotificationType.Error);
        }
    }

    public void Dispose()
    {
        _disposables.Dispose();
        _stemEngine?.Dispose();
    }
}
