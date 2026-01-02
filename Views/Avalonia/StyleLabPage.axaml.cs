using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using SLSKDONET.ViewModels;
using System;
using System.Linq;

namespace SLSKDONET.Views.Avalonia;

public partial class StyleLabPage : UserControl
{
    public StyleLabPage()
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
        
        if (e.Data.Contains(DataFormats.Text))
        {
            var text = e.Data.GetText();
            if (string.IsNullOrEmpty(text)) return;
            
            // If it's a hash (simple check)
            if (text.Length > 20) // Hashes are usually long
            {
                vm.AddTrackToStyleCommand.Execute(text).Subscribe();
            }
        }
    }
}
