# Recent Changes

## [0.1.0-alpha.9.1] - Library UI Customization (Jan 21, 2026 - Latest)

### New Features
* **Column Configuration**: Save/restore column layout, width, visibility, and sort order to `%APPDATA%/ORBIT/column_config.json`.
* **Default Columns**: Status, Artist, Title, Duration, BPM, Key, Bitrate, Format, Album, Genres, Added date.
* **Reactive Persistence**: Debounced (2s) auto-save on column changes via Rx throttling.
* **Schema Backup**: SchemaMigratorService handles auto-backup rotation (keep last 5), force-reset markers, and patching.

## [0.1.0-alpha.9] - Stem Workspace & Smart Crates (Jan 21, 2026)

### New Features
* **Stem Workspace**: Real-time stem separation and mixing powered by ONNX/Spleeter with new Stem Mixer, Channel, and Waveform views.
* **Smart Playlists & Smart Crates**: Rule-based playlist/crate builder with new dialogs, criteria models, and crate definitions.
* **Intelligence Center**: Central AI hub with Sonic Match (TensorFlow model pool) and telemetry cards for vibe insights.
* **Hardware Export**: New export service for Rekordbox/USB workflows with metadata mapping.
* **Library Sources**: Folder management UI for scanning/refreshing library sources.

### Improvements
* **Library Virtualization v2**: Virtualized track collection for large libraries, smoother scrolling, and better caching.
* **Bulk Operations**: Coordinator service plus modal to track long-running bulk tasks.
* **Cue Generation**: Phrase detection + genre-aware cue templates with Serato/Universal cue writers.

## [0.1.0-alpha.8] - Brain Tuning & Multicore (Jan 15, 2026)

### New Features
* **Brain Tuning (Phase 1.1)**: 0-100 weighted scoring, path-aware extraction, quick-strike downloads, and forensic tooltips.
* **Multicore Optimization (Phase 1.2/1.3)**: Parallel analysis with performance metrics UI and hardware telemetry.
* **Search Rejection UI**: Dedicated rejection diagnostics surfaced in Analysis Queue and Search pages.

### Fixes & Stability
* Improved SystemInfo hardware detection, parallel worker safety, and download discovery resilience.
* Refined SearchResultMatcher scoring and SonicIntegrityService safeguards.

## [0.1.0-alpha.6] - Sonic Visualizations (Phase 18.2)

### New Features
* **Sonic Profile UI**: Added `SonicProfileControl` to visualize track energy (Arousal) and mood (Valence).
  * **Energy Battery**: Gradient bar showing intensity from Chill (Blue) to Banger (Red).
  * **Mood Slider**: Bi-directional indicator for Melancholic vs. Euphoric vibes.
  * **Vocals Icon**: Indicator for Instrumental vs. Vocal tracks.
* **Track Inspector**: Integrated Sonic Profile into the inspector view.
* **Smart Playlists**: Updated creation dialog to use visual sliders for vibe selection.

### Improvements
* **SmartPlaylistService**: Refactored to ReactiveUI and removed CommunityToolkit.Mvvm dependency.
* **Build System**: Fixed duplicate command definitions and restored .NET 9.0 build health.

## [0.1.0-alpha.5] - Analysis & Inspector Update

### New Features
* **Analysis Queue Dashboard**: New page to monitor background audio analysis tasks.
  * View pending vs. processed track counts.
  * Pause/Resume analysis to save CPU usage during gaming.
  * "Stuck File" watchdog automatically skips files that take longer than 60s.
* **Track Inspector Enhancements**:
  * **Re-fetch / Upgrade**: New button to force re-analysis of a track.
  * **Forensic Logs**: View detailed logs of why a download was rejected or modified.
* **Download Manager**:
  * **Smart Deduplication**: Improved logic to prevent duplicate queue items.

### Fixes
* **Memory Leak**: Fixed DbContext leak in background analysis worker.
* **Navigation**: Fixed Analysis Queue page not appearing when clicked.
* **UI**: Fixed visibility issues in Track Inspector empty state.
* **Performance**: Download queue now uses dictionary lookups for faster deduplication.

 - December 28, 2025 (Evening Session)

## 🚀 Major Features

### 1. Analysis Queue Status Bar
**Value**: Real-time observability into the audio analysis pipeline.
- **UI**: Added a professional status bar to the bottom of the MainWindow.
- **Metrics**: Shows "Analyzing...", Pending Count, Processed Count, and a green "Active" pulse.
- **Tech**: Built using `RxUI` (ReactiveUI) event streams via `AnalysisQueueStatusChangedEvent`.

### 2. Album Priority Analysis
**Value**: User control over what gets analyzed first.
- **Feature**: Right-click any track in the Library -> **"🔬 Analyze Album"**.
- **Effect**: Immediately queues all *downloaded* tracks from that album with high priority.
- **Feedback**: Shows a toast notification confirming the number of tracks queued.

### 3. Track Inspector Overhaul
**Value**: Forensic-grade detail for audio files.
- **Hero Section**: Large album art, clear metadata, and live status badges.
- **Metrics Grid**: "Pro Stats" layout for tech data (Bitrate, Sample Rate, Integrity).
- **Forensic Logs**: Collapsible timeline of exactly what happened during analysis.
- **Interactive**: 
    - `Force Re-analyze`: Wipes cache and re-runs pipeline.
    - `Export Logs`: Saves analysis details to text file.
