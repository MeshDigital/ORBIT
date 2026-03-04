using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;

namespace SLSKDONET.Services;

/// <summary>
/// "SnapSync" - Phase 6 Automatic Library Snapshots
/// Exports daily compressed JSON snapshots of the entire database state.
/// </summary>
public class LibraryBackupService : IDisposable
{
    private readonly ILogger<LibraryBackupService> _logger;
    private readonly DatabaseService _databaseService;
    private readonly AppConfig _config;
    private readonly CancellationTokenSource _cts = new();
    private Task? _backgroundTask;
    private readonly string _backupDirectory;

    public LibraryBackupService(
        ILogger<LibraryBackupService> logger,
        DatabaseService databaseService,
        AppConfig config)
    {
        _logger = logger;
        _databaseService = databaseService;
        _config = config;
        
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _backupDirectory = Path.Combine(appData, "SLSKDONET", "Backups");
    }

    public void Start()
    {
        if (_backgroundTask != null) return;
        _backgroundTask = BackupLoopAsync(_cts.Token);
        _logger.LogInformation("🛡️ Library Backup Service Started (SnapSync)");
    }

    private async Task BackupLoopAsync(CancellationToken token)
    {
        // Wait initially to let the app start
        try { await Task.Delay(TimeSpan.FromMinutes(5), token); } catch { return; }

        while (!token.IsCancellationRequested)
        {
            try
            {
                await RunBackupAsync(token);
                // Wait 24 hours
                await Task.Delay(TimeSpan.FromHours(24), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to run daily library backup");
                await Task.Delay(TimeSpan.FromHours(1), token); // Retry in 1 hour
            }
        }
    }

    public async Task RunBackupAsync(CancellationToken token = default)
    {
        _logger.LogInformation("📸 Starting SnapSync Library Backup...");
        if (!Directory.Exists(_backupDirectory))
        {
            Directory.CreateDirectory(_backupDirectory);
        }

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var backupFile = Path.Combine(_backupDirectory, $"LibraryBackup_{timestamp}.json.gz");

        try
        {
            var jobs = await _databaseService.LoadAllPlaylistJobsAsync();
            var libraryTokens = await _databaseService.LoadAllLibraryEntriesAsync();

            var snapshot = new
            {
                Timestamp = DateTime.UtcNow,
                Version = "1.0",
                Projects = jobs,
                Library = libraryTokens
            };

            using var fileStream = new FileStream(backupFile, FileMode.Create);
            using var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal);
            
            // Need ReferenceHandler.Preserve or IgnoreCycles if EF Core tracking is preserved, 
            // but AsNoTracking should make it tree-like. We'll use IgnoreCycles to be safe.
            var options = new JsonSerializerOptions 
            { 
                ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
                WriteIndented = false 
            };
            
            await JsonSerializer.SerializeAsync(gzipStream, snapshot, options, cancellationToken: token);
            
            _logger.LogInformation("✅ SnapSync Completed: {File}", backupFile);
            
            // Cleanup old backups (keep last 7)
            CleanupOldBackups();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SnapSync Backup failed");
            if (File.Exists(backupFile)) File.Delete(backupFile);
            throw;
        }
    }

    private void CleanupOldBackups()
    {
        try
        {
            var files = new DirectoryInfo(_backupDirectory)
                .GetFiles("LibraryBackup_*.json.gz")
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();

            if (files.Count > 7)
            {
                foreach (var oldFile in files.Skip(7))
                {
                    oldFile.Delete();
                    _logger.LogDebug("Deleted old backup: {Name}", oldFile.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup old backups");
        }
    }

    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
    }
}
