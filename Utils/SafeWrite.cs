using System;
using System.IO;
using System.Threading.Tasks;

namespace SLSKDONET.Utils;

/// <summary>
/// Provides atomic file write operations to prevent data corruption.
/// Uses the "Write-Verify-Swap" pattern with platform-specific optimizations.
/// </summary>
public static class SafeWrite
{
    private const string TempExtension = ".tmp";
    private const string BackupExtension = ".bak";

    /// <summary>
    /// Performs an atomic write operation using a temporary file and swap.
    /// Supports optional verification and backup retention.
    /// </summary>
    /// <param name="targetPath">The final destination path.</param>
    /// <param name="writeAction">Action to write data to the temp path. string param is the temp file path.</param>
    /// <param name="verifyAction">Optional action to verify the written file before swap. If false, operation aborts.</param>
    /// <returns>True if successful, False if verification failed.</returns>
    public static async Task<bool> WriteAtomicAsync(
        string targetPath,
        Func<string, Task> writeAction,
        Func<string, Task<bool>>? verifyAction = null)
    {
        // 1. Prepare Paths
        // Ensure temp file is on the same volume for atomic move support
        string tempPath = targetPath + TempExtension;
        string backupPath = targetPath + BackupExtension;

        try
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Clean up any stale temp file
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            // 2. Execute Write Action
            await writeAction(tempPath);

            // 3. Verify
            if (verifyAction != null)
            {
                bool isValid = await verifyAction(tempPath);
                if (!isValid)
                {
                    // Verification failed - cleanup and abort
                    CleanupFile(tempPath);
                    return false;
                }
            }

            // 4. Atomic Swap
            // Strategy:
            // - If target exists: File.Replace (Target -> Backup, Temp -> Target)
            // - If target missing: File.Move (Temp -> Target)
            
            if (File.Exists(targetPath))
            {
                // File.Replace is atomic on NTFS
                // 'tempPath' replaces 'targetPath', original 'targetPath' is moved to 'backupPath'
                try 
                {
                    // Clean up stale backup if present
                    if (File.Exists(backupPath))
                    {
                        File.Delete(backupPath);
                    }
                    
                    File.Replace(tempPath, targetPath, backupPath);
                    
                    // Cleanup backup after successful swap (unless retention logic wraps this)
                    // For now, we clean up immediately to behave like a normal overwrite
                    if (File.Exists(backupPath))
                    {
                        File.Delete(backupPath);
                    }
                }
                catch (IOException)
                {
                    // Fallback for file lock issues or non-NTFS
                    // Manual Move-Move strategy
                    if (File.Exists(backupPath)) File.Delete(backupPath);
                    File.Move(targetPath, backupPath);
                    File.Move(tempPath, targetPath);
                    File.Delete(backupPath);
                }
            }
            else
            {
                // Simple atomic move
                File.Move(tempPath, targetPath);
            }

            return true;
        }
        catch (Exception)
        {
            // Log? Rethrow? For a generic utility, rethrowing is usually best
            // keeping the user aware of IO failures.
            CleanupFile(tempPath); // Clean up temp debris
            throw;
        }
        finally
        {
            // Final cleanup check
            CleanupFile(tempPath);
        }
    }
    
    /// <summary>
    /// Atomically moves a pre-existing source file to a target path using the SafeWrite reliability pattern.
    /// Useful for downloads where the file is already written to a .part location.
    /// </summary>
    public static async Task<bool> MoveAtomicAsync(
        string sourcePath, 
        string targetPath,
        Func<string, Task<bool>>? verifyAction = null)
    {
         if (!File.Exists(sourcePath))
             throw new FileNotFoundException("Source file not found", sourcePath);

         return await WriteAtomicAsync(targetPath, 
             async (tempPath) => 
             {
                 // Move source to temp location to prepare for the swap
                 // We Copy instead of Move to simulate a "Write" action, 
                 // preserving the source if something goes wrong until the end?
                 // Actually, for MoveAtomic, we usually want to consume the source.
                 // Let's Move.
                 if (File.Exists(tempPath)) File.Delete(tempPath);
                 File.Move(sourcePath, tempPath);
                 await Task.CompletedTask;
             }, 
             verifyAction);
    }

    private static void CleanupFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch { /* Ignore cleanup errors */ }
    }
}
