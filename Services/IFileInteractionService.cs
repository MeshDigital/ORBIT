using System.Threading.Tasks;

namespace SLSKDONET.Services;

public interface IFileInteractionService
{
    Task<string?> OpenFolderDialogAsync(string title);
}
