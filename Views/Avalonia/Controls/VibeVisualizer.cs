using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SLSKDONET.Views.Avalonia.Controls
{
    public class VibeVisualizer : Control
    {
        public static readonly StyledProperty<float> VuLeftProperty =
            AvaloniaProperty.Register<VibeVisualizer, float>(nameof(VuLeft), 0f);

        public float VuLeft { get => GetValue(VuLeftProperty); set => SetValue(VuLeftProperty, value); }

        public static readonly StyledProperty<float> VuRightProperty =
            AvaloniaProperty.Register<VibeVisualizer, float>(nameof(VuRight), 0f);

        public float VuRight { get => GetValue(VuRightProperty); set => SetValue(VuRightProperty, value); }

        public static readonly StyledProperty<float[]?> SpectrumDataProperty =
            AvaloniaProperty.Register<VibeVisualizer, float[]?>(nameof(SpectrumData));

        public float[]? SpectrumData { get => GetValue(SpectrumDataProperty); set => SetValue(SpectrumDataProperty, value); }

        public static readonly StyledProperty<double> EnergyProperty =
            AvaloniaProperty.Register<VibeVisualizer, double>(nameof(Energy), 0.5);

        public double Energy { get => GetValue(EnergyProperty); set => SetValue(EnergyProperty, value); }

        public static readonly StyledProperty<string?> MoodTagProperty =
            AvaloniaProperty.Register<VibeVisualizer, string?>(nameof(MoodTag));

        public string? MoodTag { get => GetValue(MoodTagProperty); set => SetValue(MoodTagProperty, value); }

        public static readonly StyledProperty<bool> IsPlayingProperty =
            AvaloniaProperty.Register<VibeVisualizer, bool>(nameof(IsPlaying), false);

        public bool IsPlaying { get => GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }

        private readonly Random _random = new();
        private readonly List<Particle> _particles = new();
        private float[] _smoothedSpectrum = Array.Empty<float>();
        private float _heartbeatValue = 0f;

        static VibeVisualizer()
        {
            AffectsRender<VibeVisualizer>(VuLeftProperty, VuRightProperty, EnergyProperty, IsPlayingProperty, SpectrumDataProperty, MoodTagProperty);
        }

        public VibeVisualizer()
        {
            for (int i = 0; i < 30; i++) _particles.Add(CreateParticle());
        }

        private Particle CreateParticle() => new()
        {
            X = _random.NextDouble(),
            Y = _random.NextDouble(),
            Size = _random.NextDouble() * 3 + 1,
            SpeedX = (_random.NextDouble() - 0.5) * 0.005,
            SpeedY = (_random.NextDouble() - 0.5) * 0.005,
            Opacity = _random.NextDouble() * 0.4 + 0.1
        };

        public override void Render(DrawingContext context)
        {
            if (!IsPlaying) return;

            var w = Bounds.Width;
            var h = Bounds.Height;
            if (w < 10 || h < 10) return;

            var baseColor = GetVibeColor();
            var intensity = (VuLeft + VuRight) / 2.0f;
            
            // 1. CRT Background (Darker base)
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(10, 10, 15)), null, new Rect(0, 0, w, h));

            // 2. Radial Glow (Heartbeat pulse)
            UpdateHeartbeat(intensity);
            var pulseScale = 0.8 + (_heartbeatValue * 0.4);
            var auraBrush = new RadialGradientBrush
            {
                GradientStops = new GradientStops
                {
                    new GradientStop(Color.FromArgb((byte)(160 * intensity), baseColor.R, baseColor.G, baseColor.B), 0.0),
                    new GradientStop(Color.FromArgb((byte)(40 * intensity), baseColor.R, baseColor.G, baseColor.B), 0.7 * pulseScale),
                    new GradientStop(Colors.Transparent, 1.0)
                }
            };
            context.DrawRectangle(auraBrush, null, new Rect(0, 0, w, h));

            // 3. Spectrum Bars (Linear bottom)
            RenderSpectrum(context, w, h, baseColor);

            // 4. Cosmic Bloom (Particles)
            UpdateParticles();
            foreach (var p in _particles)
            {
                double px = p.X * w;
                double py = p.Y * h;
                double pSize = p.Size * (1.0 + intensity * 3.0);
                context.DrawEllipse(new SolidColorBrush(baseColor, (float)p.Opacity), null, new Point(px, py), pSize, pSize);
            }

            // 5. Retro Post-Processing (Scanlines)
            RenderScanlines(context, w, h);

            if (IsPlaying) Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        }

        private void RenderSpectrum(DrawingContext context, double w, double h, Color baseColor)
        {
            var data = SpectrumData;
            if (data == null || data.Length == 0) return;

            if (_smoothedSpectrum.Length != 64) _smoothedSpectrum = new float[64];

            // Map and smooth data to 64 bins
            int samplesPerBin = data.Length / 64;
            for (int i = 0; i < 64; i++)
            {
                float sum = 0;
                for (int j = 0; j < samplesPerBin; j++) sum += data[i * samplesPerBin + j];
                float val = (sum / samplesPerBin) * 20.0f; // Scale up for visibility
                _smoothedSpectrum[i] = _smoothedSpectrum[i] * 0.6f + val * 0.4f; // Temporal smoothing
            }

            double barW = w / 64.0;
            var barBrush = new SolidColorBrush(baseColor, 0.6f);
            var glowBrush = new SolidColorBrush(Colors.White, 0.3f);

            for (int i = 0; i < 64; i++)
            {
                double barH = Math.Min(h * 0.5, _smoothedSpectrum[i] * h * 0.4);
                if (barH < 1) continue;

                var rect = new Rect(i * barW, h - barH, barW - 1, barH);
                context.DrawRectangle(barBrush, null, rect);
                
                // Top cap glow
                context.DrawRectangle(glowBrush, null, new Rect(i * barW, h - barH, barW - 1, 2));
            }
        }

        private void RenderScanlines(DrawingContext context, double w, double h)
        {
            var scanlinePen = new Pen(new SolidColorBrush(Colors.Black, 0.15f), 1);
            for (double y = 0; y < h; y += 4)
            {
                context.DrawLine(scanlinePen, new Point(0, y), new Point(w, y));
            }

            // Vignette
            var vignetteBrush = new RadialGradientBrush
            {
                GradientStops = new GradientStops
                {
                    new GradientStop(Colors.Transparent, 0.6),
                    new GradientStop(Color.FromArgb(120, 0, 0, 0), 1.0)
                }
            };
            context.DrawRectangle(vignetteBrush, null, new Rect(0, 0, w, h));
        }

        private void UpdateHeartbeat(float intensity)
        {
            // Specifically look at low bins if spectrum is available
            if (SpectrumData != null && SpectrumData.Length > 10)
            {
                float bass = 0;
                for (int i = 0; i < 5; i++) bass += SpectrumData[i];
                bass = (bass / 5.0f) * 15.0f;
                _heartbeatValue = _heartbeatValue * 0.7f + Math.Clamp(bass, 0, 1) * 0.3f;
            }
            else
            {
                _heartbeatValue = _heartbeatValue * 0.8f + intensity * 0.2f;
            }
        }

        private void UpdateParticles()
        {
            foreach (var p in _particles)
            {
                p.X += p.SpeedX; p.Y += p.SpeedY;
                if (p.X < 0) p.X = 1; if (p.X > 1) p.X = 0;
                if (p.Y < 0) p.Y = 1; if (p.Y > 1) p.Y = 0;
            }
        }

        private Color GetVibeColor()
        {
            if (Energy < 0.35) return Color.FromRgb(65, 105, 225); // RoyalBlue
            if (Energy < 0.65) return Color.FromRgb(50, 205, 50);  // LimeGreen
            return Color.FromRgb(255, 45, 0);                      // Neon Red
        }

        private class Particle { public double X, Y, Size, SpeedX, SpeedY, Opacity; }
    }
}
