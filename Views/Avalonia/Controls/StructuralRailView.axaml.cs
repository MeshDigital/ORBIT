using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SLSKDONET.Models;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SLSKDONET.Views.Avalonia.Controls
{
    public partial class StructuralRailView : UserControl
    {
        public static readonly StyledProperty<ObservableCollection<PhraseSegment>> SegmentsProperty =
            AvaloniaProperty.Register<StructuralRailView, ObservableCollection<PhraseSegment>>(nameof(Segments));

        public static readonly StyledProperty<double> TotalDurationProperty =
            AvaloniaProperty.Register<StructuralRailView, double>(nameof(TotalDuration));

        public ObservableCollection<PhraseSegment> Segments
        {
            get => GetValue(SegmentsProperty);
            set => SetValue(SegmentsProperty, value);
        }

        public double TotalDuration
        {
            get => GetValue(TotalDurationProperty);
            set => SetValue(TotalDurationProperty, value);
        }

        public StructuralRailView()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
