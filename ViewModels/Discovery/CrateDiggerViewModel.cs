using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels.Discovery;

public class CrateDiggerViewModel : INotifyPropertyChanged
{
    private readonly ILogger<CrateDiggerViewModel> _logger;
    private readonly INavigationService _navigationService;
    private readonly ImportOrchestrator _importOrchestrator;
    private readonly AutoCleanerService _autoCleaner;
    private readonly IEventBus _eventBus;

    public event PropertyChangedEventHandler? PropertyChanged;

    private string _scratchpadText = string.Empty;
    public string ScratchpadText
    {
        get => _scratchpadText;
        set => SetProperty(ref _scratchpadText, value);
    }

    private bool _isProcessing;
    public bool IsProcessing
    {
        get => _isProcessing;
        set => SetProperty(ref _isProcessing, value);
    }

    public ICommand AutoCleanCommand { get; }
    public ICommand SendToInBoxCommand { get; }

    public CrateDiggerViewModel(
        ILogger<CrateDiggerViewModel> logger,
        INavigationService navigationService,
        ImportOrchestrator importOrchestrator,
        AutoCleanerService autoCleaner,
        IEventBus eventBus)
    {
        _logger = logger;
        _navigationService = navigationService;
        _importOrchestrator = importOrchestrator;
        _autoCleaner = autoCleaner;
        _eventBus = eventBus;

        AutoCleanCommand = new AsyncRelayCommand(ExecuteAutoCleanAsync);
        SendToInBoxCommand = new AsyncRelayCommand(ExecuteSendToInBoxAsync);
    }

    private async Task ExecuteAutoCleanAsync()
    {
        if (string.IsNullOrWhiteSpace(ScratchpadText)) return;

        IsProcessing = true;
        try
        {
            _logger.LogInformation("Auto-Cleaning tracklist...");
            
            var lines = ScratchpadText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var cleanedLines = new List<string>();

            foreach (var line in lines)
            {
                var cleaned = _autoCleaner.Clean(line);
                // We default to "Smart" clean for the scratchpad UI, but keep others in mind
                cleanedLines.Add(cleaned.Smart);
            }

            ScratchpadText = string.Join(Environment.NewLine, cleanedLines);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to auto-clean tracklist");
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private async Task ExecuteSendToInBoxAsync()
    {
        if (string.IsNullOrWhiteSpace(ScratchpadText)) return;

        IsProcessing = true;
        try
        {
            _logger.LogInformation("Sending tracks to In-Box...");
            
            var lines = ScratchpadText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            
            // Create a temporary project/playlist for these tracks
            var projectName = $"Crate Dig - {DateTime.Now:MMM dd HH:mm}";
            
            // In a real implementation, we'd parse these into individual track objects
            // and use ImportOrchestrator to create a project.
            
            // For now, let's just navigate to Library (In-Box)
            _navigationService.NavigateTo("Library");
            
            // TODO: Actually create the project and add tracks
        }
        finally
        {
            IsProcessing = false;
        }
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
