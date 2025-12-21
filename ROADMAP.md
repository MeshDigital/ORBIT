# ORBIT (formerly SLSKDONET): Current Status & Roadmap

**Last Updated**: December 17, 2025  
**Repository**: https://github.com/MeshDigital/ORBIT

---

## ✅ Recently Completed (Dec 18, 2025)

### Performance Overhaul (Phase 2) - COMPLETE
**Impact**: 60x-100x performance improvements
- **60x faster** playlist loading (2-3s → <50ms)
- **50-100x faster** database queries (6 performance indexes)
- **4x faster** metadata enrichment
- **50-80% memory reduction** in artwork pipeline
- **95% cache hit rate** with LibraryCacheService
- **Polymorphic tagging** system (MP3, FLAC, M4A)

### Phase 6A: Bento Grid UI - COMPLETE
**Impact**: Visual transformation from ugly list to beautiful grid
- Beautiful album cards with glassmorphism design
- 200x200 album artwork with hover effects (scale 1.02 + Orbit Blue glow)
- Download progress bars
- Spotify-like aesthetic achieved
- Removed 90 lines of old code, added 133 lines of reusable component

### Phase 6D: Navigation Shell - COMPLETE
**Impact**: Professional 6-page application structure
- 🏠 **Home** - Dashboard with real-time stats (IEventBus integration)
- 🔍 **Search** - P2P search
- 📚 **Library** - Bento Grid with album cards
- ⬇️ **Downloads** - Active downloads
- ⚙️ **Settings** - Configuration
- 📥 **Import** - Spotify/CSV/USB hub
- Lazy-loading pattern for fast startup

### Phase 7: The DJ's Studio (Dec 18, 2025) - COMPLETE
**Impact**: Professional-grade metadata orchestration and DJ tools.
- **My Spotify Hub**: One-click "Liked Songs" and playlist import with auto-refresh OAuth.
- **Advanced Ranking**: Configurable scoring weights for Bitrate, BPM, and String similarity.
- **Pro Metadata Inspector**: Side-panel with Audio Guard (fidelity status) and Camelot Key mapping.
- **Resilient Orchestration**: Exponential backoff retries and persistent post-download worker.
- **Diff-based Updates**: Smart skipping of already-owned tracks during bulk imports.

### Phase 9: Refactoring & Stability (Dec 20, 2025) - COMPLETE
**Impact**: Improved app stability and user trust.
- **Dialog Service**: Implemented native Avalonia confirmation dialogs for destructive actions (Delete Project).
- **Command Safety**: Fixed "Fire-and-forget" command vulnerabilities and race conditions in Library UI.
- **Build Fixes**: Resolved all build errors related to MessageBox dependencies.

### Phase 10: UI Modernization (Dec 20, 2025) - PARTIAL
**Impact**: Significant UX improvement for Library navigation.
- **Compact Sidebar**: Replaced bulky "Album Cards" with efficient, compact list items.
- **Tools Menu**: Consolidated secondary actions into a clean dropdown menu.
- **Visual Polish**: Added hover states and consistent styling for navigation.

### Library UI Critical Fixes - COMPLETE
- PlayTrackCommand, RefreshLibraryCommand, DeleteProjectCommand implemented
- Startup crash fixed (ranking strategy config)
- Library page fully functional with Track Inspector sidebar

### Phase 8: Architectural Foundations (Package 5) - COMPLETE ✅
**Impact**: Critical infrastructure for sonic integrity and automation
- **Producer-Consumer Pattern**: Refactored `SonicIntegrityService.cs` with Channel<T> for batch analysis
  - 2-worker concurrency limit prevents CPU/IO spikes
  - Non-blocking queued analysis architecture
- **Xabe.FFmpeg Integration**: Robust cross-platform FFmpeg wrapper (v5.2.6)
  - Replaced brittle Process.Start() calls
  - Platform-agnostic executable detection
- **Database Maintenance**: Added `VacuumDatabaseAsync()` to `DatabaseService.cs`
  - Reclaims space and optimizes performance
  - Reduces database bloat from frequent updates
- **Automated Maintenance Tasks**: Added daily background cleanup to `App.axaml.cs`
  - Deletes `.backup` files older than 7 days (File.Replace artifacts)
  - Runs database VACUUM for performance
  - Non-blocking with full error handling

