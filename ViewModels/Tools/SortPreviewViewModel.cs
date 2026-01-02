using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Reactive;
using ReactiveUI;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels.Tools
{
    public class SortPreviewViewModel : ReactiveObject
    {
        private bool _isProcessing;
        public bool IsProcessing
        {
            get => _isProcessing;
            set => this.RaiseAndSetIfChanged(ref _isProcessing, value);
        }

        private string _statusText = "Ready to organize.";
        public string StatusText
        {
            get => _statusText;
            set => this.RaiseAndSetIfChanged(ref _statusText, value);
        }

        // The list of proposed moves
        public ObservableCollection<FileMoveOperation> Operations { get; } = new();

        public ReactiveCommand<Unit, bool> ConfirmCommand { get; }
        public ReactiveCommand<Unit, Unit> CancelCommand { get; }

        private readonly Services.Library.SmartSorterService _sorterService;

        public SortPreviewViewModel(Services.Library.SmartSorterService sorterService)
        {
            _sorterService = sorterService;
            ConfirmCommand = ReactiveCommand.CreateFromTask(ExecuteMovesAsync);
            CancelCommand = ReactiveCommand.Create(() => { });
        }

        public void LoadOperations(System.Collections.Generic.IEnumerable<FileMoveOperation> ops)
        {
            Operations.Clear();
            foreach (var op in ops)
            {
                Operations.Add(op);
            }
            StatusText = $"Found {Operations.Count} tracks to organize.";
        }

        private async Task<bool> ExecuteMovesAsync()
        {
            if (IsProcessing) return false;
            
            try 
            {
                IsProcessing = true;
                StatusText = "Moving files... Do not close.";
                
                // Execute using the service
                await _sorterService.ExecuteSortAsync(
                    Operations.Where(o => o.IsChecked).ToList(),
                    (progress) => StatusText = progress
                );
                
                StatusText = "Organization complete!";
                IsProcessing = false;
                return true; // Closes dialog
            }
            catch (Exception ex)
            {
                StatusText = $"Error: {ex.Message}";
                IsProcessing = false;
                return false; // Keep open so user sees error
            }
        }
    }
}
