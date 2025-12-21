using Avalonia.Controls;
using Avalonia.Input;
using System.Linq;
using SLSKDONET.Views;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia
{
    public partial class SearchPage : UserControl
    {
        public SearchPage()
        {
            InitializeComponent();
            
            // Enable Drag & Drop
            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);
        }

        public SearchPage(SearchViewModel viewModel) : this()
        {
            DataContext = viewModel;
        }

        private void OnDragOver(object? sender, DragEventArgs e)
        {
            if (e.Data.Contains(DataFormats.Files))
            {
                // Only allow CSV files
                var files = e.Data.GetFiles();
                if (files != null && files.Any(f => f.Name.EndsWith(".csv", System.StringComparison.OrdinalIgnoreCase)))
                {
                    e.DragEffects = DragDropEffects.Copy;
                    return;
                }
            }
            e.DragEffects = DragDropEffects.None;
        }

        private void OnDrop(object? sender, DragEventArgs e)
        {
            if (e.Data.Contains(DataFormats.Files))
            {
                var files = e.Data.GetFiles();
                var csvFile = files?.FirstOrDefault(f => f.Name.EndsWith(".csv", System.StringComparison.OrdinalIgnoreCase));

                if (csvFile != null && DataContext is SLSKDONET.ViewModels.SearchViewModel vm)
                {
                    // Auto-switch to CSV mode and populate path
                    // vm.CurrentSearchMode = Models.SearchInputMode.CsvFile; // Logic is now inferred from extension in SearchViewModel
                    
                    if (csvFile.Path.IsAbsoluteUri && csvFile.Path.Scheme == "file")
                    {
                        vm.SearchQuery = csvFile.Path.LocalPath;
                    }
                    else
                    {
                        vm.SearchQuery = System.Uri.UnescapeDataString(csvFile.Path.ToString());
                    }
                    
                    // Optional: Trigger browse/preview automatically if desired?
                    // vm.BrowseCsvCommand.Execute(null); 
                }
            }
        }
    }
}
