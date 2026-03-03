using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services.IO;

namespace SLSKDONET.Services
{
    public class SpotifySyncManager : IDisposable
    {
        private readonly ILogger<SpotifySyncManager> _logger;
        private readonly SpotifyCrateSyncService _syncService;
        private readonly SLSKDONET.Configuration.ConfigManager _configManager;
        
        private readonly string _syncJobsFilePath;
        private CancellationTokenSource? _daemonCts;

        public ObservableCollection<SpotifySyncJob> ActiveJobs { get; } = new();

        public SpotifySyncManager(
            ILogger<SpotifySyncManager> logger,
            SpotifyCrateSyncService syncService,
            SLSKDONET.Configuration.ConfigManager configManager)
        {
            _logger = logger;
            _syncService = syncService;
            _configManager = configManager;

            var configDir = Path.GetDirectoryName(SLSKDONET.Configuration.ConfigManager.GetDefaultConfigPath()) 
                            ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _syncJobsFilePath = Path.Combine(configDir, "spotify_syncs.json");
        }

        public async Task LoadJobsAsync()
        {
            if (!File.Exists(_syncJobsFilePath))
            {
                _logger.LogInformation("No existing spotify_syncs.json found. Starting fresh.");
                return;
            }

            try
            {
                string json = await File.ReadAllTextAsync(_syncJobsFilePath);
                var jobs = JsonSerializer.Deserialize<SpotifySyncJob[]>(json);
                if (jobs != null)
                {
                    ActiveJobs.Clear();
                    foreach (var job in jobs)
                    {
                        ActiveJobs.Add(job);
                    }
                    _logger.LogInformation("Loaded {Count} Spotify Sync Jobs from disk.", ActiveJobs.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load spotify_syncs.json. Creating a new collection.");
                ActiveJobs.Clear();
            }
        }

        public async Task SaveJobsAsync()
        {
            try
            {
                string json = JsonSerializer.Serialize(ActiveJobs, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_syncJobsFilePath, json);
                _logger.LogInformation("Saved {Count} Spotify Sync Jobs to disk.", ActiveJobs.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist spotify_syncs.json.");
            }
        }

        public async Task AddJobAsync(string urlOrId, string name)
        {
            var job = new SpotifySyncJob
            {
                Id = Guid.NewGuid(),
                PlaylistUrlOrId = urlOrId,
                PlaylistName = name,
                LastSyncedAt = DateTime.MinValue,
                IsActive = true,
                IsSyncing = false
            };

            ActiveJobs.Add(job);
            await SaveJobsAsync();

            // Trigger an immediate initial sync in the background
            _ = RunSingleSyncSafeAsync(job);
        }

        public async Task RemoveJobAsync(Guid id)
        {
            var job = ActiveJobs.FirstOrDefault(j => j.Id == id);
            if (job != null)
            {
                ActiveJobs.Remove(job);
                await SaveJobsAsync();
                _logger.LogInformation("Removed Sync Job for '{PlaylistName}'.", job.PlaylistName);
            }
        }

        public void StartDaemon()
        {
            if (_daemonCts != null) return; // Already running

            _logger.LogInformation("Starting Spotify Crate Sync Daemon (1-Hour Polling, 12-Hour Threshold).");
            _daemonCts = new CancellationTokenSource();
            
            _ = Task.Run(() => DaemonLoopAsync(_daemonCts.Token));
        }

        public void StopDaemon()
        {
            _daemonCts?.Cancel();
            _daemonCts?.Dispose();
            _daemonCts = null;
            _logger.LogInformation("Stopped Spotify Crate Sync Daemon.");
        }

        private async Task DaemonLoopAsync(CancellationToken ct)
        {
            // Initial delay to let the app start up completely
            await Task.Delay(TimeSpan.FromSeconds(30), ct);

            // Periodic timer ticking every 1 hour
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));

            // Run first pass immediately
            await ProcessJobsAsync(ct);

            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    await ProcessJobsAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Daemon loop was canceled gracefully.");
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Daemon loop encountered a fatal error.");
            }
        }

        private async Task ProcessJobsAsync(CancellationToken ct)
        {
            _logger.LogInformation("Daemon woken up. Checking {Count} active jobs for sync eligibility.", ActiveJobs.Count(j => j.IsActive));

            foreach (var job in ActiveJobs.Where(j => j.IsActive).ToList())
            {
                ct.ThrowIfCancellationRequested();

                // If it hasn't been 12 hours since the last sync, skip it.
                if (DateTime.UtcNow - job.LastSyncedAt < TimeSpan.FromHours(12))
                {
                    _logger.LogTrace("Skipping '{PlaylistName}'. Last synced {Hours:F1} hours ago.", job.PlaylistName, (DateTime.UtcNow - job.LastSyncedAt).TotalHours);
                    continue;
                }

                await RunSingleSyncSafeAsync(job, ct);
            }
        }

        public async Task RunSingleSyncSafeAsync(SpotifySyncJob job, CancellationToken ct = default)
        {
            try
            {
                job.IsSyncing = true;
                _logger.LogInformation("Daemon triggered sync for '{PlaylistName}'.", job.PlaylistName);
                
                await _syncService.ExecuteSyncAsync(job, ct);
                
                // job.LastSyncedAt is updated inside ExecuteSyncAsync natively,
                // but we will also ensure persistence right after.
                await SaveJobsAsync();
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Sync for '{PlaylistName}' was canceled.", job.PlaylistName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync '{PlaylistName}'. Daemon will retry next cycle.", job.PlaylistName);
            }
            finally
            {
                job.IsSyncing = false;
            }
        }

        public void Dispose()
        {
            StopDaemon();
        }
    }
}
