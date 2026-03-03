using Avalonia.Threading;
using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels
{
    public class SpotifySyncSettingsViewModel : ReactiveObject
    {
        private readonly SpotifySyncManager _syncManager;
        private readonly INotificationService _notificationService;

        public ObservableCollection<SpotifySyncJob> ActiveJobs => _syncManager.ActiveJobs;

        private string _newPlaylistUrl = string.Empty;
        public string NewPlaylistUrl
        {
            get => _newPlaylistUrl;
            set => this.RaiseAndSetIfChanged(ref _newPlaylistUrl, value);
        }

        private bool _isAdding = false;
        public bool IsAdding
        {
            get => _isAdding;
            set => this.RaiseAndSetIfChanged(ref _isAdding, value);
        }

        public ReactiveCommand<Unit, Unit> AddJobCommand { get; }
        public ReactiveCommand<Guid, Unit> RemoveJobCommand { get; }
        public ReactiveCommand<SpotifySyncJob, Unit> ForceSyncCommand { get; }

        public SpotifySyncSettingsViewModel(SpotifySyncManager syncManager, INotificationService notificationService)
        {
            _syncManager = syncManager;
            _notificationService = notificationService;

            var canAdd = this.WhenAnyValue(
                x => x.NewPlaylistUrl,
                x => x.IsAdding,
                (url, isAdding) => !string.IsNullOrWhiteSpace(url) && 
                                   (url.Contains("spotify.com/playlist") || url.Length == 22) && 
                                   !isAdding);

            AddJobCommand = ReactiveCommand.CreateFromTask(AddJobAsync, canAdd);
            RemoveJobCommand = ReactiveCommand.CreateFromTask<Guid>(RemoveJobAsync);
            ForceSyncCommand = ReactiveCommand.CreateFromTask<SpotifySyncJob>(ForceSyncAsync);
        }

        private async Task AddJobAsync()
        {
            try
            {
                IsAdding = true;
                
                string url = NewPlaylistUrl.Trim();
                string placeholderName = "Resolving Playlist..."; 

                await _syncManager.AddJobAsync(url, placeholderName);

                NewPlaylistUrl = string.Empty;
                Dispatcher.UIThread.Post(() => _notificationService.Show("Spotify Sync", "Playlist added to monitoring queue.", NotificationType.Success));
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => _notificationService.Show("Sync Error", $"Failed to add playlist: {ex.Message}", NotificationType.Error));
            }
            finally
            {
                IsAdding = false;
            }
        }

        private async Task RemoveJobAsync(Guid id)
        {
            try
            {
                await _syncManager.RemoveJobAsync(id);
                Dispatcher.UIThread.Post(() => _notificationService.Show("Spotify Sync", "Playlist removed from monitoring.", NotificationType.Information));
            }
            catch (Exception ex)
            {
                 Dispatcher.UIThread.Post(() => _notificationService.Show("Sync Error", $"Failed to remove playlist: {ex.Message}", NotificationType.Error));
            }
        }

        private async Task ForceSyncAsync(SpotifySyncJob job)
        {
            if (job == null || job.IsSyncing) return;

            try
            {
                // Fire and forget so we don't block the UI thread during the long sync
                _ = Task.Run(async () => 
                {
                    try
                    {
                        await _syncManager.RunSingleSyncSafeAsync(job);
                        Dispatcher.UIThread.Post(() => _notificationService.Show("Sync Complete", $"Synced {job.PlaylistName}", NotificationType.Success));
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.UIThread.Post(() => _notificationService.Show("Sync Failed", $"Failed to sync {job.PlaylistName}: {ex.Message}", NotificationType.Error));
                    }
                });
            }
            catch (Exception ex)
            {
                _notificationService.Show("Sync Error", $"Could not trigger sync: {ex.Message}", NotificationType.Error);
            }
        }
    }
}