### Phase 8: Dependency Validation (Package 6) - COMPLETE ✅
**Impact**: User transparency and graceful degradation  
**Date**: December 18, 2025
- **Smart FFmpeg Checker**: Enhanced validation in `SettingsViewModel.cs`
  - 5-second timeout prevents UI freezing
  - Captures both stdout AND stderr (where FFmpeg prints version)
  - Fallback to common Windows directories (`C:\ffmpeg\bin`, etc.)
  - Improved regex for version parsing
- **Global State**: `AppConfig.IsFfmpegAvailable` and `AppConfig.FfmpegVersion`
  - Synced across app and Settings UI
  - Persisted to config for fast startup checks
- **Dynamic Settings UI**: New "Dependencies" section in Settings
  - Border color changes: 🟢 Green (installed) / 🟠 Orange (missing)
  - One-click "Download FFmpeg" button (auto-hides when installed)
  - OS-specific install instructions (Windows/macOS/Linux)
- **Startup Integration**: FFmpeg validated on app launch
  - Logs warning if missing (non-critical)
  - Enables graceful feature degradation

---

## 🔥 In Progress

### NEW DIRECTION: Library-First Design Philosophy (December 21, 2025)
**Core Principle**: The Library is the central hub - all actions happen from there.

**Why This Matters**:
- Users shouldn't navigate between Library and Downloads pages
- Download controls belong IN the library, not in a separate view
- Track states (downloading, missing, completed) should be visual indicators in the library
- One unified workflow: Browse → Download → Manage

### Phase 11: Library-Integrated Download Controls (65% Complete)
**Goal**: Transform library into a unified workspace with embedded download management

**Completed** ✅:
- [x] **Track State Indicators** - Colored badges showing download status with real-time updates
- [x] **Inline Download Controls** - Context-sensitive buttons (Search/Pause/Resume/Cancel) on track rows
- [x] **Progress Display** - Download percentage shown inline in status badges
- [x] **Post-Import Navigation** - Auto-navigate to library and select imported album
- [x] **Enhanced Playlist Cards** - Show download stats (total, successful, active, failed) with larger album art
- [x] **Performance Optimization** - Event-to-project mapping eliminates O(n) loop bottleneck (Dec 21)

**In Progress** 🚧:
- [ ] **Active Downloads Tracking** - Real-time count of actively downloading tracks per project
  - *Status*: UI ready, backend needs DownloadManager query methods
  - *TODO*: Implement `GetActiveDownloadsForProject()` and `GetCurrentlyDownloadingTrack()`
  - *Blocker*: Requires distinguishing runtime queue state from database state

**Planned** 📋:
- [ ] **Batch Operations** - Multi-select tracks and bulk download/retry/cancel
- [ ] **Context Menu Integration** - Right-click tracks to initiate searches/downloads
- [ ] **Smart Filtering** - Quick filter chips for "Downloading", "Failed", "Missing"
- [ ] **Sidebar Search** - Filter playlist list for users with 50+ projects

**Impact**: 60% reduction in page switching, immediate visual feedback, faster workflow

### Phase 9: Media Player UI Polish (Deferred)
**Status**: Deferred in favor of library-first approach
- Can be integrated into library view later (bottom player bar)

### Phase 8: Sonic Integrity & Automation (40% Complete)
**Status**: Packages 5-6 complete, ready for export UI and background worker  
**Estimated**: 12-16 hours remaining (MVP scope)

### Phase 12: Search Experience 2.0 (Search Page Overhaul) ✨ NEW
**Goal**: Professional, reactive search interface with advanced filtering and batch actions.

**Functional Improvements**:
- **Streaming Results (Reactive Flow)**: `IAsyncEnumerable` integration for instant "pop-in" results.
- **Advanced Filtering HUD**: 
  - Bitrate Range Slider (e.g., >320kbps)
  - User Trust Toggle (Hide locked/slow peers)
  - Format Chips ([MP3] [FLAC] [WAV])
- **Batch Actions**: Multi-select supported with floating action bar (Download All, Add to Playlist).

**Technical Fixes**:
- **Overlay Bug**: Explicit state management for Import Preview overlay to prevent UI blocking.
- **DynamicData**: Replace ObservableCollection with SourceList for high-performance, throttled updates.
- **Theming**: Standardize colors to DynamicResource for dark/light mode compatibility.

