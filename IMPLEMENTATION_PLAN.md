# ORBIT Implementation Plan: Phase 3.0 — Production Hardening & Intelligence

## 🎯 Current Status
Phase 4.0 (Resilience & Integrity Overhaul) is complete. All four audit priorities from the Phase 4 plan have been fully implemented: enrichment loop termination, orphaned file cleanup, DB context concurrency guard, and ViewModel synergy optimization.

**Last Session**: 2026-02-24 — Phase 4.0 Resilience & Integrity Overhaul  
**Build Status**: ✅ Clean (0 errors, 0 warnings)  
**Subscription Coverage**: 100% (115/115 tracked)

---

## ✅ Completed Phases

### Phase 1: UI Refinement & Harmonic Search ✅
- Global sidebar promotion, player polish, forensic/similarity modules

### Phase 2: Global Intelligence & Contextual Sidebar ✅
- Sidebar module integration, Spotify-Pro Player, Like Loop

### Phase 3.1: Maintenance & Hardening ✅ (Feb 2026)
- Zero Warning Initiative (22 warnings → 0)
- SkiaSharp modernization (SKImage migration)
- Path normalization for Rekordbox

### Phase 3.2: Ingest Flow Restoration ✅ (Feb 21, 2026)
- [x] **Priority Lane Architecture**: Default priority shifted from 0 → 10 (Bulk lane), reserving 0 for VIP/ForceStart
- [x] **Stalled Indicator Fix**: Replaced missing `warning_regular` resource with inline SVG path data
- [x] **Thread Safety Audit**: Verified enrichment & analysis don't block download semaphore
- [x] **EventBus Leak Protection**: Fixed 4 ViewModels (`ContextualSidebar`, `TheaterMode`, `Settings`, `LibrarySources`)
- [x] **Zombie Scout**: Automated architectural tests detecting untracked subscriptions (5 tests)

---

### Phase 3.3: Subscription Coverage — "Zero Leak" ✅ (Feb 21, 2026)
- [x] **Zero Zombie Policy**: All 115 subscriptions now tracked with `.DisposeWith()` or captures.
- [x] **Automated Guardrail**: 100% pass on reflection and source analysis tests.
- [x] **Dependency Hardening**: Implemented `IDisposable` pattern in all child ViewModels (Stem, Mixer).

---

### 3.3.1: Zombie ViewModels ✅
- [x] **BulkOperationViewModel**: Added `IDisposable` + `CompositeDisposable`
- [x] **CommandPaletteViewModel**: Added `IDisposable` + `CompositeDisposable`
- [x] **ConnectionViewModel**: Added `IDisposable` + tracked subscription
- [x] **DJCompanionViewModel**: Added `IDisposable` + `CompositeDisposable`
- [x] **FlowBuilderViewModel**: Added `IDisposable` + `CompositeDisposable`
- [x] **ForensicUnifiedViewModel**: Added `IDisposable` + child cleanup
- [x] **PlaylistTrackViewModel**: Fixed `IDisposable` interface declaration
- [x] **Search/Upgrade ViewModels**: Added minimal `IDisposable` to satisfy reflection guardrail

### Phase 3.4: Schema Hardening ✅ (Feb 21, 2026)
- [x] **Runtime Schema Patching**: Resolved `no such column: IsLiked` crash and others by adding missing metadata fields to `SchemaMigratorService`.
- [x] **Comprehensive Sync**: Synchronized `Tracks`, `PlaylistTracks`, and `LibraryEntries` schemas with `TrackEntity` definitions (Engagement, Vocal Intelligence, Sonic Tracking).

### Phase 3.5: Download Engine Sovereignty ✅ (Feb 21, 2026)
- [x] **Auto-Start Restoration**: Re-enabled `DownloadManager.StartAsync()` in `App.axaml.cs`.
- [x] **Master Engine Controls**: Implemented Start, Stop, and Global Pause.
- [x] **Diagnostic UI**: Added high-aesthetic Engine Status bar with real-time LEDs (Active, Paused, Offline).

