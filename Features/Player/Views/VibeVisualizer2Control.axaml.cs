using System;
using System.Reactive.Disposables;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SLSKDONET.Features.Player.Rendering;
using SLSKDONET.Services;
using ReactiveUI;
using System.Reactive.Linq;

namespace SLSKDONET.Features.Player.Views
{
    public class VibeVisualizer2Control : SLSKDONET.Views.Avalonia.Controls.BaseSkiaWaveform
    {
        protected override void StartRenderingLoop() { }
        protected override void StopRenderingLoop() { }
        private readonly CompositeDisposable _disposables = new();
        private FftFrame? _latestFrame;
        private VibeProfile _profile = VibeProfile.GetProfile(0.5f, null);
        
        // Pre-allocated buffers for zero-allocation render path
        private readonly float[] _smoothingBuffer = new float[64];
        private readonly float[] _peakBuffer = new float[64];
        private readonly DateTime[] _peakHoldTime = new DateTime[64];

        public static readonly StyledProperty<float> EnergyProperty =
            AvaloniaProperty.Register<VibeVisualizer2Control, float>(nameof(Energy), 0.5f);

        public float Energy
        {
            get => GetValue(EnergyProperty);
            set => SetValue(EnergyProperty, value);
        }

        public static readonly StyledProperty<string?> MoodTagProperty =
            AvaloniaProperty.Register<VibeVisualizer2Control, string?>(nameof(MoodTag));

        public string? MoodTag
        {
            get => GetValue(MoodTagProperty);
            set => SetValue(MoodTagProperty, value);
        }

        public static readonly StyledProperty<bool> IsActiveProperty =
            AvaloniaProperty.Register<VibeVisualizer2Control, bool>(nameof(IsActive), true);

        public bool IsActive
        {
            get => GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        public VibeVisualizer2Control()
        {
            this.WhenAnyValue(x => x.Energy, x => x.MoodTag)
                .Subscribe(tuple => _profile = VibeProfile.GetProfile(tuple.Item1, tuple.Item2))
                .DisposeWith(_disposables);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            
            if (global::Avalonia.Application.Current is SLSKDONET.App app && app.Services != null)
            {
                var audioService = app.Services.GetService(typeof(IAudioPlayerService)) as IAudioPlayerService;
                if (audioService != null)
                {
                    audioService.FftStream
                        .ObserveOn(RxApp.MainThreadScheduler)
                        .Do(_ => audioService.IsVisualizerActive = IsActive && audioService.IsPlaying)
                        .Subscribe(frame => 
                        {
                            if (IsActive && audioService.IsPlaying)
                            {
                                System.Threading.Interlocked.Exchange(ref _latestFrame, frame);
                                InvalidateVisual();
                            }
                        })
                        .DisposeWith(_disposables);
                }
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _disposables.Clear();
        }

        public override void Render(DrawingContext context)
        {
            if (!IsActive || _latestFrame == null) return;
            
            context.Custom(new VibeVisualizer2DrawOp(
                new Rect(0, 0, Bounds.Width, Bounds.Height),
                _latestFrame,
                _profile,
                _smoothingBuffer,
                _peakBuffer,
                _peakHoldTime));
        }
    }
}
