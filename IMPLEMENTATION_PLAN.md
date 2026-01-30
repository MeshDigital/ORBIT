# Phase 0.7: Unified Download UI

## Goal
Unify the visual design of the Download Center by applying the "Card" style (currently used in the Failed/Rejected queue) to Active and Completed downloads.

## Proposed Changes

### UI Layer

#### [MODIFY] [ProjectPickerDialog.axaml.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/Views/Avalonia/Controls/ProjectPickerDialog.axaml.cs)
- Add a public parameterless constructor to satisfy Avalonia's runtime/XAML loader requirements (fixes AVLN3001 warning).

---

### Performance & Stability

#### [MODIFY] [PlayerViewModel.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/ViewModels/PlayerViewModel.cs)
- **Debounce Queue Persistence**: Replace immediate `SaveQueueAsync` calls in `OnQueueCollectionChanged` with a debounced call (e.g., wait 500ms after last change) to prevent UI freezes during bulk additions (like enqueuing an album).
- **Asynchronous ANLZ Probing**: Move `AnlzFileParser.TryFindAndParseAnlz` and subsequent waveform generation checks to a background task in `PlayTrackAtIndex` to prevent blocking the UI thread during track transitions.
- **Bulk Addition Optimization**: Add a "suppress save" flag or use `AddRange` equivalents if possible (though `ObservableCollection` doesn't support it natively, we can wrap the bulk update).
- **Event Bus Subscription**: Ensure `AddToQueueRequestEvent` handler is efficient and doesn't trigger redundant updates.

#### [MODIFY] [AnalysisQueueService.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/Services/AnalysisQueueService.cs)
- **Dynamic Parallelism Tuning**: Consider lowering the default max threads or ensuring `AnalysisWorker` threads have `ProcessPriorityClass.Idle` by default to preserve UI responsiveness during heavy analysis bursts. (Note: It already has some logic for this, but could be more conservative).

---

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
- **Advanced CPU Detection**:
    - Implement `CpuTopology` struct to track P-Cores, E-Cores, and Total Threads.
    - Use **Heuristic/P-Invoke**: Identify P-Cores via SMT (2 threads) vs E-Cores (1 thread) or use `GetLogicalProcessorInformationEx`.
    - **[TIP]** Accept `CancellationToken` for long-running checks.
- Update `GetOptimalParallelism` to account for "Eco Mode" (Target E-Cores only) vs "Performance" (P+E).

#### [MODIFY] [AnalysisQueueService.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/Services/AnalysisQueueService.cs)
- **Dynamic Concurrency (Pressure Monitor)**:
    - Implement a background loop (every 2-5s) checking `PerformanceCounter` (System CPU).
    - **Throttling Logic**:
        - `CPU > 85%`: Reduce `MaxParallelism` (min 1).
        - `CPU < 50%`: Increase `MaxParallelism` (up to Optimal).
    - Use `Interlocked.Exchange` or a "Leaky Bucket" gate instead of static `SemaphoreSlim`.
- **Worker Optimization**:
    - "Check-in" before processing each track to get current `MaxParallelism` and Mode.
    - Pass `ProcessPriorityClass` to `EssentiaAnalyzerService`.

#### [MODIFY] [EssentiaAnalyzerService.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/Services/EssentiaAnalyzerService.cs)
- **Leaf Icon Trick**:
    - Accept `ProcessPriorityClass` priority.
    - **Eco Mode**: Use `ProcessPriorityClass.Idle` (triggers Windows Thread Director to enforce E-Core affinity).
    - **Balanced**: Use `ProcessPriorityClass.BelowNormal`.

### Verification Plan
- **Dashboard**: Add "CPU Topology" readout (e.g., "Hybrid (8P + 4E)") and "Active Workers".
- **System Stutter Test**: Run analysis while playing a video/game.
    - **Success**: Essentia threads stay on E-Cores (High Index cores) and UI/Video does not stutter.

## Phase 1.3: Hybrid Columnar List View (Refined Library UI)

### Goal
Implement a high-performance Columnar List View with perfectly aligned metadata (Artist, Title, BPM/Key) while maintaining a clean, professional aesthetic. Fix StyleFilters duplication and header overlap.

### Proposed Changes

#### ViewModels
- **[FIX] TrackListViewModel**: Deduplicate StyleFilters by Name in `LoadStyleFiltersAsync`. Add a loading guard.

#### Models
- **[MODIFY] ColumnDefinition**: Add `SharedSizeGroup` helper property.

#### Controls
- **[MODIFY] StandardTrackRow.axaml**:
    - Refactor Grid to use `SharedSizeGroup` for Artist, Title, and BPM/Key columns.
    - Consolidate 10+ metadata indicators into a compact "Badge Tray".
    - Align Vibe pills and other indicators for vertical consistency.

#### Views
- **[MODIFY] LibraryPage.axaml**:
    - Redesign header into two clean rows: Search/Actions and Filters.
    - Add a fixed Column Header row using `SharedSizeGroup` to provide alignment and sorting UI.
    - Set `Grid.IsSharedSizeScope="True"` to enable global columnar alignment.

### Verification Plan
1.  **StyleFilters**: Verify no duplicate AI chips appear in the toolbar.
2.  **Alignment**: Verify Artist/Title/BPM columns line up perfectly across different rows.
3.  **Search Bar**: Verify the search bar no longer overlaps action buttons and allows spaces.
4.  **Badges**: Verify metadata tray looks cleaner and less cluttered.

## Phase 1.4: Library Performance Optimization (Critical Fix)

### Goal
Resolve the "broken" performance of the library caused by `SharedSizeGroup` overhead in virtualized lists. Replace dynamic alignment with optimized fixed widths while ensuring "Card" and "Pro" views remain fully functional.

### Proposed Changes

#### [MODIFY] [LibraryPage.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/Views/Avalonia/LibraryPage.axaml)
- Remove `Grid.IsSharedSizeScope="True"` from the main layout.
- Replace `SharedSizeGroup` with fixed pixel widths in the Column Header row:
    - **Artist**: 220px
    - **Title**: 320px
    - **BPM/Key**: 100px

#### [MODIFY] [StandardTrackRow.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/Views/Avalonia/Controls/StandardTrackRow.axaml)
- Remove `SharedSizeGroup` from the row layout.
- Apply matching fixed pixel widths (220, 320, 100) to ensure perfect vertical alignment.

### Verification Plan
1.  **Load Speed**: Verify the library loads instantly and scrolling is smooth.
2.  **View Integrity**: Toggle between List, Cards, and Pro views to verify they are all intact and functional.
3.  **Alignment**: Verify that Artist, Title, and BPM/Key align perfectly with the header labels.

## Phase 1.5: Metadata Visibility Enhancements (Camelot Key & Tech Info)

### Goal
Improve the clarity and visibility of size and Camelot key information in the Library UI, as requested by the user. Transform keys into high-contrast harmonic pills and enhance technical metadata legibility.

### Proposed Changes

#### [MODIFY] [PlaylistTrackViewModel.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/ViewModels/PlaylistTrackViewModel.cs)
- Add `CamelotDisplay` property to return the Camelot code (e.g., "8A") using `KeyConverter`.
- Re-notify `CamelotDisplay` when `MusicalKey` changes.

#### [MODIFY] [StandardTrackRow.axaml](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/Views/Avalonia/Controls/StandardTrackRow.axaml)
- **Camelot Key Pill**:
    - Replace the raw `MusicalKey` TextBlock with a `Border` (Pill) using `ColorBrush` for background and `CamelotDisplay` for text.
    - Set `CornerRadius` to 4 and give it subtle padding for a professional "Rekordbox-style" look.
- **Technical Info Visibility**:
    - Increase the contrast of `TechnicalSummary` by changing its `Foreground` color to a lighter, more legible shade (e.g., `#888` or `#AAA`).
    - Add a subtle icon or vertical separator to give it more presence in the badge tray.

### Verification Plan
1.  **Camelot Keys**: Verify keys are displayed as colored pills that stand out.
2.  **Size/Tech Info**: Verify bitrate and file size are clearly readable and don't blend into the background.
3.  **Layout**: Ensure the new key pills don't break the columnar alignment.

## Phase 1.6: Musical Key Search Enhancements

### Goal
Allow users to search for tracks by musical key using either Standard (e.g., "Am") or Camelot (e.g., "8A") notation.

### Proposed Changes

#### [MODIFY] [SchemaMigratorService.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/Services/Data/SchemaMigratorService.cs)
- Update `TracksFts` and `LibraryEntriesFts` schema to include a `Key` or `MusicalKey` field.
- Update triggers to populate this field with both the raw key and its Camelot equivalent (e.g., "Am 8A").
- Ensure the seeding logic also incorporates both notations.

#### [MODIFY] [TrackListViewModel.cs](file:///c:/Users/quint/OneDrive/Documenten/GitHub/QMUSICSLSK/ViewModels/Library/TrackListViewModel.cs)
- Update the in-memory `FilterTracks` method to check the `MusicalKey` and `CamelotDisplay` properties when searching.

### Verification Plan
- **Standard Search**: Search for "Am" and verify results include A Minor tracks.
- **Camelot Search**: Search for "8A" and verify results include the same A Minor tracks.
- **Mixed Search**: Search for "9B" and verify tracks with key "G" appear.


