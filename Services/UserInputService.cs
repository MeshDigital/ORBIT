using System;

namespace SLSKDONET.Services;

public class UserInputService : IUserInputService
{
    public string? GetInput(string prompt, string title, string defaultValue = "")
    {
        // Using VisualBasic Interaction.InputBox as a quick standard dialog for WPF
        // This requires 'UseWindowsForms' or just a manual reference, but standard InputBox is in Microsoft.VisualBasic namespace
        // which is available in typical WPF Desktop projects if referenced.
        // Alternatively, we can make a trivial WPF Window. Let's make a trivial WPF Window to avoid VB dependency if possible,
        // OR just rely on the fact that most .NET Desktop apps have access to it.
        // Let's implement a simple custom InputDialog to be safe and cleaner.
        
        var dialog = new InputDialog(title, prompt, defaultValue);
        var result = dialog.ShowDialog();
        return result == true ? dialog.ResponseText : null;
    }
}
