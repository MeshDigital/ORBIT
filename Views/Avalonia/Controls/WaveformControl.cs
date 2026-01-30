using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System;
using SLSKDONET.Models;

namespace SLSKDONET.Views.Avalonia.Controls
{
    public class WaveformControl : Control
    {
        public static readonly StyledProperty<WaveformAnalysisData> WaveformDataProperty =
            AvaloniaProperty.Register<WaveformControl, WaveformAnalysisData>(nameof(WaveformData));

        public WaveformAnalysisData WaveformData
        {
            get => GetValue(WaveformDataProperty);
            set => SetValue(WaveformDataProperty, value);
        }

        public static readonly StyledProperty<float> ProgressProperty =
            AvaloniaProperty.Register<WaveformControl, float>(nameof(Progress), 0f);

        public float Progress
        {
            get => GetValue(ProgressProperty);
            set => SetValue(ProgressProperty, value);
        }

        public static readonly StyledProperty<bool> IsRollingProperty =
            AvaloniaProperty.Register<WaveformControl, bool>(nameof(IsRolling), false);

        public bool IsRolling
        {
            get => GetValue(IsRollingProperty);
            set => SetValue(IsRollingProperty, value);
        }

        public static readonly StyledProperty<IBrush?> PlayheadBrushProperty =
            AvaloniaProperty.Register<WaveformControl, IBrush?>(nameof(PlayheadBrush), Brushes.White);

        public IBrush? PlayheadBrush
        {
            get => GetValue(PlayheadBrushProperty);
            set => SetValue(PlayheadBrushProperty, value);
        }

        public static readonly StyledProperty<System.Windows.Input.ICommand?> SeekCommandProperty =
            AvaloniaProperty.Register<WaveformControl, System.Windows.Input.ICommand?>(nameof(SeekCommand));

        public System.Windows.Input.ICommand? SeekCommand
        {
            get => GetValue(SeekCommandProperty);
            set => SetValue(SeekCommandProperty, value);
        }

        // Band Properties (Low, Mid, High) for RGB rendering
        public static readonly StyledProperty<byte[]?> LowBandProperty = AvaloniaProperty.Register<WaveformControl, byte[]?>(nameof(LowBand));
        public byte[]? LowBand { get => GetValue(LowBandProperty); set => SetValue(LowBandProperty, value); }
        public static readonly StyledProperty<byte[]?> MidBandProperty = AvaloniaProperty.Register<WaveformControl, byte[]?>(nameof(MidBand));
        public byte[]? MidBand { get => GetValue(MidBandProperty); set => SetValue(MidBandProperty, value); }
        public static readonly StyledProperty<byte[]?> HighBandProperty = AvaloniaProperty.Register<WaveformControl, byte[]?>(nameof(HighBand));
        public byte[]? HighBand { get => GetValue(HighBandProperty); set => SetValue(HighBandProperty, value); }

        public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<WaveformControl, IBrush?>(nameof(Foreground));
        public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
        public static readonly StyledProperty<IBrush?> BackgroundProperty = AvaloniaProperty.Register<WaveformControl, IBrush?>(nameof(Background));
        public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

        public static readonly StyledProperty<System.Collections.Generic.IEnumerable<OrbitCue>?> CuesProperty =
            AvaloniaProperty.Register<WaveformControl, System.Collections.Generic.IEnumerable<OrbitCue>?>(nameof(Cues));

        public System.Collections.Generic.IEnumerable<OrbitCue>? Cues
        {
            get => GetValue(CuesProperty);
            set => SetValue(CuesProperty, value);
        }

        static WaveformControl()
        {
            AffectsRender<WaveformControl>(WaveformDataProperty, ProgressProperty, IsRollingProperty, LowBandProperty, MidBandProperty, HighBandProperty, CuesProperty, ForegroundProperty, BackgroundProperty, PlayheadBrushProperty);
        }

        public static readonly StyledProperty<System.Windows.Input.ICommand?> CueUpdatedCommandProperty =
            AvaloniaProperty.Register<WaveformControl, System.Windows.Input.ICommand?>(nameof(CueUpdatedCommand));

        public System.Windows.Input.ICommand? CueUpdatedCommand
        {
            get => GetValue(CueUpdatedCommandProperty);
            set => SetValue(CueUpdatedCommandProperty, value);
        }

        private OrbitCue? _draggedCue;
        private bool _isDraggingCue;
        private bool _isDraggingProgress;
        private const double CueHitThreshold = 10.0;

