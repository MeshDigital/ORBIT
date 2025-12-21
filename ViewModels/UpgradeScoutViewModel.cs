using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

public class UpgradeScoutViewModel : INotifyPropertyChanged
{
    private readonly ILogger<UpgradeScoutViewModel> _logger;
    private readonly LibraryUpgradeScout _scout;
    private readonly DownloadDiscoveryService _discoveryService;
    private readonly IEventBus _eventBus;
    private readonly AppConfig _config;
    private bool _isScanning;
    private bool _isProcessing;

    public UpgradeScoutViewModel(
        ILogger<UpgradeScoutViewModel> logger,
        LibraryUpgradeScout scout,
        DownloadDiscoveryService discoveryService,
        IEventBus eventBus,
        AppConfig config)
    {
        _logger = logger;
        _scout = scout;
        _discoveryService = discoveryService;
        _eventBus = eventBus;
        _config = config;

        ScoutCommand = new AsyncRelayCommand(ExecuteScoutAsync);
        SearchAllCommand = new AsyncRelayCommand(ExecuteSearchAllAsync);
        UpgradeCommand = new AsyncRelayCommand<UpgradeCandidateViewModel>(ExecuteUpgradeAsync);
        ClearCompletedCommand = new RelayCommand(() => 
        {
            var completed = Candidates.Where(c => c.Status == UpgradeStatus.Completed).ToList();
            foreach (var item in completed)
            {
                Candidates.Remove(item);
            }
        });
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
    }

    public ObservableCollection<UpgradeCandidateViewModel> Candidates { get; } = new();

    public bool IsScanning
    {
        get => _isScanning;
        set { _isScanning = value; OnPropertyChanged(); }
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        set { _isProcessing = value; OnPropertyChanged(); }
    }

    public ICommand ScoutCommand { get; }
    public ICommand SearchAllCommand { get; }
    public ICommand UpgradeCommand { get; }
    public ICommand ClearCompletedCommand { get; }
    public ICommand CloseCommand { get; }
    
    public event EventHandler? CloseRequested;

    private async Task ExecuteScoutAsync()
    {
        IsScanning = true;
        try
        {
            Candidates.Clear();
            var entities = await _scout.GetUpgradeCandidatesAsync();
            foreach (var entity in entities)
            {
                Candidates.Add(new UpgradeCandidateViewModel(entity));
            }
        }
        finally
        {
            IsScanning = false;
        }
    }

    private async Task ExecuteSearchAllAsync()
    {
        IsProcessing = true;
        try
        {
            var pending = Candidates.Where(c => c.Status == UpgradeStatus.Pending).ToList();
            foreach (var candidate in pending)
            {
                candidate.Status = UpgradeStatus.Searching;
                
                // Map to PlaylistTrack for discovery
                var trackModel = new PlaylistTrack
                {
                    TrackUniqueHash = candidate.GlobalId,
                    Artist = candidate.Artist,
                    Title = candidate.Title,
                    Bitrate = candidate.CurrentBitrate
                };

                var bestMatch = await _discoveryService.FindBestMatchAsync(trackModel, default);
                
                if (bestMatch != null && (bestMatch.Bitrate > candidate.CurrentBitrate))
                {
                    candidate.ProposedReplacement = bestMatch;
                    candidate.Status = UpgradeStatus.Ready;
                    candidate.StatusMessage = $"Found {bestMatch.Bitrate}kbps replacement";
                }
                else
                {
                    candidate.Status = UpgradeStatus.Failed;
                    candidate.StatusMessage = "No better version found";
                }

                await Task.Delay(2000); // Throttling
            }
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private async Task ExecuteUpgradeAsync(UpgradeCandidateViewModel? candidate)
    {
        if (candidate?.ProposedReplacement == null) return;

        candidate.Status = UpgradeStatus.Upgrading;
        _logger.LogInformation("Manually triggering upgrade for {Artist} - {Title}", candidate.Artist, candidate.Title);
        
        // Publish the event that DownloadManager handles
        _eventBus.Publish(new SLSKDONET.Models.AutoDownloadUpgradeEvent
(candidate.GlobalId, candidate.ProposedReplacement));
        
        candidate.Status = UpgradeStatus.Completed;
        candidate.StatusMessage = "Upgrade queued";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) 
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
