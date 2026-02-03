using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia
{
    public partial class FlowBuilderView : UserControl
    {
        public FlowBuilderView()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