        protected override void OnPointerPressed(global::Avalonia.Input.PointerPressedEventArgs e)
        {
            var point = e.GetPosition(this);
            var data = WaveformData;
            var cues = Cues;

            // 1. Hit Test for Cues
            if (cues != null && data != null && data.DurationSeconds > 0)
            {
                foreach (var cue in cues)
                {
                    double x = GetCueX(cue, data);
                    if (Math.Abs(point.X - x) <= CueHitThreshold)
                    {
                        _draggedCue = cue;
                        _isDraggingCue = true;
                        e.Pointer.Capture(this);
                        e.Handled = true;
                        return;
                    }
                }
            }

            // 2. Progress Dragging / Click-Seek
            _isDraggingProgress = true;
            e.Pointer.Capture(this);
            UpdateProgressFromPoint(point);
            e.Handled = true;
        }

        protected override void OnPointerMoved(global::Avalonia.Input.PointerEventArgs e)
        {
            var point = e.GetPosition(this);
            var data = WaveformData;

            if (_isDraggingCue && _draggedCue != null && data != null && data.DurationSeconds > 0)
            {
                double x = Math.Clamp(point.X, 0, Bounds.Width);
                if (IsRolling)
                {
                     // In rolling mode, X is relative to playhead at center
                     double center = Bounds.Width / 2;
                     double progressAtCenter = Progress;
                     double dur = data.DurationSeconds;
                     double pixelsPerSec = Bounds.Width / 10.0; // Assume 10sec visible window for rolling
                     double offsetSec = (point.X - center) / pixelsPerSec;
                     _draggedCue.Timestamp = Math.Clamp(progressAtCenter * dur + offsetSec, 0, dur);
                }
                else
                {
                    _draggedCue.Timestamp = (x / Bounds.Width) * data.DurationSeconds;
                }
                InvalidateVisual();
            }
            else if (_isDraggingProgress)
            {
                UpdateProgressFromPoint(point);
            }
        }

        private void UpdateProgressFromPoint(Point point)
        {
             if (IsRolling)
             {
                  // In Rolling mode, clicking left/right of center seeks relative to playhead?
                  // Or we just treat the whole strip as 0-1 range still? 
                  // Usually, clicking a rolling waveform seeks to that spot.
                  // For simplicity, let's keep the click 0-1 range for the static view, 
                  // and maybe a relative seek for rolling.
             }
             
             var progress = (float)(point.X / Bounds.Width);
             progress = Math.Clamp(progress, 0f, 1f);
             
             if (SeekCommand != null && SeekCommand.CanExecute(progress))
             {
                 SeekCommand.Execute(progress);
             }
             Progress = progress; // Immediate UI feedback
        }

        protected override void OnPointerReleased(global::Avalonia.Input.PointerReleasedEventArgs e)
        {
            if (_isDraggingCue)
            {
                _isDraggingCue = false;
                if (CueUpdatedCommand != null && CueUpdatedCommand.CanExecute(_draggedCue))
                    CueUpdatedCommand.Execute(_draggedCue);
                _draggedCue = null;
            }
            _isDraggingProgress = false;
            e.Pointer.Capture(null);
        }

        private double GetCueX(OrbitCue cue, WaveformAnalysisData data)
        {
            if (IsRolling)
            {
                double center = Bounds.Width / 2;
                double pixelsPerSec = Bounds.Width / 10.0; // 10s window
                return center + (cue.Timestamp - (Progress * data.DurationSeconds)) * pixelsPerSec;
            }
            return (cue.Timestamp / data.DurationSeconds) * Bounds.Width;
        }

        public override void Render(DrawingContext context)
        {
            var data = WaveformData;
            if (data == null || data.IsEmpty || data.PeakData == null)
            {
                context.DrawLine(new Pen(Brushes.Gray, 1), new Point(0, Bounds.Height / 2), new Point(Bounds.Width, Bounds.Height / 2));
                return;
            }

            var width = Bounds.Width;
            var height = Bounds.Height;
            var mid = height / 2;
            int samples = data.PeakData.Length;

            if (IsRolling)
            {
                RenderRolling(context, data, width, height, mid);
            }
            else
            {
                RenderStatic(context, data, width, height, mid);
            }

            // Draw Playhead Line
            double playheadX = IsRolling ? width / 2 : Progress * width;
            context.DrawLine(new Pen(PlayheadBrush ?? Brushes.White, 2), new Point(playheadX, 0), new Point(playheadX, height));

            RenderCues(context, width, height);
        }

        private static readonly Pen StaticBasePen = new Pen(Brushes.DimGray, 1);
        private static readonly Pen StaticPlayedPen = new Pen(Brushes.DeepSkyBlue, 1);
        
