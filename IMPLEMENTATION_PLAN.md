# Phase 0.7: Unified Download UI

## Goal
Unify the visual design of the Download Center by applying the "Card" style (currently used in the Failed/Rejected queue) to Active and Completed downloads.

## Proposed Changes

### Controls
#### [MODIFY] [StandardTrackRow.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/Views/Avalonia/Controls/StandardTrackRow.axaml)
- Move root `Border` properties to Styles.
- Define a Default Style (List View - existing).
- Define a "Card" Style (Card View - new):
    - Background: `#252526`
    - Border: `#333`, 1px
    - CornerRadius: 6
    - Padding: 12
    - Margin: 0,0,0,8 (Spacing)
- **Fix Visibility Gating (Ghost Data)**:
    - `Vibe Button`: Add `IsCompleted` to MultiBinding.
    - `Primary Genre`: Add `IsCompleted` to visibility check (or MultiBinding).
    - `TechnicalSummary`: Bind `FontStyle` to `IsCompleted` (Italic if false).

### Views
#### [MODIFY] [DownloadsPage.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/Views/Avalonia/DownloadsPage.axaml)
- Update `Active` downloads `ItemsRepeater` template:
    - Add `Classes="card"` to `c:StandardTrackRow`.
    - **[ENHANCEMENT]** Use PseudoClasses (e.g. `:card`) in `StandardTrackRow` code-behind for cleaner CSS-like styling instead of relying solely on `IsVisible`.
    - Remove overly restrictive wrapping borders if no longer needed.
- Update `DownloadItemTemplate` (Completed):
    - Add `Classes="card"` to `c:StandardTrackRow`.
    - Clean up surrounding borders.

## Verification Plan
1.  **Check Library**:
    -   Ensure Library list still looks concise (List style).
2.  **Check Download Center**:
    -   **Active Tab**: Verify rows look like cards (Rounded, Darker background, Spaced).
    -   **Completed Tab**: Verify rows look like cards.
    -   **Failed Tab**: Verify consistency (Failed items use custom template, but should match visually).

## Phase 0.8: Diagnostics Fix
### Views
#### [MODIFY] [DownloadsPage.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/Views/Avalonia/DownloadsPage.axaml)
- **FailedDownloadItemTemplate**:
    - Update `RejectionDetails` Grid:
        - Add column/row to display `Filename`.
        - Add ToolTip to show full path and detailed rejection reason on hover.
        - **[ENHANCEMENT]** Make ToolTip selectable (or add Copy icon).
        - **[ENHANCEMENT]** Color-code rejection reasons (Yellow=Bitrate, Orange=Mismatch) for quick scanning.
        - **[NEW]** Add "View Log" button to show exact search strings/peer responses.

## Phase 0.9: Download Resilience
### Services
#### [MODIFY] [DownloadManager.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/Services/DownloadManager.cs)
- **Fix "Stuck" Retries**:
    - Update `HardRetryTrack` to set `ctx.Model.Priority = 0` (High Priority) AND call `_analysisQueue.RequestRefill()`.
- **Fix "Queue Reset/Mass Failure"**:
    - Add **Circuit Breaker** to `ProcessQueueLoop`.
    - Check `_soulseek.IsConnected` at start of loop.
    - If disconnected:
        - Publish `GlobalStatusEvent` with Backoff Countdown (e.g. "Retrying in 8s...").
        - Transition downloading tracks to "WaitingForConnection" visual state.
        - Wait with **Exponential Backoff** (2s, 4s, 8s..., max 60s).

## Phase 1.0: Final Polish (Fitness & Finish)
### Controls
#### [MODIFY] [StandardTrackRow.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/Views/Avalonia/Controls/StandardTrackRow.axaml)
- **Verified Badge**: Add `IsCompleted` to MultiBinding (Safety + Integrity).
- **Vibe Pill**: Implement **Skeleton State** (Neutral Grey + Pulse) when `!IsCompleted` instead of hiding. Burn transition to "Active Vibe" using `Transitions`.
- **Technical Summary**: Use `DataTrigger` in styles for Italics (view logic).

