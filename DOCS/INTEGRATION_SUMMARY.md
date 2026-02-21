# Sidebar and Like Feature Integration

## Summary
The application has undergone a significant architectural shift by promoting the **Contextual Sidebar** to a global shell component and moving its implementation into a dedicated `Features/LibrarySidebar` module. Additionally, the "Like" functionality has been integrated from the UI down to the database persistence layer.

## Architectural Changes
- **Module Relocation**: Sidebar ViewModels and Views moved from generic `ViewModels/Sidebar` and `Views/Avalonia/Sidebar` to `Features/LibrarySidebar`.
- **Global Integration**: `ContextualSidebarViewModel` is now managed by `MainViewModel`, allowing any part of the application (Library, Search, Playlists) to push track selections to the sidebar via an `IObservable` selection stream.
- **Like persistence Loop**:
    - `PlayerViewModel.ToggleLikeCommand` triggers the update.
    - `LibraryService.UpdateLikeStatusAsync` handles the business logic and cache invalidation.
    - `TrackRepository.UpdateLikeStatusAsync` persists the `IsLiked` status to the SQLite database.
    - UI feedback is provided via a data-bound heart icon in `PlayerControl.axaml`.

## Known Stubs
- **Similarity Module**: The UI is wired and the ViewModel structure exists, but the `SonicMatchService` logic (using Energy/BPM/Acoustic features) is still a stub to be implemented in Phase 2.3.

## Resource Management Note
- **AnalysisWorker**: I identified that `AnalysisQueueService` uses `System.Diagnostics.PerformanceCounter`, which should be disposed. An `IDisposable` pattern needs to be implemented.
