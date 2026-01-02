using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.ReactiveUI;
using ReactiveUI;
using SLSKDONET.ViewModels.Tools;
using System;
using System.Reactive.Disposables;

namespace SLSKDONET.Views.Avalonia.Controls
{
    public partial class SortPreviewDialog : ReactiveWindow<SortPreviewViewModel>
    {
        public SortPreviewDialog()
        {
            InitializeComponent();
            this.WhenActivated(disposables => 
            {
                if (ViewModel != null)
                {
                    // Close window when Confirm returns true (success) or Cancel is executed
                    ViewModel.ConfirmCommand.Subscribe(success => 
                    {
                        if (success) Close(true);
                    }).DisposeWith(disposables);

                    ViewModel.CancelCommand.Subscribe(_ => Close(false)).DisposeWith(disposables);
                }
            });
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
