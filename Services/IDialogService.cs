using System.Threading.Tasks;

namespace SLSKDONET.Services;

public interface IDialogService
{
    /// <summary>
    /// Shows a confirmation dialog with Yes/No options.
    /// </summary>
    /// <returns>True if confirmed (Yes), False otherwise.</returns>
    Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Yes", string cancelLabel = "No");

    /// <summary>
    /// Shows a simple alert dialog.
    /// </summary>
    Task ShowAlertAsync(string title, string message);
}
