# Recent Changes - December 28, 2025 (Evening Session)

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