        // Cache for RGB pens to avoid allocations
        private static readonly Pen LowBasePen = new Pen(new SolidColorBrush(Color.FromRgb(100, 0, 0), 0.35f * 0.8f), 1);
        private static readonly Pen LowPlayedPen = new Pen(new SolidColorBrush(Colors.Red, 0.8f), 1);
        private static readonly Pen MidBasePen = new Pen(new SolidColorBrush(Color.FromRgb(0, 100, 0), 0.35f * 0.7f), 1);
        private static readonly Pen MidPlayedPen = new Pen(new SolidColorBrush(Colors.Lime, 0.7f), 1);
        private static readonly Pen HighBasePen = new Pen(new SolidColorBrush(Color.FromRgb(0, 80, 100), 0.35f), 1);
        private static readonly Pen HighPlayedPen = new Pen(new SolidColorBrush(Colors.DeepSkyBlue, 1.0f), 1);

        private void RenderStatic(DrawingContext context, WaveformAnalysisData data, double width, double height, double mid)
        {
            var samples = data.PeakData!.Length;
            double step = width / samples;
            var lowData = LowBand ?? data.LowData;
            var midData = MidBand ?? data.MidData;
            var highData = HighBand ?? data.HighData;
            bool hasRgb = lowData != null && midData != null && highData != null && lowData.Length > 0;

            if (hasRgb)
            {
                RenderTrueRgb(context, data, width, mid, samples, step, lowData!, midData!, highData!, false);
            }
            else
            {
                RenderSingleBand(context, data, width, mid, samples, step);
            }
        }

        private void RenderSingleBand(DrawingContext context, WaveformAnalysisData data, double width, double mid, int samples, double step)
        {
            var playedLimit = (int)(Progress * samples);

            // Draw unplayed part first
            var baseGeom = new StreamGeometry();
            using (var ctx = baseGeom.Open())
            {
                for (int i = playedLimit; i < samples; i++)
                {
                    double x = i * step;
                    double h = (data.PeakData![i] / 255.0) * mid;
                    if (h < 0.5) continue;
                    ctx.BeginFigure(new Point(x, mid - h), false);
                    ctx.LineTo(new Point(x, mid + h));
                }
            }
            context.DrawGeometry(null, StaticBasePen, baseGeom);

            // Draw played part
            var playedGeom = new StreamGeometry();
            using (var ctx = playedGeom.Open())
            {
                for (int i = 0; i < playedLimit; i++)
                {
                    double x = i * step;
                    double h = (data.PeakData![i] / 255.0) * mid;
                    if (h < 0.5) continue;
                    ctx.BeginFigure(new Point(x, mid - h), false);
                    ctx.LineTo(new Point(x, mid + h));
                }
            }
            context.DrawGeometry(null, StaticPlayedPen, playedGeom);
        }

        private void RenderTrueRgb(DrawingContext context, WaveformAnalysisData data, double width, double mid, int samples, double step, byte[] low, byte[] midB, byte[] high, bool isRolling, double currentXOffset = 0)
        {
            var playedLimit = (int)(Progress * samples);
            var peak = data.PeakData!;
            
            // Professional Neon Palette
            var lowColor = Color.FromRgb(255, 40, 100);    // Hot Pink / Red
            var midColor = Color.FromRgb(0, 255, 120);    // Neon Green
            var highColor = Color.FromRgb(0, 200, 255);   // Cyan / Blue

            for (int i = 0; i < Math.Min(samples, low.Length); i++)
            {
                if (i >= peak.Length) break;
                
                double h = (peak[i] / 255.0) * mid;
                if (h < 0.5) continue;

                double x = (i * step) + currentXOffset;
                if (x < -step || x > width + step) continue;

                // Intensity-based blending
                double l = low[i] / 255.0;
                double m = midB[i] / 255.0;
                double hf = high[i] / 255.0;
                double total = l + m + hf;

                if (total > 0)
                {
                    byte r = (byte)Math.Clamp((l * lowColor.R + m * midColor.R + hf * highColor.R) / total, 0, 255);
                    byte g = (byte)Math.Clamp((l * lowColor.G + m * midColor.G + hf * highColor.G) / total, 0, 255);
                    byte b = (byte)Math.Clamp((l * lowColor.B + m * midColor.B + hf * highColor.B) / total, 0, 255);
                    
                    bool isPlayed = isRolling ? (i <= playedLimit) : (x < Progress * width);
                    float opacity = isPlayed ? 1.0f : 0.35f;
                    
                    var col = Color.FromArgb((byte)(opacity * 255), r, g, b);
                    context.DrawLine(new Pen(new SolidColorBrush(col), 1), new Point(x, mid - h), new Point(x, mid + h));
                }
            }
        }

