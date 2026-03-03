using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Reactive;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Export;
using SLSKDONET.Services.Audio;
using SLSKDONET.ViewModels;
using SLSKDONET.Views;
using SLSKDONET.Data; // For LibraryEntryEntity
using ReactiveUI;

namespace SLSKDONET.Features.LibrarySidebar.ViewModels;

public class BulkActionSidebarViewModel : ReactiveObject, ISidebarContent
{
    private readonly RekordboxService _rekordboxService;
    private readonly ILibraryService _libraryService;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;
    private readonly BatchStemExportService _batchStemExportService;
    private readonly IEventBus _eventBus;
    private IReadOnlyList<PlaylistTrackViewModel> _activeTracks = System.Array.Empty<PlaylistTrackViewModel>();

    public BulkActionSidebarViewModel(
        RekordboxService rekordboxService,
        ILibraryService libraryService,
        IDialogService dialogService,
        INotificationService notificationService,
        BatchStemExportService batchStemExportService,
        IEventBus eventBus)
    {
        _rekordboxService = rekordboxService;
        _libraryService = libraryService;
        _dialogService = dialogService;
        _notificationService = notificationService;
        _batchStemExportService = batchStemExportService;
        _eventBus = eventBus;

        ExportCommand = ReactiveCommand.CreateFromTask(ExecuteExportAsync);
        ExtractStemsCommand = ReactiveCommand.Create(ExecuteExtractStems);
    }

    public ReactiveCommand<Unit, Unit> ExportCommand { get; }
    public ReactiveCommand<Unit, Unit> ExtractStemsCommand { get; }

    public int SelectedCount => _activeTracks.Count;

    public async Task ActivateAsync(PlaylistTrackViewModel track)
    {
        await ActivateBulkAsync(new[] { track });
    }

    public async Task ActivateBulkAsync(IReadOnlyList<PlaylistTrackViewModel> tracks)
    {
        _activeTracks = tracks;
        this.RaisePropertyChanged(nameof(SelectedCount));
        await Task.CompletedTask;
    }

    public void Deactivate()
    {
        _activeTracks = System.Array.Empty<PlaylistTrackViewModel>();
        this.RaisePropertyChanged(nameof(SelectedCount));
    }

    private void ExecuteExtractStems()
    {
        if (!_activeTracks.Any()) return;

        int count = _activeTracks.Count;
        _batchStemExportService.EnqueueBatch(_activeTracks, acapellaOnly: false);
        _notificationService.Show("Acapella Factory", $"Queued {count} tracks for Stem Extraction.", NotificationType.Information);
        
        Deactivate();
    }

    private async Task ExecuteExportAsync()
    {
        if (!_activeTracks.Any()) return;

        try
        {
            var defaultFileName = $"ORBIT_Bulk_{System.DateTime.Now:yyyyMMdd_HHmm}.xml";
            var outputPath = await _dialogService.SaveFileAsync("Export Rekordbox XML", defaultFileName, "xml");
            
            if (!string.IsNullOrEmpty(outputPath))
            {
                var entries = new List<LibraryEntryEntity>();
                foreach (var t in _activeTracks)
                {
                    var entry = await _libraryService.GetTrackEntityByHashAsync(t.GlobalId);
                    if (entry != null) entries.Add(entry);
                }

                await _rekordboxService.ExportTracksAsync(entries, outputPath);
                _notificationService.Show("Export Successful", $"{entries.Count} tracks exported.", NotificationType.Success);
            }
        }
        catch (System.Exception ex)
        {
            _notificationService.Show("Export Failed", ex.Message, NotificationType.Error);
        }
    }
}
