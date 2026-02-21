# ORBIT Implementation Plan: Phase 2.0 & Beyond

## 🎯 Current Status
We have successfully completed Phase 1 (UI Refinement & Harmonic Search) and promoted the **Contextual Sidebar** to the global `MainViewModel`. The application is now ready for deep feature integration and stability hardening.

---

## 🏗️ Phase 2: Global Intelligence & Contextual Sidebar (Current)
**Goal**: Transform the sidebar from a simple container into a proactive "Intelligence Hub" that stays relevant across all pages.

### 2.1: Sidebar Module Integration
- [x] **Global Promotion**: Move `ContextualSidebarViewModel` to `MainViewModel`.
- [x] **Player Module**: Display the `PlayerControl` within the sidebar by default.
- [x] **Forensic Module**: Wire the `ForensicSidebarViewModel` to the selected track app-wide (Library, Search, Playlists).
- [x] **Similarity Module**: Wire UI but implement `SonicMatchService` logic (Phase 2.3).
- [x] **Metadata Module**: Allow quick tag editing (BPM, Key, Energy) directly from the sidebar.
- [x] **Bulk Action Module**: Show multi-select operations (Download All, Analyze All) when multiple tracks are selected in any view.

### 2.2: The "Spotify-Pro" Player Polish
- [x] **Waveform Control**: Centered playhead and click-to-seek.
- [x] **Hardware Controls**: Pitch (90%-110%) and WASAPI low-latency output.
- [ ] **The "Like" Loop**:
    - [x] Implement `ToggleLikeCommand` in `PlayerViewModel`.
    - [x] Persistence: Save "Liked" status to the physical file (Tag) and database.
    - [x] UI: Pulsing heart animation on success.
- [ ] **Vibe Visualizer 2.0**:
    - [ ] Refine `VibeVisualizer` (SkiaSharp) to react to `Energy` (color intensity) and `MoodTag` (vibe patterns).
    - [ ] Add 60fps spectrum analyzer backdrop.
- [ ] **Smart Context Menu**:
    - [ ] "Go to Album" / "Go to Artist" commands.
    - [ ] "Add to Playlist" sub-menu.
    - [ ] "Show in Search" (instant similarity lookup).

---

## ⚡ Phase 3: Stability & Performance ("Zero Lag" Initiative)
**Goal**: Address critical technical debt identifying during the Feb 2026 audit.

### 3.1: Scalability & Memory
- [ ] **Virtualization 2.0**: 
    - [ ] Implement hierarchical virtualization for the Library (grouping by Album/Artist without losing scroll performance).
    - [ ] Shimmer loading placeholders for pending data.
- [ ] **Resource Lockdown**:
    - [ ] **AnalysisWorker Cleanup**: Implement `IDisposable` pattern for FFmpeg/Essentia unmanaged handles.
    - [ ] **Memory Leak Audit**: Verify `EventBus` unsubscriptions in all long-lived ViewModels.
- [ ] **Concurrency Control**:
    - [ ] Optimize `ConcurrentDictionary` usage in `AnalysisQueueService`.
    - [ ] Implement `SemaphoreSlim` for file-system intensive operations (Tagging, Moving).

### 3.2: Network Resilience
- [ ] **Spotify API Circuit Breaker**: implement Polly-based backoff for 403/429 errors.
- [x] **Soulseek Heartbeat**: Automatic reconnection logic with exponential backoff and "Offline" UI mode.

---

## 🧠 Phase 4: AI Forensic Lab & Visual Science
**Goal**: Expose the deep musical intelligence of the "Brain" to the user.

### 4.1: Visual Intelligence
- [ ] **Vibe Radar**: Interactive 2D scatter plot (Arousal vs Valence) in the Forensic Lab.
- [ ] **Subgenre Badge Engine**:
    - [ ] UI wiring for electronic subgenres (DnB, Techno, Melodic House).
    - [ ] "Match Confidence" LEDs for AI detections.
- [ ] **Macro-Waveform (Segmented)**:
    - [ ] Color-code Waveform regions by phrase (Intro, Drop, Outro) using AI detection.

### 4.2: Mission Control Modernization
- [ ] **Glass Console Dashboard**:
    - [ ] Port the analysis dashboard to `ExperimentalAcrylicBorder` styling.
    - [ ] Real-time "Workload Heatmap" showing CPU thread distribution.

---

## 📝 Getting Started (Immediate Tasks)
1. **Vibe Visualizer 2.0**: Implement SkiaSharp-based energy reactiveness in the player backdrop.
2. **Smart Context Menu**: Add "Show in Search" and "Go to Album" commands to the Library grid.
3. **Spotify Resilience**: Integrate Polly policies for `SpotifyMetadataService` rate limiting.
