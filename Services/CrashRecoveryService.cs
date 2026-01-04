using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SLSKDONET.Services.IO;
using SLSKDONET.Services.Repositories;

namespace SLSKDONET.Services;

public class RecoveryStats
{
    public int Resumed { get; set; }
    public int Cleaned { get; set; }
    public int Failures { get; set; }
    public int DeadLetters { get; set; }
}

/// <summary>
/// Phase 2A: Recovers interrupted operations on application startup.
/// Uses priority-based recovery, dead-letter handling, and path sanitization.
/// </summary>
public class CrashRecoveryService
{
    private readonly ILogger<CrashRecoveryService> _logger;
    private readonly CrashRecoveryJournal _journal;
    private readonly DatabaseService _databaseService;
    private readonly IEventBus _eventBus; // Phase 2A
    private readonly IEnrichmentTaskRepository _enrichmentTaskRepository; // Phase 0.2

    public CrashRecoveryService(
        ILogger<CrashRecoveryService> logger,
        CrashRecoveryJournal journal,
        DatabaseService databaseService,
        IEventBus eventBus,
        IEnrichmentTaskRepository enrichmentTaskRepository)
    {
        _logger = logger;
        _journal = journal;
        _databaseService = databaseService;
        _eventBus = eventBus;
        _enrichmentTaskRepository = enrichmentTaskRepository;
    }

