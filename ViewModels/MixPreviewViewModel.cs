using System;
using System.Reactive;
using System.Threading.Tasks;
using System.Windows.Input;
using ReactiveUI;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using Avalonia.Threading;

namespace SLSKDONET.ViewModels
{
    public class MixPreviewViewModel : ReactiveObject, IDisposable
    {
        private readonly TransitionPreviewService _previewService;
        private readonly WaveformAnalysisService _waveformService;
        
        private LibraryEntryEntity? _trackA;
        private LibraryEntryEntity? _trackB;
        private WaveformAnalysisData _waveformDataA;
        private WaveformAnalysisData _waveformDataB;
        private double _progressA;
        private double _progressB;
        private float _phaseConfidence;
        private bool _isActive;
        private bool _isPlaying;
        private string _statusMessage = "Ready to Mix";
        private DispatcherTimer _updateTimer;

        public MixPreviewViewModel(
            TransitionPreviewService previewService, 
            WaveformAnalysisService waveformService)
        {
            _previewService = previewService;
            _waveformService = waveformService;

            PlayCommand = ReactiveCommand.Create(Play);
            PauseCommand = ReactiveCommand.Create(Pause);
            StopCommand = ReactiveCommand.Create(Stop);
            CommitCommand = ReactiveCommand.CreateFromTask(CommitAsync);
            CancelCommand = ReactiveCommand.Create(Cancel);
            
            NudgeForwardCommand = ReactiveCommand.Create(() => Nudge(0.05)); // +50ms
            NudgeBackCommand = ReactiveCommand.Create(() => Nudge(-0.05));   // -50ms

            _previewService.PlaybackStateChanged += OnPlaybackStateChanged;

            // Timer for UI updates (Playhead, Phase Meter) - 60 FPS
            _updateTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnTick);
            _updateTimer.Start();
        }

        public LibraryEntryEntity? TrackA
        {
            get => _trackA;
            private set => this.RaiseAndSetIfChanged(ref _trackA, value);
        }

        public LibraryEntryEntity? TrackB
        {
            get => _trackB;
            private set => this.RaiseAndSetIfChanged(ref _trackB, value);
        }

        public WaveformAnalysisData WaveformDataA
        {
            get => _waveformDataA;
            private set => this.RaiseAndSetIfChanged(ref _waveformDataA, value);
        }

        public WaveformAnalysisData WaveformDataB
        {
            get => _waveformDataB;
            private set => this.RaiseAndSetIfChanged(ref _waveformDataB, value);
        }

        public double ProgressA
        {
            get => _progressA;
            set => this.RaiseAndSetIfChanged(ref _progressA, value);
        }

        public double ProgressB
        {
            get => _progressB;
            set => this.RaiseAndSetIfChanged(ref _progressB, value);
        }

        public float PhaseConfidence
        {
            get => _phaseConfidence;
            set 
            {
                this.RaiseAndSetIfChanged(ref _phaseConfidence, value);
                this.RaisePropertyChanged(nameof(PhaseConfidenceWidth));
            }
        }

        public double PhaseConfidenceWidth => PhaseConfidence * 100.0;

        public bool IsActive
        {
            get => _isActive;
            set => this.RaiseAndSetIfChanged(ref _isActive, value);
        }

        public bool IsPlaying
        {
            get => _isPlaying;
            set => this.RaiseAndSetIfChanged(ref _isPlaying, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
        }

        public ReactiveCommand<Unit, Unit> PlayCommand { get; }
        public ReactiveCommand<Unit, Unit> PauseCommand { get; }
        public ReactiveCommand<Unit, Unit> StopCommand { get; }
        public ReactiveCommand<Unit, Unit> CommitCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelCommand { get; }
        
        public ReactiveCommand<Unit, Unit> NudgeForwardCommand { get; }
        public ReactiveCommand<Unit, Unit> NudgeBackCommand { get; }

        public async Task LoadPreviewAsync(LibraryEntryEntity trackA, LibraryEntryEntity trackB)
        {
            TrackA = trackA;
            TrackB = trackB;
            IsActive = true;
            StatusMessage = "Loading Preview...";

            // 1. Prepare Engine (Async)
            await _previewService.PreparePreviewAsync(trackA, trackB);

            // 2. Load Waveforms (Parallel)
            var taskA = LoadWaveformAsync(trackA);
            var taskB = LoadWaveformAsync(trackB);
            
            await Task.WhenAll(taskA, taskB);
            
            StatusMessage = "Preview Ready";
        }

        private async Task LoadWaveformAsync(LibraryEntryEntity track)
        {
            if (string.IsNullOrEmpty(track.FilePath)) return;
            try
            {
                // Use default resolution for now
                var data = await _waveformService.GenerateWaveformAsync(track.FilePath);
                if (track == TrackA) WaveformDataA = data;
                else WaveformDataB = data;
            }
            catch (Exception)
            {
                // Handle error
            }
        }

        private void Play() => _previewService.Play();
        private void Pause() => _previewService.Pause();
        private void Stop() => _previewService.Stop();
        private void Nudge(double amount) => _previewService.Nudge(amount);

        private async Task CommitAsync()
        {
            await _previewService.CommitTransitionAsync();
            IsActive = false;
        }

        private void Cancel()
        {
            _previewService.Stop();
            IsActive = false;
        }

        private void OnPlaybackStateChanged(object? sender, EventArgs e)
        {
            IsPlaying = _previewService.IsPlaying;
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (!IsActive) return;

            // Update Playheads
            // We need to map Engine Position (samples) to 0-1 Progress for each track
            // Engine 0 = StartSampleOffset for TrackA
            
            // This mapping logic needs to match TransitionPreviewService.PreparePreview
            // Ideally Service exposes "CurrentTimeA" and "CurrentTimeB"
            
            // TODO: Refine this logic by exposing exact times from service
            // For now, placeholder:
            
            // Phase Meter Simulation (Using SnappingEngine later)
            // PhaseConfidence = _previewService.GetPhaseDrift(); 
        }

        public void Dispose()
        {
            _updateTimer.Stop();
            _previewService.PlaybackStateChanged -= OnPlaybackStateChanged;
            _previewService.Dispose();
        }
    }
}
