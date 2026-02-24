using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ReactiveUI;
using SLSKDONET.Services;
using SLSKDONET.Views;
using SLSKDONET.Features.LibrarySidebar;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Features.LibrarySidebar.ViewModels;

public class ForensicSidebarViewModel : ReactiveObject, ISidebarContent
{
    private readonly ForensicLibrarianService _forensicLibrarian;
    private readonly SLSKDONET.Views.INotificationService _notificationService;
    private PlaylistTrackViewModel? _activeTrack;

    private FraudReport? _activeFraud;
    public FraudReport? ActiveFraud
    {
        get => _activeFraud;
        set => this.RaiseAndSetIfChanged(ref _activeFraud, value);
    }

    private ObservableCollection<FraudReport> _detectedFrauds = new();
    public ObservableCollection<FraudReport> DetectedFrauds
    {
        get => _detectedFrauds;
        set => this.RaiseAndSetIfChanged(ref _detectedFrauds, value);
    }

    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        set => this.RaiseAndSetIfChanged(ref _isScanning, value);
    }

    public ICommand PurgeAndRedownloadCommand { get; }
    public ICommand RunScanCommand { get; }

    public ForensicSidebarViewModel(
        ForensicLibrarianService forensicLibrarian,
        SLSKDONET.Views.INotificationService notificationService)
    {
        _forensicLibrarian = forensicLibrarian;
        _notificationService = notificationService;

        PurgeAndRedownloadCommand = ReactiveCommand.CreateFromTask<string>(ExecutePurgeAndRedownloadAsync);
        RunScanCommand = ReactiveCommand.CreateFromTask(ExecuteLibraryScanAsync);
    }

    public async Task ActivateAsync(PlaylistTrackViewModel track)
    {
        _activeTrack = track;
        
        // Check if this specific track has a fraud report
        // For now, we'll just scan the active track specifically if needed, 
        // or check if it's already in the DetectedFrauds list.
        ActiveFraud = DetectedFrauds.FirstOrDefault(f => f.TrackUniqueHash == track.Model.TrackUniqueHash);
        
        await Task.CompletedTask;
    }

    public async Task ActivateBulkAsync(IReadOnlyList<PlaylistTrackViewModel> tracks)
    {
        await Task.CompletedTask;
    }

    public void Deactivate()
    {
        _activeTrack = null;
        ActiveFraud = null;
    }

    private async Task ExecutePurgeAndRedownloadAsync(string hash)
    {
        if (string.IsNullOrEmpty(hash)) return;

        var success = await _forensicLibrarian.PurgeAndRedownloadAsync(hash);
        if (success)
        {
            _notificationService.Show("Remediation Success", "Track purged from disk and re-enqueued for download.", NotificationType.Success);
            
            // Remove from list if present
            var report = DetectedFrauds.FirstOrDefault(f => f.TrackUniqueHash == hash);
            if (report != null) DetectedFrauds.Remove(report);
            
            if (ActiveFraud?.TrackUniqueHash == hash) ActiveFraud = null;
        }
        else
        {
            _notificationService.Show("Remediation Failed", "Could not complete the purge and redownload process.", NotificationType.Error);
        }
    }

    private async Task ExecuteLibraryScanAsync()
    {
        try
        {
            IsScanning = true;
            DetectedFrauds.Clear();
            
            var frauds = await _forensicLibrarian.ScanLibraryForFraudsAsync();
            foreach (var f in frauds) DetectedFrauds.Add(f);

            if (frauds.Any())
            {
                _notificationService.Show("Scan Complete", $"Found {frauds.Count} potential frauds.", SLSKDONET.Views.NotificationType.Warning);
            }
            else
            {
                _notificationService.Show("Scan Complete", "No integrity issues found.", SLSKDONET.Views.NotificationType.Success);
            }
        }
        finally
        {
            IsScanning = false;
        }
    }
}
