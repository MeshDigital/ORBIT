# SLSKDONET: Roadmap to v1.0

This roadmap outlines the strategic steps to elevate SLSKDONET from a functional prototype to a robust, "daily driver" application.

## 1. The Stability Layer: Persistence (Priority: 🔴 Critical)
**Current State**: The download queue is fully in-memory. Closing the app wipes the "Warehouse".
**The Plan**:
- **Database**: Integrate **SQLite** (lightweight, zero-config).
- **Migration**: Move `PlaylistTrackViewModel` state to persistent storage.
- **Lifecycle**: On app launch, restore all `Pending`, `Paused`, and `Failed` tracks.
- **History**: Keep a log of `Completed` downloads for user reference (and to prevent duplicate downloads).

## 2. The Missing Core: True Album Downloading (Priority: 🟠 High)
**Current State**: `SoulseekAdapter` finds album directories but doesn't process them. Users must pick tracks individually.
**The Plan**:
- **Directory Parsing**: Implement recursive file enumeration for `DownloadMode.Album`.
- **Grouping**: Update `LibraryViewModel` to group tracks by `Album` header.
- **Batch Logic**: Create `AlbumDownloadJob` that acts as a parent task for multiple files.

## 3. The Visual Experience: UI Polish (Priority: 🟡 Medium)
**Current State**: Functional, data-dense, text-heavy.
**The Plan**:
- **Album Art**: Integrate a metadata provider (e.g., Last.fm or Spotify API) to fetch cover art based on Artist/Album tags.
- **Styling**: Implement a consistent Design System (Colors, Typography) using `WPF UI` or modern styles.
- **Feedback**: Add toast notifications for "Download Complete" or "Search Finished".

## 4. Automation: The "Wishlist" (Priority: 🟢 Low/Future)
**Current State**: Manual Search -> Download flow.
**The Plan**:
- **Wishlist**: Users add "Artist - Album" to a watch list.
- **Sentinel**: A low-priority background worker searches periodically (e.g., every 30 mins).
- **Auto-Snatch**: Automatically queues items that meet strict criteria (e.g., 320kbps + Free Slot).

## 5. Performance Improvements
- **Virtualization**: Ensure `DataGrid` enables UI virtualization to handle 10,000+ items without lag.
- **Memory Management**: Optimize `ObservableCollection` updates (using `AddRange` patterns) to reduce UI thread thrashing.

---

## Recommended Next Step: 1. Persistence
Implementing SQLite persistence is the most high-impact change for user trust. It transforms the app from a "session tool" to a "library manager".
