using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SLSKDONET.Views
{
    public class DragAdorner : Adorner
    {
        private System.Windows.Media.Brush _visualBrush;
        private System.Windows.Point _centerOffset;
        private double _leftOffset;
        private double _topOffset;

        public DragAdorner(UIElement adornedElement, UIElement dragVisual, double opacity)
            : base(adornedElement)
        {
            _visualBrush = new VisualBrush(dragVisual) { Opacity = opacity, Stretch = Stretch.None };
            _centerOffset = new System.Windows.Point(dragVisual.RenderSize.Width / 2, dragVisual.RenderSize.Height / 2);
            
            // Allow hit test to pass through
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            Rect rect = new Rect(new System.Windows.Point(_leftOffset, _topOffset), new System.Windows.Size(RenderSize.Width, RenderSize.Height));
            drawingContext.DrawRectangle(_visualBrush, null, rect);
        }

        public double LeftOffset
        {
            get { return _leftOffset; }
            set
            {
                _leftOffset = value;
                UpdatePosition();
            }
        }

        public double TopOffset
        {
            get { return _topOffset; }
            set
            {
                _topOffset = value;
                UpdatePosition();
            }
        }

        private void UpdatePosition()
        {
            if (Parent is AdornerLayer layer)
            {
                layer.Update(AdornedElement);
            }
        }
    }
}
