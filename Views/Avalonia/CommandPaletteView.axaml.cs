using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using ReactiveUI;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia
{
    public partial class CommandPaletteView : ReactiveUserControl<CommandPaletteViewModel>
    {
        public CommandPaletteView()
        {
            InitializeComponent();
            
            this.GetObservable(IsVisibleProperty).Subscribe(visible =>
            {
                if (visible)
                {
                    var textbox = this.FindControl<TextBox>("SearchTextBox");
                    textbox?.Focus();
                }
            });
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