**Status**: Planning ✅ - Detailed analysis complete.


**Completed** ✅:
- Package 5: Architectural Foundations (Producer-Consumer, Xabe.FFmpeg, Maintenance)
- Package 6: Dependency Validation (FFmpeg checker with timeout and fallback paths)

**Next Priority**:
- [ ] **Export UI** - Rekordbox/Denon USB export (Package 1)
- [ ] **Background Worker** - Upgrade Scout automation (Package 3)
- [ ] **Smart Replacement** - Atomic file upgrades (Package 4)

### Phase 6C: TreeDataGrid (Lower Priority)
**Status**: Planning complete, ready to implement  
**Estimated**: 4-5 hours

**Approach**: Fork open-source TreeDataGrid to avoid commercial license
- ⚠️ **License Issue**: NuGet package requires commercial Avalonia license
- ✅ **Solution**: Fork `https://github.com/AvaloniaUI/Avalonia.Controls.TreeDataGrid`
- Integrate as project reference (not NuGet)
- Maintain our own version

**Features**:
- Hierarchical track view (Playlist → Album → Tracks)
- Virtual scrolling for 1000+ tracks
- Collapse/expand albums
- Custom column templates
- Performance matches backend optimizations

**Implementation Plan**: `phase6c_treedatagrid_plan.md`

---

## 🎯 Planned Features

### Phase 6B: Styling & Polish (3-4h)
**Priority**: High (final UI touches)
- Wire album card Play/Download commands
- Add Inter/JetBrains Mono fonts
- Integrate ArtworkPipeline for real album images
- Add loading states
- Active navigation highlights (current page)
- Import progress feedback (recent imports)
- Dashboard quick actions (resume last download)


### High Priority

#### 1. Spotify Metadata Foundation ✨ (High Priority)
**The Gravity Well That Stabilizes Everything**
- **Database Schema**: Add Spotify IDs (track/album/artist) to all entities ✅ Complete (Phase 0.1)
- **Metadata Service**: Automatic enrichment with artwork, genres, popularity ✅ Complete (Phase 0.2)
- **Import Integration**: Every import gets canonical metadata anchors ✅ Complete
- **Cache Layer**: Local metadata cache to avoid API spam ✅ Complete
- **Smart Logic**: "DJ Secret" duration matching and fuzzy search ✅ Complete (Phase 0.3)
- **Canonical Anchors**: Store Spotify Canonical Duration to prevent "Radio Edit" vs "Extended Mix" mismatches.

#### 2. Spotify OAuth Authentication (Anchorless Beacon)
- User sign-in with Spotify (PKCE flow) ✅ Complete (See `DOCS/SPOTIFY_AUTH.md`)
- Access private playlists and collections
- Saved/liked tracks import
- Secure cross-platform token storage ✅ Complete
- **Status**: Core complete, UI integrated
- **Impact**: Unlocks user's entire Spotify library

#### 3. Critical Bug Fixes (Orbit Correction)
- Fix drag-drop namespace issue (build error)
- Implement Open Folder command
- Replace WPF dialogs with Avalonia
- Complete album download logic
- **Impact**: Stability and compilation

#### 4. Architecture Refactoring (Technical Debt Reduction)
- **DownloadManager**: Split into Discovery, Orchestration, and Enrichment services
- **Event-Driven UI**: Unify service-to-UI communication via EventBus
- **Library Mapping**: Move entity mapping to extension methods
- **OAuth Server**: Generic loopback server for future integrations
- **Input Processing**: Abstract input handling from ViewModels
- **Impact**: Maintainability, Performance, and Testability

#### 2. Album Download Completion
- Recursive directory parsing for album mode
- UI grouping by album in Library view
- Batch download job management
- **Impact**: Major feature gap

#### 3. Metadata Enrichment
- Album art fetching (Last.fm/Spotify API)
- Automatic ID3 tag writing
- Cover art display in Library
- **Impact**: Visual polish

#### 4. Advanced Ranking Configuration ✨ NEW
- **Settings UI**: User-configurable weight sliders for ranking components
  - BPM Proximity weight (default: 150 pts)
  - Bitrate Quality weight (default: 200 pts, uncapped)
  - Duration Match weight (default: 100 pts)
  - String Similarity weight (default: 200 pts)