    /// <summary>
    /// Called on application startup to recover from crashes.
    /// Runs asynchronously to avoid blocking UI.
    /// </summary>
    public async Task RecoverAsync()
    {
        _logger.LogInformation("🔧 Starting Ironclad Recovery...");

        // ASYNC TRAP FIX: Run recovery on background thread
        await Task.Run(async () =>
        {
            var startTime = DateTime.UtcNow; // Track recovery duration
            try
            {
                // STEP 1: Clear truly stale checkpoints (>24 hours)
                await _journal.ClearStaleCheckpointsAsync();

                // STEP 2: Get pending checkpoints
                var pendingCheckpoints = await _journal.GetPendingCheckpointsAsync();
                
                if (!pendingCheckpoints.Any())
                {
                    _logger.LogInformation("✅ No pending operations to recover");
                    return;
                }

                _logger.LogInformation("🔄 Recovering {Count} operations...", pendingCheckpoints.Count);

                var stats = new RecoveryStats();

                // STEP 3: Recover each operation (already sorted by priority)
                foreach (var checkpoint in pendingCheckpoints)
                {
                    try
                    {
                        // DEAD-LETTER CHECK
                        if (checkpoint.FailureCount >= 3)
                        {
                            _logger.LogWarning(
                                "⚠️ Checkpoint {Id} failed {Count} times - moving to dead-letter",
                                checkpoint.Id, checkpoint.FailureCount);
                            
                            // Phase 3A: Mark as Dead-Letter in DB (persistent status)
                            await _journal.MarkAsDeadLetterAsync(checkpoint.Id);
                            // We do NOT call CompleteCheckpointAsync removal, as MarkAsDeadLetterAsync keeps it
                            // but sets Status=2 so it's ignored by future GetPending calls.
                            stats.DeadLetters++;
                            continue;
                        }

                        switch (checkpoint.OperationType)
                        {
                            case OperationType.Download:
                                // Handled by DownloadManager.HydrateFromCrashAsync() during its initialization
                                // We skip it here to avoid double-processing or race conditions.
                                // If DownloadManager runs successfully, it will delete these checkpoints.
                                _logger.LogDebug("Skipping download checkpoint {Id} (handled by DownloadManager)", checkpoint.Id);
                                break;

                            case OperationType.TagWrite:
                                await RecoverTagWriteAsync(checkpoint, stats);
                                break;

                            case OperationType.MetadataHydration:
                                await RecoverHydrationAsync(checkpoint, stats);
                                break;

                            default:
                                _logger.LogWarning("Unknown operation type: {Type}", checkpoint.OperationType);
                                await _journal.CompleteCheckpointAsync(checkpoint.Id);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Recovery failed for checkpoint {Id}", checkpoint.Id);
                        
                        // Increment failure count
                        checkpoint.FailureCount++;
                        await _journal.LogCheckpointAsync(checkpoint);
                        stats.Failures++;
                    }
                }

                var recoveryDuration = DateTime.UtcNow - startTime;
                
                _logger.LogInformation(
                    "✅ Recovery complete: {Resumed} resumed, {Cleaned} cleaned, {Failed} failed, {DeadLetters} dead-letters (Duration: {Duration}ms)",
                    stats.Resumed, stats.Cleaned, stats.Failures, stats.DeadLetters, recoveryDuration.TotalMilliseconds);

                // Phase 2A: Publish RecoveryCompletedEvent for UX (non-intrusive)
                if (stats.Resumed > 0 || stats.DeadLetters > 0)
                {
                    _eventBus.Publish(new SLSKDONET.Models.RecoveryCompletedEvent(
                        stats.Resumed,
                        stats.Cleaned,
                        stats.Failures,
                        stats.DeadLetters,
                        recoveryDuration));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error during crash recovery");
            }
        });
    }



    private async Task RecoverTagWriteAsync(RecoveryCheckpoint checkpoint, RecoveryStats stats)
    {
        var state = JsonSerializer.Deserialize<TagWriteCheckpointState>(checkpoint.StateJson);
        if (state== null)
        {
            await _journal.CompleteCheckpointAsync(checkpoint.Id);
            return;
        }

        _logger.LogInformation("Recovering tag write: {Path}", state.FilePath);

        // IDEMPOTENT CHECK 1: If temp is gone and target exists, assume success
        if (!File.Exists(state.TempPath) && File.Exists(state.FilePath))
        {
            _logger.LogInformation("✅ Tag write appears complete (target exists, temp gone). Completing checkpoint.");
            await _journal.CompleteCheckpointAsync(checkpoint.Id);
            stats.Resumed++;
            return;
        }

        // IDEMPOTENT CHECK 2: If temp exists but target is missing, perform the move now
        // This means crash happened after original was deleted but before temp was moved
        if (File.Exists(state.TempPath) && !File.Exists(state.FilePath))
        {
            try
            {
                _logger.LogInformation("🔧 Target missing but temp exists. Performing delayed atomic move: {Temp} → {Target}",
                    state.TempPath, state.FilePath);
                
                File.Move(state.TempPath, state.FilePath);
                
                // Restore original timestamps if available
                if (state.OriginalCreationTime.HasValue)
                {
                    var fileInfo = new FileInfo(state.FilePath);
                    fileInfo.CreationTime = state.OriginalCreationTime.Value;
                    fileInfo.LastWriteTime = state.OriginalTimestamp;
                }
                
                _logger.LogInformation("✅ Completed delayed atomic move successfully");
                await _journal.CompleteCheckpointAsync(checkpoint.Id);
                stats.Resumed++;
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to complete delayed atomic move");
                throw;
            }
        }

        // Clean up orphaned temp file if it exists (both target and temp exist = incomplete)
        if (!string.IsNullOrEmpty(state.TempPath) && File.Exists(state.TempPath))
        {
            // Verify temp file isn't empty before deleting
            var tempInfo = new FileInfo(state.TempPath);
            if (tempInfo.Length == 0)
            {
                _logger.LogWarning("Temp file is empty, safe to delete: {Path}", state.TempPath);
            }

            try
            {
                File.Delete(state.TempPath);
                _logger.LogInformation("🗑️ Cleaned up orphaned temp file: {Path}", state.TempPath);
                stats.Cleaned++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete temp file: {Path}", state.TempPath);
            }
        }

        await _journal.CompleteCheckpointAsync(checkpoint.Id);
    }

    private async Task RecoverHydrationAsync(RecoveryCheckpoint checkpoint, RecoveryStats stats)
    {
        var state = JsonSerializer.Deserialize<HydrationCheckpointState>(checkpoint.StateJson);
        if (state == null)
        {
            await _journal.CompleteCheckpointAsync(checkpoint.Id);
            return;
        }

        _logger.LogInformation("Recovering metadata hydration: Track {Id}, Step {Step}", 
            state.TrackGlobalId, state.Step);

        try
        {
            // Re-queue for enrichment
            // Note: We use Guid.Empty for PlaylistId as the enrichment worker will resolve context from the track itself 
            // or the repository handles generic queuing.
            if (Guid.TryParse(state.TrackGlobalId, out _))
            {
                await _enrichmentTaskRepository.QueueTaskAsync(state.TrackGlobalId, Guid.Empty);
                _logger.LogInformation("✅ Re-queued enrichment for track {Id}", state.TrackGlobalId);
                stats.Resumed++;
            }
            else
            {
                 _logger.LogWarning("Invalid TrackGlobalId in hydration checkpoint: {Id}", state.TrackGlobalId);
                 stats.Cleaned++;
            }
            
            // Mark checkpoint as complete since we handed it off to the persistent queue
            await _journal.CompleteCheckpointAsync(checkpoint.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recover hydration task");
            throw; // Will trigger retry logic
        }
    }

    private async Task LogDeadLetterAsync(RecoveryCheckpoint checkpoint)
    {
        try
        {
            var deadLetterPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SLSKDONET", "dead_letters.log");
            
            var logEntry = $"[{DateTime.UtcNow:O}] DEAD_LETTER | Type: {checkpoint.OperationType} | " +
                          $"Path: {checkpoint.TargetPath} | Failures: {checkpoint.FailureCount} | " +
                          $"State: {checkpoint.StateJson}\n";
            
            await File.AppendAllTextAsync(deadLetterPath, logEntry);
            
            _logger.LogWarning("Dead-letter logged to: {Path}", deadLetterPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log dead-letter");
        }
    }
}