        private void DrawBandBatch(DrawingContext context, byte[] data, int samples, double step, double mid, int playedLimit, Pen basePen, Pen playedPen)
        {
            var baseGeom = new StreamGeometry();
            using (var ctx = baseGeom.Open())
            {
                for (int i = playedLimit; i < Math.Min(samples, data.Length); i++)
                {
                    double h = (data[i] / 255.0) * mid;
                    if (h < 0.5) continue;
                    double x = i * step;
                    ctx.BeginFigure(new Point(x, mid - h), false);
                    ctx.LineTo(new Point(x, mid + h));
                }
            }
            context.DrawGeometry(null, basePen, baseGeom);

            var playedGeom = new StreamGeometry();
            using (var ctx = playedGeom.Open())
            {
                for (int i = 0; i < Math.Min(playedLimit, data.Length); i++)
                {
                    double h = (data[i] / 255.0) * mid;
                    if (h < 0.5) continue;
                    double x = i * step;
                    ctx.BeginFigure(new Point(x, mid - h), false);
                    ctx.LineTo(new Point(x, mid + h));
                }
            }
            context.DrawGeometry(null, playedPen, playedGeom);
        }

        private void RenderRolling(DrawingContext context, WaveformAnalysisData data, double width, double height, double mid)
        {
            double windowSec = 10.0;
            double pixelsPerSec = width / windowSec;
            double currentSec = Progress * data.DurationSeconds;
            double startSec = currentSec - (windowSec / 2);
            
            int samplesPerSec = (int)(data.PeakData!.Length / data.DurationSeconds);
            int startIdx = (int)(startSec * samplesPerSec);
            double startX = (width / 2) + ( (startSec - currentSec) * pixelsPerSec );

            var lowData = LowBand ?? data.LowData;
            var midData = MidBand ?? data.MidData;
            var highData = HighBand ?? data.HighData;
            bool hasRgb = lowData != null && midData != null && highData != null && lowData.Length > 0;

            if (hasRgb)
            {
                // step = pixels per sample. 
                // pixelsPerSec = width / 10.0
                // samplesPerSec = total_samples / duration
                // step = pixelsPerSec / samplesPerSec
                double step = pixelsPerSec / samplesPerSec;
                RenderTrueRgb(context, data, width, mid, data.PeakData.Length, step, lowData!, midData!, highData!, true, (width / 2) - (currentSec * pixelsPerSec));
            }
            else
            {
                // Fallback to static blue if no RGB
                int endIdx = startIdx + (int)(windowSec * samplesPerSec);
                int playedLimit = (int)(Progress * data.PeakData.Length);
                var playedGeom = new StreamGeometry();
                var baseGeom = new StreamGeometry();
                using (var pCtx = playedGeom.Open())
                using (var bCtx = baseGeom.Open())
                {
                    for (int i = startIdx; i <= endIdx; i++)
                    {
                        if (i < 0 || i >= data.PeakData.Length) continue;
                        double sampleSec = (double)i / samplesPerSec;
                        double x = (width / 2) + (sampleSec - currentSec) * pixelsPerSec;
                        double h = (data.PeakData[i] / 255.0) * mid;
                        var ctx = i <= playedLimit ? pCtx : bCtx;
                        ctx.BeginFigure(new Point(x, mid - h), false);
                        ctx.LineTo(new Point(x, mid + h));
                    }
                }
                context.DrawGeometry(null, StaticPlayedPen, playedGeom);
                context.DrawGeometry(null, StaticBasePen, baseGeom);
            }
        }

        private void RenderCues(DrawingContext context, double width, double height)
        {
            var cues = Cues;
            var data = WaveformData;
            if (cues == null || data == null || data.DurationSeconds <= 0) return;

            foreach (var cue in cues)
            {
                double x = GetCueX(cue, data);
                if (x < 0 || x > width) continue;

                var color = Color.Parse(cue.Color ?? "#FFFFFF");
                context.DrawLine(new Pen(new SolidColorBrush(color, 0.8), 2), new Point(x, 0), new Point(x, height));
                
                // FormattedText is still a bit expensive but only for cues
                var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
                var formattedText = new FormattedText(cue.Name ?? cue.Role.ToString(), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10, new SolidColorBrush(color));
                context.DrawRectangle(new SolidColorBrush(Colors.Black, 0.6), null, new Rect(x + 4, 2, formattedText.Width + 4, formattedText.Height));
                context.DrawText(formattedText, new Point(x + 6, 2));
            }
        }
    }
}
