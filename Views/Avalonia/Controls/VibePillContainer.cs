using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Platform;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SLSKDONET.ViewModels; // For VibePill record

namespace SLSKDONET.Views.Avalonia.Controls
{
    /// <summary>
    /// A lightweight control that renders VibePills directly to the drawing context
    /// to avoid the heavy overhead of ItemsControl/ItemContainerGenerator in virtualized lists.
    /// </summary>
    public class VibePillContainer : Control
    {
        // Cache typefaces to avoid lookups on every render
        private static readonly Typeface _typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold);
        private static readonly double _fontSize = 10.0;
        
        public static readonly StyledProperty<IEnumerable<VibePill>> ItemsProperty =
            AvaloniaProperty.Register<VibePillContainer, IEnumerable<VibePill>>(nameof(Items));

        public IEnumerable<VibePill> Items
        {
            get => GetValue(ItemsProperty);
            set => SetValue(ItemsProperty, value);
        }

        static VibePillContainer()
        {
            AffectsRender<VibePillContainer>(ItemsProperty);
            AffectsMeasure<VibePillContainer>(ItemsProperty);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var items = Items;
            if (items == null) return new Size(0, 0);

            double width = 0;
            double height = 0;
            double spacing = 4.0;
            
            // Standard pill padding: 8,1 -> height approx 16-18px
            // Text height ~12px + 2px padding top/bottom
            double pillHeight = 18.0; 

            foreach (var item in items)
            {
                var text = $"{item.Icon} {item.Label}";
                var formattedText = new FormattedText(
                    text,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    _typeface,
                    _fontSize,
                    Brushes.White
                );

                width += formattedText.Width + 16; // 8px padding each side
                width += spacing;
            }

            if (width > 0) width -= spacing; // Remove last spacing
            height = pillHeight;

            return new Size(width, height);
        }

        public override void Render(DrawingContext context)
        {
            var items = Items;
            if (items == null) return;

            double x = 0;
            double y = 0;
            double spacing = 4.0;
            double pillHeight = 18.0;
            double cornerRadius = 9.0; // Fully rounded ends

            foreach (var item in items)
            {
                var text = $"{item.Icon} {item.Label}";
                
                IBrush bgBrush = item.Color ?? Brushes.Gray;

                var formattedText = new FormattedText(
                    text,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    _typeface,
                    _fontSize,
                    Brushes.White
                );

                double pillWidth = formattedText.Width + 16;
                var rect = new Rect(x, y, pillWidth, pillHeight);
                
                // Draw Pill Background
                context.DrawRectangle(bgBrush, null, rect, cornerRadius, cornerRadius);

                // Draw Text
                // Center vertically: (18 - textHeight) / 2
                // Center horizontally: 8px padding left
                double textY = y + (pillHeight - formattedText.Height) / 2;
                context.DrawText(formattedText, new Point(x + 8, textY));

                x += pillWidth + spacing;
            }
        }
    }
}
