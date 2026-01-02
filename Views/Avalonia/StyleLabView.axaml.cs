using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using SLSKDONET.ViewModels;
using System.Linq;

namespace SLSKDONET.Views.Avalonia;

public partial class StyleLabView : UserControl
{
    public StyleLabView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not StyleLabViewModel vm) return;
        
        // This is a rough heuristic - assuming custom data format or text
        // In the Library, we usually drag 'Generic' data or file paths.
        // Let's inspect data.
        
        // If we dragged internal items (from LibraryPage), we might not have a clean format standard yet.
        // Assuming we rely on ViewModels passing data or clipboard.
        
        // BUT Phase 2: DragAdornerService usually handles this.
        // Let's assume the drag source puts Text (TrackUniqueHash) or FileNames.
        
        if (e.Data.Contains(DataFormats.Text))
        {
            var text = e.Data.GetText();
            if (string.IsNullOrEmpty(text)) return;
            {
                // If it's a hash (simple check)
                if (text.Length > 20) // Hashes are usually long
                {
                    vm.AddTrackToStyleCommand.Execute(text).Subscribe();
                }
            }
        }
        // TODO: Handle File Drop (if we want to drag from Explorer)
    }
}