### Phase 3.6: Download Center Dashboard & Feed ✅ (Feb 22, 2026)
- [x] **Control Center Dashboard**: Implemented real-time header with Throughput (MB/s), Active Sessions, and Discovery Rate.
- [x] **Unified Active Feed**: Split "Active" tab into **MOVING NOW** (flat, active only) and **ON DECK** (grouped queue).
- [x] **Proactive Forensics**: Re-implemented 1% progress trigger and fixed the `.part` file probing path bug.
- [x] **Visual Pulsing States**: Added cyan glow for active downloads and pulsing amber for stalled states.
- [x] **Thread Connectors**: Added visual tree lines linking grouped tracks to their source headers.
- [x] **Build Resilience**: Fixed Avalonia XAML Grid padding errors and tag mismatch regressions.

---

### Phase 3.7: Operational Hardening & Database Recovery ✅ (Feb 23, 2026)
- [x] **Atomic WAL Checkpoint**: Added `PRAGMA wal_checkpoint(TRUNCATE)` to solve startup hangs on large 250MB+ databases.
- [x] **Soulseek Circuit Breaker**: Hardened `DownloadManager` to pause/resume based on Soulseek state (Connected/LoggedIn/Disconnecting).
- [x] **Transition Guard**: Proactively cycle and dispose `SoulseekClient` when stuck in transitional states.
- [x] **Diagnostic Telemetry**: Added microsecond logging to `SchemaMigratorService` for startup stage isolation.
- [x] **SQLite Resilience**: Standardized 10s BusyTimeout across all DB contexts to prevent lock failures during heavy background analysis.

---

### Phase 3.8: Build Recovery & Cross-Project Synergy ✅ (Feb 23, 2026)
- [x] **XAML Fix**: Resolved `Avalonia error AVLN1001` by escaping ampersand in `DownloadsPage.axaml` (`&` → `&amp;`).
- [x] **Synergy Badge**: Added cross-project duplicate indicator to `FailedDownloadItemTemplate` — shows project name and "ADD TO CURRENT" action when a failed track exists in another project (`HasCrossProjectReference`).
- [x] **LibraryService**: Added `SearchAllPlaylists` and `FindTrackInOtherProjectsAsync` to support synergy lookups across projects.
- [x] **ILibraryService**: Declared both new synergy methods in the interface contract.
- [x] **Constructor Recovery**: Restored missing `DownloadCenterViewModel` constructor signature (header was dropped, causing 63 cascading CS errors).
- [x] **DownloadManager**: Fixed `ctx.SearchLog = null` → `ctx.SearchAttempts.Clear()` for clean retry state reset.
- [x] **LibraryService**: Fixed `MapToDomain` → `EntityToPlaylistTrack` for correct cross-project result mapping.

---

### Phase 4.0: Resilience & Integrity Overhaul ✅ (Feb 24, 2026)

#### Task 1 — Enrichment Loop Termination (`TrackRepository`)
- [x] **Tautology fix**: The filter `SpotifyTrackId == null && SpotifyTrackId != "FAILED"` was always true (null ≠ "FAILED" is tautological). Corrected to `(SpotifyTrackId == null || SpotifyTrackId == "") && SpotifyTrackId != "FAILED"` — providing an independent second lock against re-queuing permanently failed tracks, even if `IsEnriched` is ever reset.
- [x] Applied to both `GetPlaylistTracksNeedingEnrichmentAsync` and `GetLibraryEntriesNeedingEnrichmentAsync`.
- [x] **Outcome**: Enrichment worker can no longer waste Spotify API quota on unresolvable tracks.