- **Fixes**: Resolved runtime crash caused by invalid CSS gradient syntax.

## 🛠 Technical Improvements

- **Status Bar Architecture**: Created `StatusBarViewModel` to decouple status logic from `MainViewModel`.
- **Service Layer**: Enhanced `AnalysisQueueService` with `QueueAlbumWithPriority` method.
- **Stability**: Fixed build errors in `LibraryViewModel` (Enum types, Property access).
- **Cleanup**: Restored correct `MainWindow.axaml` grid structure (3 rows).

## 📝 Configuration Updates

- **Dependencies**: No new NuGet packages added.
- **Database**: No schema changes required (uses existing indices).
## [0.1.0-alpha.6] - Unified UI & Build Stability

### New Features
* **Unified Command Bar**: A single, sleek top bar replaces the split top/bottom layout.
  * **Global Activity Indicator**: Centralized spinner for all background tasks.
  * **Status & Telemetry**: Combined download, upload, and analysis stats in one view.
  * **Optimized Layout**: Increased vertical space for the main library view.
* **Flexible Player**: Added "Dock to Bottom" vs "Sidebar" toggle (Internal logic ready).

### Fixes & Stability
* **Build Restoration**: Resolved 13+ compilation errors to restore `net9.0` build.
  * Fixed `IntegrityLevel` enum mismatches (Suspicious/Verified).
  * Fixed `AnalysisProgressEvent` type conversion errors.
  * Fixed missing fields in `AnalysisWorker` (`_queue`) and `DownloadDiscoveryService` (`_logger`).
* **Search Diagnostics**: Added `SearchScore` to `SearchAttemptLog` for better debugging.

### Cleanup
* **Dependency Removal**: Removed unused `LibVLC` packages (`LibVLCSharp`, `LibVLCSharp.Avalonia`, `VideoLAN.LibVLC.Windows`) to reduce build size and complexity.
## [0.1.0-alpha.7] - Intelligence & Context Mastery

### New Features
* **Analysis Context Menus**:
  * **"Analyze Track"**: Right-click any track in the Library (flat list) to queue it for immediate priority analysis.
  * **"Analyze Album"**: Right-click any Album Card in the Library (hierarchical view) to queue the entire album for analysis.
* **Musical Brain Test Mode**: Added a diagnostic utility to the Analysis Queue page to validate the entire processing pipeline (FFmpeg, Essentia, concurrent execution).

### Fixes & Stability
* **Startup Stability**: Fixed a critical `InvalidOperationException` (DI Resolution) that prevented application startup due to missing `AppDbContext` registration in `MusicalBrainTestService`.
* **LINQ Translation**: Fixed a runtime crash in track selection where `File.Exists` was used inside a database query.
* **Build Fixes**:
  * Resolved ambiguous `NotificationType` references.
  * Fixed nullability mismatches in `SettingsViewModel` (Selection commands).
  * Added mandatory `ILogger` injection to `AnalysisQueueService`.
  * Added missing `QueueTrackWithPriority` method to `AnalysisQueueService`.
  * Added null safety check to `SafetyFilterService` for blacklisted users.

### Infrastructure
* **Database Access**: Refactored `MusicalBrainTestService` to use the "New Context per Unit of Work" pattern, ensuring database connection health in singleton services.

### Recent Updates (January 4, 2026) - Operational Resilience & Hardware Acceleration
* **Phase 0.1: Operational Resilience**:
  * **Atomic File Moves**: `DownloadManager` now uses `SafeWriteService` for final file writes, preventing 0-byte corruption on crash.
  * **Crash Journal**: Heartbeats are correctly decoupled from UI updates and properly stopped before finalization.
* **Phase 4: GPU & Hardware Acceleration**:
  * **FFmpeg Acceleration**: Enabled `-hwaccel auto` for spectral analysis (NVIDIA/AMD/Intel).
  * **Future-Proof ML**: Installed `Microsoft.ML.OnnxRuntime.DirectML` and added helper for future Deep Learning models.
  * **GPU Detection**: Updated `SystemInfoHelper` to centralize hardware capabilities.
* **January 8, 2026 - Analysis Navigation & UI Masterclass**:
  * **Workspace Restoration**: Re-implemented the missing "Right Panel" in `LibraryPage.axaml`, enabling the **Track Inspector** and **Mix Helper** sidebars in Analyst and Preparer modes.
  * **Mix Helper UI**: Created a new `MixHelperView` for real-time harmonic match suggestions in the sidebar.
  * **Forensic Lab Master**: Fixed the `ForensicLabDashboard` data binding and added a direct "Open in Forensic Lab" context menu option.
  * **Quick Look Upgrade**: Replaced the "Waveform Analysis Visualization" placeholder with a functional, high-fidelity `WaveformControl` in the Spacebar overlay.
  * **Infrastructure**: Corrected `ForensicLabViewModel` DI registration and updated workspace logic to automatically load the selected track when switching to Forensic mode.