- **Presets**: "Quality First" (bitrate heavy), "DJ Mode" (BPM heavy), "Balanced"
- **Real-time Preview**: Show example rankings as weights change
- **Impact**: User control over search behavior (quality vs musical alignment)

#### 5. Download Resume
- Partial file recovery after crashes
- Resume interrupted downloads
- Better error recovery
- **Impact**: Reliability

### Phase 2: Code Quality & Maintainability (Refactoring) ✨ NEW

#### 1. Extract Method - ResultSorter & DownloadDiscoveryService
- **Problem**: Monolithic scoring methods mixing BPM, bitrate, and duration logic
- **Solution**: Extract `CalculateBitrateScore()`, `CalculateDurationPenalty()`, `EvaluateUploaderTrust()`
- **VBR Fraud Detection**: Add post-download bit-depth/spectral analysis to flag "Fake Lossless" (e.g., FLAC with no data >16kHz).
- **Path-Based Token Decay**: Weight tokens in filename higher than tokens in parent folders (prevent folder-level metadata contamination).
- **Impact**: Easier unit testing, clearer "Brain" logic
- **Reference**: [Refactoring.Guru - Extract Method](https://refactoring.guru/extract-method)

#### 2. Replace Magic Numbers - Scoring Constants
- **Problem**: Hardcoded values (`15000` duration tolerance, `+10`/`-20` point values)
- **Solution**: Create `ScoringConstants` class or move to `AppConfig`
- **Impact**: Single source of truth for tuning sensitivity
- **Reference**: [Refactoring.Guru - Replace Magic Number](https://refactoring.guru/replace-magic-number-with-symbolic-constant)

#### 3. Replace Conditional with Polymorphism - MetadataTaggerService
- **Problem**: Complex if/else for MP3 vs FLAC tagging logic
- **Solution**: Base `AudioTagger` class with `Id3Tagger` and `VorbisTagger` subclasses
- **Impact**: Cleaner format handling, easier to add new formats
- **Reference**: [Refactoring.Guru - Replace Conditional](https://refactoring.guru/replace-conditional-with-polymorphism)

#### 4. Introduce Parameter Object - SpotifyMetadataService
- **Problem**: Long parameter lists (Artist, Title, BPM, Duration, etc.)
- **Solution**: Create `TrackIdentityProfile` object wrapping search criteria
- **Impact**: Prevents "Long Parameter List" smell, easier bulk operations
- **Reference**: [Refactoring.Guru - Introduce Parameter Object](https://refactoring.guru/introduce-parameter-object)

#### 5. Extract Class - MetadataEnrichmentOrchestrator
- **Problem**: God Object handling renaming, artwork, and tag persistence
- **Solution**: Split into `LibraryOrganizationService`, `ArtworkPipeline`, `MetadataPersistenceOrchestrator`
- **Impact**: Single Responsibility Principle, better testability
- **Reference**: [Refactoring.Guru - Extract Class](https://refactoring.guru/extract-class)

#### 6. Strategy Pattern - Ranking Modes
- **Problem**: Need different ranking behaviors (Audiophile vs DJ vs Fastest)
- **Solution**: `ISortingStrategy` interface with mode implementations
- **Impact**: Runtime switching between "Quality First" and "BPM Match" modes
- **Reference**: [Refactoring.Guru - Strategy](https://refactoring.guru/design-patterns/strategy)

#### 7. Observer Pattern - Event-Driven Architecture
- **Problem**: Hard dependencies between analysis engine and UI (tight coupling)
- **Solution**: Use `EventBusService` for `TrackAnalysisProgressEvent`, `DownloadProgressEvent`
- **Impact**: Multi-core analysis doesn't "know" about UI, multiple observers can listen
- **Reference**: [Refactoring.Guru - Observer](https://refactoring.guru/design-patterns/observer)

#### 8. Null Object Pattern - Metadata Handling
- **Problem**: Constant null checks (`if (metadata != null)`, `if (bpm.HasValue)`)
- **Solution**: `NullSpotifyMetadata` with default values (BPM=0, Key="Unknown", Confidence=0)
- **Impact**: Cleaner scoring logic, no null-conditional operators, fewer crashes
- **Reference**: [Refactoring.Guru - Null Object](https://refactoring.guru/introduce-null-object)

#### 9. Command Pattern - Undo/Redo for Library Actions
- **Problem**: No way to undo library upgrades or deletions
- **Solution**: Encapsulate actions as objects with `Execute()` and `Undo()` methods
- **Impact**: Ctrl+Z support for Self-Healing Library, safer bulk operations
- **Reference**: [Refactoring.Guru - Command](https://refactoring.guru/design-patterns/command)

#### 10. Proxy Pattern - Lazy-Loading Artwork
- **Problem**: Loading 1000+ album arts simultaneously crashes UI
- **Solution**: Virtual Proxy returns placeholder, loads high-res only when visible
- **Impact**: Smooth scrolling in large libraries, reduced memory usage
- **Reference**: [Refactoring.Guru - Proxy](https://refactoring.guru/design-patterns/proxy)

#### 11. Template Method - Import Provider Skeleton
- **Problem**: Each import provider (CSV, Spotify, Tracklist) duplicates enrichment logic
- **Solution**: Base `ImportProvider` with template method defining skeleton
- **Impact**: Ensures all providers follow "Gravity Well" enrichment automatically
- **Reference**: [Refactoring.Guru - Template Method](https://refactoring.guru/design-patterns/template-method)

#### 12. State Pattern - Download Job State Machine
- **Problem**: Massive `switch(status)` blocks in `DownloadManager`
- **Solution**: `DownloadingState`, `QueuedState`, `EnrichingState` classes
- **Impact**: Cleaner state transitions, easier to add VBR verification step
- **Reference**: [Refactoring.Guru - State](https://refactoring.guru/design-patterns/state)

### Phase 6: Modern UI Redesign (Bento Grid & Glassmorphism) ✨ NEW

#### 1. Bento-Box Dashboard Layout
- **Problem**: Current UI feels like standard Windows form (single massive DataGrid)
- **Solution**: 3-column modular layout (Navigation | Content | Inspector)
- **Layout**:
  - Left (250px): Source trees (Library, USB, Playlists)
  - Middle (flex): Hero header + tracklist with rounded corners
  - Right (300px): Track inspector with large album art, BPM/Key visualizer
- **Impact**: Premium 2025-era desktop app aesthetic
- **Reference**: [Fluent Design System](https://www.youtube.com/watch?v=vcBGj4U75zk)

#### 2. Glassmorphism & Depth
- **Problem**: Flat UI lacks visual hierarchy and polish
- **Solution**: `ExperimentalAcrylicBorder` with blur effects
- **Implementation**:
  - Dark navy background (#0D0D0D)
  - Orbit Blue accent (#00A3FF) for active states
  - Soft colored glows (5% opacity) on hover
  - Blur effects on sidebar and player controls
- **Impact**: Weightless, high-end feel

#### 3. TreeDataGrid for Performance
- **Problem**: Standard DataGrid stutters with 50,000+ tracks
- **Solution**: Avalonia TreeDataGrid with hierarchical views
- **Features**:
  - Smooth inertial scrolling (macOS/iOS-like)
  - Expand Artist → Albums → Tracks
  - Virtualization for massive libraries
- **Impact**: Professional-grade performance

#### 4. Professional Typography & Micro-Interactions
- **Typography**:
  - Variable font (Inter or Geist)
  - SemiBold for titles, 50% opacity for metadata
- **Micro-Interactions**:
  - 150ms hover transitions
  - Play icon overlay on album art hover
  - Skeleton screens instead of spinners
  - Scale(1.01) on track row hover
- **Impact**: Polished, product-quality feel

#### 5. DJ-Focused Visuals
- **Camelot Wheel**: Visual key wheel in inspector panel
- **Bitrate Progress Bars**: Visual quality scanning (full bar = FLAC, half = 192kbps)
- **Waveform Preview**: Mini waveform in track row (planned)
- **BPM/Key Badges**: Color-coded badges for quick scanning
- **Visual Quality Badges**: Color-coded badges for quick scanning (Purple=FLAC, Green=320kbps, Orange<192kbps).
- **Skeleton Screens**: Ghost rows during API/Search loading for 2x faster perceived performance.
- **Impact**: Professional DJ tool aesthetic

### Medium Priority

#### 4. Advanced Filters
- Bitrate range sliders
- Format multi-select
- Length tolerance
- User/uploader filters
- **Impact**: Power user features

#### 5. Playlist Export
- Export to M3U/M3U8
- Export to CSV
- Spotify playlist sync
- **Impact**: Workflow integration

#### 6. Batch Operations
- Multi-select in Library
- Bulk delete/move
- Batch metadata editing
- **Impact**: Efficiency

### Low Priority / Future

#### 7. Wishlist/Auto-Download
- Background monitoring for new releases
- Auto-queue matching tracks
- Notification system
- **Impact**: Automation

#### 8. Statistics Dashboard
- Download history charts
- Library analytics
- Source statistics
- **Impact**: Nice-to-have

#### 9. Themes
- Light mode option
- Custom color schemes
- User-defined themes
- **Impact**: Personalization

### Advanced Audio Features (differentiators)

#### 10. Self-Healing Library (Phase 5)
- **Automatic Upgrades**: Replace 128kbps MP3s with FLACs automatically
- **Cue Point Preservation**: Transfer DJ hot cues and memory points to new files
- **Smart Time Alignment**: Cross-correlation to fix silence offsets during transfer
- **Key Detection**: Chromagram analysis for Camelot key notation
- **Impact**: **REVOLUTIONARY** for DJ workflow

---

## 🐛 Known Issues

### Critical
- None currently

### Minor
- Drag-and-drop adorner positioning on high-DPI displays
- Occasional UI thread delays with large playlists (10k+ tracks)

---

## 📊 Performance Targets

- **Startup Time**: < 2 seconds
- **Search Response**: < 5 seconds for 100 results
- **Download Throughput**: Limited by network and Soulseek peers
- **UI Responsiveness**: No freezes during background operations
- **Database Operations**: < 100ms for typical queries

---

## 🔄 Recent Changes (v1.0.0)

- ✅ **Phase 0.3 ("Brain Activation")**: Verified Smart Search logic & Validation Command
- ✅ **Phase 0.2 ("Gravity Well")**: Spotify Metadata Service with Caching and Enrichment Orchestrator
- ✅ **Phase 0.1**: Database Schema Evolution (Keys, BPM, CuePoints)
- ✅ Implemented `AsyncRelayCommand` for responsive UI operations
- ✅ Added "Clear Spotify Cache" to Settings
- ✅ Fixed database concurrency exception in drag-and-drop
- ✅ Added UI refresh after playlist modifications
- ✅ Implemented file path resolution from DownloadManager
- ✅ Added taskbar icon with transparent background
- ✅ Enabled console diagnostics for debug builds
- ✅ Added version display in status bar

---

## 🎯 Next Generation: Phase 8 - Sonic Integrity & Automation

### 1. VBR Fraud Detection & Spectral Analysis
- **Problem**: Many "FLAC" files on P2P networks are actually upscaled 128kbps MP3s.
- **Solution**: Use `FFmpeg` or `BASS` to perform spectral analysis post-download.
- **Feature**: Automatically flag tracks with a frequency cutoff below 16kHz as "Suspicious/Fraudulent".
- **Impact**: Guaranteed audiophile quality for the library.

### 2. Self-Healing Library (Part 1: Auto-Upgrades)
- **Problem**: Library contains many old, low-quality (128/192kbps) tracks.
- **Solution**: Background "Upgrade Scout" that searches for FLAC/320kbps versions of existing library tracks.
- **Feature**: "One-Click Upgrade" to replace lower-quality files while preserving metadata and playlist position.

### 3. Recordbox & Denon Hub (USB Export)
- **Problem**: Moving music from app to DJ hardware is manual.
- **Solution**: Generate Rekordbox-compatible XML or Denon Engine database on a USB drive.
- **Feature**: Sync a playlist directly to a FAT32 USB drive with proper folder structure.

### 4. Advanced Batch Discovery
- **Problem**: Searching for 100 tracks in a new playlist is tedious.
- **Solution**: "Deep Search" mode for playlists that queues every missing track for autonomous discovery.
- **Impact**: Fully automated library building.

---

## 📝 Next Immediate Actions

1. **Phase 8 Implementation Plan** - Architect the Spectral Analysis worker.
2. **Complete album downloading** - Improve recursive directory parsing.
3. **User documentation** - Tutorials and guides.

---

**Last Updated**: December 17, 2024
**Current Version**: 1.2.1
**Status**: Active Development