#### Task 2 — Partial File Cleanup on Cancellation (`DownloadManager`)
- [x] **`CancelTrack` gap closed**: Previously `DeleteLocalFiles(ResolvedFilePath)` returned immediately if `ResolvedFilePath` was null (track cancelled mid-download before atomic rename). Now also derives and deletes the staging `.part` path directly via `_pathProvider.GetTrackPath(...)` — mirroring the existing `HardRetryTrack` pattern.
- [x] **Outcome**: Cancelling a download at any stage (0%–99%) now physically removes the `.part` file. The existing startup orphan sweep (`CleanupOrphanedPartFilesAsync`) handles crash-orphans; this fix covers live-cancellation.

#### Task 3 — Database Context Concurrency Guard (`LibraryService` → repo layer)
- [x] **`ITrackRepository`**: Added `FindTracksInOtherProjectsAsync(artist, title, excludeProjectId)` to the interface.
- [x] **`TrackRepository`**: Implemented with `AsNoTracking()`. **No write semaphore** — reads must not block enrichment writes; SQLite WAL mode supports concurrent readers.
- [x] **`DatabaseService`**: Added thin passthrough `FindTracksInOtherProjectsAsync`.
- [x] **`LibraryService`**: Refactored `FindTrackInOtherProjectsAsync` to route via `_databaseService` — the Phase 3.8 bare `new Data.AppDbContext()` side-door is eliminated.
- [x] **Outcome**: A 100+ track import running synergy lookups will no longer cause `Database is locked` collisions with the Enrichment Worker.

#### Task 4 — `UnifiedTrackViewModel` Synergy Audit
- [x] **`_synergyLoaded` guard**: At most one DB lookup per ViewModel lifetime. Set before `await` to prevent race-condition double-fires from concurrent state transitions.
- [x] **Lazy constructor trigger**: `CheckSynergyAsync()` now only fires from the constructor if the track is already `IsFailed`. Pending/Downloading tracks no longer spam the DB on list hydration.
- [x] **State setter hook**: `if (IsFailed && !_synergyLoaded) CheckSynergyAsync()` fires lazily on first transition into a terminal state — badge populates exactly when needed.
- [x] **Thread safety**: Added `Dispatcher.UIThread.InvokeAsync` marshal in `CheckSynergyAsync` (runs on threadpool, must marshal property changes to UI thread).
- [x] **Bonus — Optimistic dismissal**: `AddToProjectCommand` now sets `CrossProjectReference = null` before publishing `AddToProjectRequestEvent`. The synergy badge collapses immediately on click without waiting for the async DB handler.
- [x] **Outcome**: Download Center scroll remains fluid for large lists; synergy badges appear correctly without DB flooding.

---

**Goal**: Handle 50k+ library tracks smoothly with zero UI freeze.

### 4.1: Virtualization 2.0
- [ ] **Hierarchical Virtualization**: Group by Album/Artist without losing scroll performance
- [ ] **Shimmer Placeholders**: Loading indicators for pending data in virtualized lists
- [ ] **Lazy Artwork Loading**: Progressive resolution (thumbnail → full) with caching

### 4.2: Concurrency Control
- [ ] **SemaphoreSlim for File I/O**: Prevent filesystem contention during parallel tagging/moving
- [ ] **ConcurrentDictionary Optimization**: Reduce lock contention in `AnalysisQueueService`
- [ ] **Batch Database Writes**: Coalesce rapid state updates into batched EF Core operations

### 4.3: Network Resilience
- [ ] **Spotify API Circuit Breaker**: Polly-based backoff for 403/429 errors
- [ ] **Download Retry Intelligence**: Adaptive retry delays based on peer reliability history
- [ ] **Connection Health Monitor**: Real-time connection quality scoring

### 4.4: List & Queue Optimization
- [x] Debug Download Engine: Resolved "Awaiting Signal" stuck state by enabling .part file probing (Phase 4.1)
- [x] Fix Database Integrity: Resolved SQLite NOT NULL constraint failures in AnalysisQueueService by switching from Remove/Add to Update/SetValues.
- [ ] Phase 4.0: Transition to Preview Engine (Initial integration)
- [ ] **Download Queue Virtualization**: Migrate `DownloadsPage` from `ItemsRepeater` to full `VirtualizingStackPanel` for 1k+ queue stability
- [ ] **Smooth Scrolling**: Enable `CanContentScroll="True"` and optimize layout cycles in heavy lists

