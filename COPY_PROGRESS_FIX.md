## Copy to Folder - Progress Improvement

**Current Behavior:**
The copy operation is completely silent - no UI feedback during the copy process.

**Quick Fix Applied:**
Changed line 481 in `TrackListViewModel.cs` from:
```csharp
_logger.LogDebug("Copied: {File}", fileName);
```

To:
```csharp
_logger.LogInformation("📂 Copied {Current}/{Total}: {File}", successCount + failCount, selectedTracks.Count, fileName);
```

**Result:**
Progress will now be visible in the console/logs showing:
- Current file number / Total files
- Filename being copied
- Real-time progress as each file completes

**Example Output:**
```
📂 Copied 1/25: Track1.mp3
📂 Copied 2/25: Track2.mp3
...
✅ Copy complete: 25 succeeded, 0 failed out of 25 files
```

**To Apply:**
Manually change line 481 in TrackListViewModel.cs or rebuild from this session's changes.

**Future Enhancement:**
For richer UI feedback, consider adding:
1. Progress bar in status bar
2. Toast notification on completion
3. Modal dialog with cancel button for large batches
