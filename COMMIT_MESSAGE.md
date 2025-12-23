# Commit Message

## Title
fix: Library UI Update Synchronization & Synchronization Logic

## Description

### Library Persistence & UI Sync
- **Synchronized UI Lists**: Fixed critical bug where `ProjectListViewModel` updated the master project list (`AllProjects`) but failed to notify the filtered view (`FilteredProjects`), causing new imports to appear missing until restart.
- **Smart Filtering**: Implemented logic in `OnPlaylistAdded` to respect active search filters—new imports matching the filter appear immediately; non-matches are added to the database silently.
- **Immediate Deletion**: Updated `OnProjectDeleted` to ensure projects are removed from both master and filtered lists instantly.
- **Thread Safety**: Wrapped all observable collection modifications in `Dispatcher.UIThread.InvokeAsync` to prevent cross-thread exceptions from background event handlers.

### Spotify Robustness (The Three Laws)
- **Law 1 (Chunking)**: Implemented `SpotifyBulkFetcher` with strict chunking (50 for tracks, 100 for features) to prevent 400 Bad Request errors.
- **Law 2 (Two-Pass Fetch)**: Architecture now separates initial track import from metadata enrichment, merging results efficiently.
- **Law 3 (Retry Mandate)**: Configured global `SimpleRetryHandler` with `Retry-After` respect (1s default, 3 retries) in `SpotifyAuthService` and `SpotifyInputSource`.

### Unified Import Pipeline Fixes
- **Unified Streaming**: Refactored `ImportOrchestrator` to use streaming for all providers (Spotify, CSV).
- **Liked Songs Fix**: Implemented `IStreamingImportProvider` for `SpotifyLikedSongsImportProvider`, resolving crashes/silent failures in the new orchestrator.
- **Album Support Added**: Extended `SpotifyInputSource` (API) to handle `spotify:album:` and `/album/` URLs natively.
- **Input Source Optimization**: Added implicit limits (50/100) to `SpotifyInputSource` pagination.

### Files Modified
- `ViewModels/Library/ProjectListViewModel.cs`
- `Services/SpotifyBulkFetcher.cs`
- `Services/InputParsers/SpotifyInputSource.cs`
- `Services/ImportProviders/SpotifyLikedSongsImportProvider.cs`
- `Services/SpotifyAuthService.cs`
- `Services/MetadataEnrichmentOrchestrator.cs`
- `App.axaml.cs`

---

## Git Commands to Run

```bash
git add .
git commit -F COMMIT_MESSAGE.md
git push
```