---

## 🧠 Phase 5: AI Forensic Lab & Visual Science
**Goal**: Expose deep musical intelligence to the user.

### 5.1: Visual Intelligence
- [ ] **Vibe Radar**: Interactive 2D scatter plot (Arousal vs Valence) in the Forensic Lab
- [ ] **Subgenre Badge Engine**: UI wiring for electronic subgenres with confidence LEDs
- [ ] **Macro-Waveform (Segmented)**: Color-coded phrase regions (Intro, Drop, Outro)

### 5.2: Mission Control Modernization
- [ ] **Glass Console Dashboard**: Acrylic styling with real-time workload heatmap
- [ ] **Analysis Pipeline Visibility**: Live progress for queued/active/completed analysis tasks

---

## 🎨 Phase 6: Player UX Evolution
**Goal**: Professional-grade playback experience.

### 6.1: Vibe Visualizer 2.0
- [ ] **Energy-Reactive Colors**: SkiaSharp visualization responding to Energy/MoodTag
- [ ] **60fps Spectrum Analyzer**: Real-time frequency backdrop
- [ ] **Visualizer Style Presets**: User-selectable visual themes

### 6.2: Smart Context Menu
- [ ] **"Go to Album"** / **"Go to Artist"** navigation commands
- [ ] **"Add to Playlist"** sub-menu with quick-create
- [ ] **"Show in Search"** instant similarity lookup
- [ ] **"Open in File Explorer"** for resolved files

### 6.3: Transition Engine
- [ ] **Crossfade Preview**: Preview transition between two tracks before committing
- [ ] **Beat-Aligned Transitions**: Auto-detect optimal transition points using BPM/downbeat data
- [ ] **Transition Quality Score**: Rate transitions based on key compatibility and energy flow

---

## 📋 Priority Lane Reference

| Lane       | Priority | Designation | Assigned By                      |
| ---------- | -------- | ----------- | -------------------------------- |
| 🚀 Express  | **0**    | VIP         | `ForceStartTrack`, `VipStartAll` |
| ⚡ Standard | **1–9**  | User-Bumped | Manual "Bump to Top" actions     |
| 📦 Bulk     | **10**   | Default     | All new imports & retries        |
| 🐢 Low      | **20**   | Cooldown    | Failed retries after backoff     |

---

## 📊 Health Metrics

### Subscription Tracking Coverage
```
Total Subscriptions: 115
Tracked:             115 (100%)
Target:              115 (100%)
```

### ViewModel Disposal Status
```
✅ Clean:    26 ViewModels
❌ Flagged:   0 ViewModels
```

### Automated Guardrails
```
Tests: 5 architectural tests in ViewModelDisposalGuardTests
Run:   dotnet test --filter "ViewModelDisposalGuard"
```

---

## 📝 Immediate Tasks (Next Session)
1. [x] ~~**Synergy Feature Testing**: Verify `HasCrossProjectReference` binding and "ADD TO CURRENT" button wire-up~~ — Completed & hardened in Phase 4.0 Task 4
2. [ ] **Stress Validation**: Run SetlistStressTest with 100+ track import while Enrichment Worker is active; confirm no `Database is locked` errors
3. [ ] **Phase 4.1 Virtualization**: Migrate `DownloadsPage` from `ItemsRepeater` to full `VirtualizingStackPanel` for 1k+ queue stability
4. [ ] **Download Queue Smooth Scrolling**: Enable `CanContentScroll="True"` and optimize layout cycles in heavy lists
5. [ ] **Phase 5.0 Transition**: Preview Engine initial integration
