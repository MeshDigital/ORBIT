using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Visuals;
using Avalonia.Controls;

namespace SLSKDONET.Services;

public class FileInteractionService : IFileInteractionService
{
    private readonly IClassicDesktopStyleApplicationLifetime? _desktop;

    public FileInteractionService()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
        }
    }

    public async Task<string?> OpenFolderDialogAsync(string title)
    {
        if (_desktop?.MainWindow is not { } window)
        {
            return null;
        }

        var result = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        return result.Count > 0 ? result[0].Path.LocalPath : null;
    }
}
