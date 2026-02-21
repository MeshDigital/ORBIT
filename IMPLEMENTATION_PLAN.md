# ORBIT Implementation Plan: Phase 3.0 — Production Hardening & Intelligence

## 🎯 Current Status
Phase 2 (Global Intelligence & Contextual Sidebar) is complete. Phase 3.1 (Zero Warning Initiative, SkiaSharp, Path Normalization) is complete. Phase 3.2 (Ingest Flow Restoration & EventBus Leak Protection) is complete. The application now builds with **0 Warnings, 0 Errors** and has automated guardrails preventing future memory leaks.

**Last Session**: 2026-02-21 — Ingest Flow Restoration + Zombie Exorcism  
**Build Status**: ✅ Clean  
**Subscription Coverage**: 81% (93/115 tracked)

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

## 🏗️ Phase 3.3: Subscription Coverage — "Zero Leak" (Next Sprint)
**Goal**: Push subscription tracking from 81% → 100%. Fix the 5 remaining flagged ViewModels.

### 3.3.1: Remaining Zombie ViewModels
- [ ] **BulkOperationViewModel**: Add `IDisposable` + `CompositeDisposable`
- [ ] **CommandPaletteViewModel**: Add `IDisposable` + `CompositeDisposable`
- [ ] **ConnectionViewModel**: Add `IDisposable` + tracked subscription
- [ ] **DJCompanionViewModel**: Add `IDisposable` + `CompositeDisposable`
- [ ] **FlowBuilderViewModel**: Add `IDisposable` + `CompositeDisposable`

### 3.3.2: EventBus Hardening
- [ ] **Weak Reference Subscribers**: Evaluate adding `WeakReference<IDisposable>` to `EventBusService` internal subscriber list as a failsafe
- [ ] **Subscription Count Diagnostics**: Add `GetSubscriberCount<T>()` to `IEventBus` for runtime leak detection
- [ ] **CI Integration**: Add `dotnet test --filter "ViewModelDisposalGuard"` to build pipeline

### 3.3.3: Stress Validation
- [ ] **SetlistStressTest**: Run rapid sidebar open/close + theater mode toggle cycles
- [ ] **Memory Floor Check**: Verify GC heap doesn't climb after repeated VM lifecycle cycles
- [ ] **Timer Audit**: Confirm no orphaned `DispatcherTimer` instances after stress test

---

## ⚡ Phase 4: Scalability & Performance
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
Tracked:              93 (81%)
Target:              115 (100%)
```

### ViewModel Disposal Status
```
✅ Clean:    21 ViewModels
❌ Flagged:   5 ViewModels (Phase 3.3)
```

### Automated Guardrails
```
Tests: 5 architectural tests in ViewModelDisposalGuardTests
Run:   dotnet test --filter "ViewModelDisposalGuard"
```

---

## 📝 Immediate Tasks (Next Session)
1. **Phase 3.3**: Fix remaining 5 zombie ViewModels → push to 100% coverage
2. **CI Integration**: Add Zombie Scout to build pipeline
3. **Stress Validation**: Run SetlistStressTest with memory profiling
4. **Phase 4.1 Prep**: Audit current Library virtualization for 50k+ track readiness
