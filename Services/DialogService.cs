using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using SLSKDONET.Views.Avalonia.Controls;
using Avalonia.Threading;

namespace SLSKDONET.Services;

public class DialogService : IDialogService
{
    private Window? GetOwnerWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }
        return null;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Yes", string cancelLabel = "No")
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dialog = new ConfirmationDialog(title, message, confirmLabel, cancelLabel);
            var owner = GetOwnerWindow();

            if (owner != null)
            {
                await dialog.ShowDialog(owner);
            }
            else
            {
                // Fallback: If no owner (should unlikely happen in this flow), try to show standalone
               dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
               dialog.Show();
               // We can't await completion easily with Show(). 
               // Assuming MainWindow always exists for user interactions.
            }

            return dialog.IsConfirmed;
        });
    }

    public async Task ShowAlertAsync(string title, string message)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
             var dialog = new ConfirmationDialog(title, message, "OK", "");
             // Hide No button for alert
             var noBtn = dialog.FindControl<Button>("NoButton");
             if (noBtn != null) noBtn.IsVisible = false;

             var owner = GetOwnerWindow();
             if (owner != null)
             {
                 await dialog.ShowDialog(owner);
             }
        });
    }
}
