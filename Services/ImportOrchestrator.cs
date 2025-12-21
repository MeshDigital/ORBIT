using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Services;

/// <summary>
/// Centralized orchestrator for all import operations.
/// Handles the entire import pipeline from source parsing to library persistence.
/// </summary>
public class ImportOrchestrator
{
    private readonly ILogger<ImportOrchestrator> _logger;
    private readonly ImportPreviewViewModel _previewViewModel;
    private readonly DownloadManager _downloadManager;
    private readonly INavigationService _navigationService;
    private readonly Views.INotificationService _notificationService;
    private readonly ILibraryService _libraryService;

    // Track current import to avoid duplicate event subscriptions in older logic
    // private bool _isHandlingImport; // REMOVED: Unused

    public ImportOrchestrator(
        ILogger<ImportOrchestrator> logger,
        ImportPreviewViewModel previewViewModel,
        DownloadManager downloadManager,
        INavigationService navigationService,
        Views.INotificationService notificationService,
        ILibraryService libraryService)
    {
        _logger = logger;
        _previewViewModel = previewViewModel;
        _downloadManager = downloadManager;
        _navigationService = navigationService;
        _notificationService = notificationService;
        _libraryService = libraryService;
    }

    /// <summary>
    /// Import with preview screen - allows user to select tracks.
    /// </summary>
    /// <summary>
    /// Import with preview screen - allows user to select tracks.
    /// Supports streaming for immediate UI feedback.
    /// </summary>
    /// <summary>
    /// Unified Import Method: Streams into Preview, then Hands off to DownloadManager.
    /// Replaces all legacy blocking/split logic.
    /// </summary>
    public async Task StartImportWithPreviewAsync(IImportProvider provider, string input)
    {
        try
        {
            _logger.LogInformation("Starting unified import from {Provider}: {Input}", provider.Name, input);

            if (provider is IStreamingImportProvider streamProvider)
            {
                 // Phase 7: Deterministic ID / Deduplication
                 var newJobId = Utils.GuidGenerator.CreateFromUrl(input);
                 
                 // Retrieve existing job if any (Deduplication)
                 var existingJob = await _libraryService.FindPlaylistJobAsync(newJobId);
                 
                 // Initialize UI
                 _previewViewModel.InitializeStreamingPreview(provider.Name, provider.Name, newJobId, input, existingJob);
                 
                 // Clean/Setup Callbacks
                 SetupPreviewCallbacks();

                 // Navigate
                 _navigationService.NavigateTo("ImportPreview");
                 
                 // Start Streaming
                 _ = Task.Run(async () => await StreamPreviewAsync(streamProvider, input));
            }
            else
            {
                throw new InvalidOperationException($"Provider {provider.Name} must implement IStreamingImportProvider");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start import from {Provider}", provider.Name);
            _notificationService.Show("Import Error", $"Failed to import: {ex.Message}", Views.NotificationType.Error);
        }
    }

    private async Task StreamPreviewAsync(IStreamingImportProvider provider, string input)
    {
        try
        {
            await foreach (var batch in provider.ImportStreamAsync(input))
            {
                 // Update Title from first batch if generic
                 if (!string.IsNullOrEmpty(batch.SourceTitle) && _previewViewModel.SourceTitle == provider.Name)
                 {
                     await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
                     {
                         _previewViewModel.SourceTitle = batch.SourceTitle;
                     });
                 }
                 
                 await _previewViewModel.AddTracksToPreviewAsync(batch.Tracks);
            }
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Error during streaming preview");
             await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => _previewViewModel.StatusMessage = "Stream error: " + ex.Message);
        }
        finally
        {
             await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => _previewViewModel.IsLoading = false);
        }
    }

    /// <summary>
    /// Set up event handlers for preview screen callbacks.
    /// </summary>
    private void SetupPreviewCallbacks()
    {
        // Always clean up any existing subscriptions first to avoid doubles
        _logger.LogInformation("Setting up ImportPreviewViewModel event callbacks");
        _previewViewModel.AddedToLibrary -= OnPreviewConfirmed;
        _previewViewModel.Cancelled -= OnPreviewCancelled;

        // Subscribe
        _previewViewModel.AddedToLibrary += OnPreviewConfirmed;
        _previewViewModel.Cancelled += OnPreviewCancelled;
    }

    /// <summary>
    /// Handle when user confirms tracks in preview screen.
    /// </summary>
    private void OnPreviewConfirmed(object? sender, PlaylistJob job)
    {
        try
        {
            _logger.LogInformation("Preview confirmed: {Title} with {Count} tracks",
                job.SourceTitle, job.OriginalTracks.Count);

            // Navigate to library
            _navigationService.NavigateTo("Library");

            _logger.LogInformation("Import completed and navigated to Library");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle preview confirmation");
        }
        finally
        {
            CleanupCallbacks();
        }
    }

    /// <summary>
    /// Handle when user cancels preview.
    /// </summary>
    private void OnPreviewCancelled(object? sender, EventArgs e)
    {
        _logger.LogInformation("Import preview cancelled");
        _navigationService.GoBack();
        CleanupCallbacks();
    }

    /// <summary>
    /// Remove event handlers after import completes.
    /// </summary>
    private void CleanupCallbacks()
    {
        _previewViewModel.AddedToLibrary -= OnPreviewConfirmed;
        _previewViewModel.Cancelled -= OnPreviewCancelled;
    }
}