### Views
#### [MODIFY] [DownloadsPage.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/Views/Avalonia/DownloadsPage.axaml)
- **Rejection Tooltip**: Ensure `SearchScore` is visible (e.g. progress bar or "Matching: 45%").

## Phase 1.1: Brain Tuning (Smart Matcher)
### ViewModels
#### [MODIFY] [UnifiedTrackViewModel.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/ViewModels/Downloads/UnifiedTrackViewModel.cs)
- **Fix "Unknown failure"**:
    - Update `FailureDisplayMessage` to return "Search Rejected" if `HasRejectionDetails` is true and `FailureEnum` is `None/Unknown`.

### Services
#### [MODIFY] [SearchResultMatcher.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/Services/SearchResultMatcher.cs)
- **Relax Artist Matching**:
    - Normalize strings (remove "The", replace "feat", etc.) before partial match.
    - Use word boundary checks to avoid false positives (e.g. "The Beat" in "The Beatles").
## Phase 0.10: Sync Library Folders [DONE]
### Models
#### [NEW] [LibraryFoldersChangedEvent.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/Models/LibraryFoldersChangedEvent.cs)
- Simple event class to notify when library folders are added or removed.

### ViewModels
#### [MODIFY] [SettingsViewModel.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/ViewModels/SettingsViewModel.cs)
- Inject `IDbContextFactory<AppDbContext>` (or `DatabaseService` / `ServiceProvider` if factory not available).
- Inject `IEventBus`.
- Expose `ObservableCollection<LibraryFolderViewModel> LibraryFolders`.
- Initialize from DB.
- Add `AddLibraryFolderCommand` (async).
- Add `RemoveLibraryFolderCommand` (async).
- Subscribe to `LibraryFoldersChangedEvent` -> Reload Folders.

#### [MODIFY] [LibrarySourcesViewModel.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/ViewModels/LibrarySourcesViewModel.cs)
- Inject `IEventBus`.
- Publish `LibraryFoldersChangedEvent` after Add/Remove.
- Subscribe to `LibraryFoldersChangedEvent` -> Reload Folders (to sync changes from Settings).

## Phase 1.2: Multicore Optimization (Hybrid CPU Support)

### Goal
Detect Hybrid Architectures (Intel 12th Gen+, etc.) to distinguish between Performance (P) and Efficiency (E) cores. Scale analysis workload to avoid system stutter (overstressing P-Cores) or maximize background efficiency (using E-Cores).

### Proposed Changes

#### [MODIFY] [SystemInfoHelper.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/Services/SystemInfoHelper.cs)
- Implement `GetCpuTopology()` using `GetLogicalProcessorInformationEx` (P/Invoke) or heuristics.
- Add `CpuTopology` struct (P-Core Count, E-Core Count).
- Update `GetOptimalParallelism` to account for "Eco Mode" (Target E-Cores only) vs "Performance" (P+E).

#### [MODIFY] [AnalysisQueueService.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/Services/AnalysisQueueService.cs)
- Update `AnalysisWorker` to poll `GetOptimalParallelism` more effectively.
- **Fix**: The `SemaphoreSlim` is currently static. Replace with a dynamic throttle check (e.g. `while active > currentMax wait`).
- **[ENHANCEMENT] Pressure Monitor**: If system-wide CPU > 80%, reduce workers temporarily.
- Pass `ProcessPriorityClass` to `EssentiaAnalyzerService`.

#### [MODIFY] [EssentiaAnalyzerService.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/Services/EssentiaAnalyzerService.cs)
- Accept `ProcessPriorityClass` in `AnalyzeTrackAsync`.
- Apply `ProcessPriorityClass.Idle` for Eco Mode (Forces E-Cores on Windows 11).
- Apply `ProcessPriorityClass.BelowNormal` for Balanced.

### Verification Plan
- **Manual Test**: Run on User's Machine (if Hybrid).
- **Check Task Manager**: Verify `essentia.exe` priority and core affinity (Eco mode should show leaf icon).
