using Avalonia.Controls;
using Avalonia;
using SLSKDONET.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace SLSKDONET.Views.Avalonia
{
    public partial class ExportManagerView : UserControl
    {
        public ExportManagerView()
        {
            InitializeComponent();
            
            if (!Design.IsDesignMode)
            {
                // Note: In a real app, we'd use a ViewModel locator or DI
                // For now, assume DataContext is set by the parent or manually resolved
                if (Application.Current is App app)
                {
                    DataContext = app.Services?.GetService<ExportManagerViewModel>();
                }
            }
        }
    }
}
