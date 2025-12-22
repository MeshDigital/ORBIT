# QMUSICSLSK - Upgrade Roadmap

**Spotify-Like Music Player - Feature Implementation Plan**

---

## Current Status: 71% Complete ✅

### Recent Updates (December 21, 2025)
### Recent Updates (December 21, 2025)
### Recent Updates (December 22, 2025)
- ✅ **Download Stability**: Fixed "Download Album" button binding and layout (CompactPlaylistTemplate).
- ✅ **API Rate Limits**: Fixed "Single-Item Batch" bug in Enrichment Orchestrator using Smart Buffer (250ms).
- ✅ **Streaming Import**: Implemented `IAsyncEnumerable` streaming for Spotify imports (50-track batches) for instant responsiveness.
- ✅ **Enrichment Control**: Added "Use Spotify API" toggle to dynamically enable/disable background metadata fetching.
- ✅ **Decoupling**: Ensured downloads continue gracefully even if Spotify enrichment fails (403/Auth errors).
- ✅ **Validation**: Sanitized Spotify IDs to reduce API errors.
- ✅ **API Reliability**: Implemented Global Circuit Breaker for Spotify 403 errors (5min backoff).
- ✅ **Soulseek Stability**: Fixed startup race condition by waiting for `LoggedIn` state.
- ✅ **Data Schema**: Added `ISRC` field to all relevant entities for cross-platform matching.
- ✅ **Ingestion Safety**: Implemented strict length validation (>= 20 chars) for Spotify IDs to prevent malformed imports (e.g. "64").

### Recent Updates (December 21, 2025)
- ✅ **Library & Import 2.0 Refinement**: Multi-select support, Floating Action Bar (FAB), side-filtering.
- ✅ **Performance**: Background DB checks, faster search rendering.
- ✅ **Search UI**: Implemented Bento Grid for albums and reactive status icons for tracks.
- ✅ **Library UI**: Fixed real-time status updates and bindings for track view.

### 🚨 Technical Debt & Stability (Pending FIX)
- [ ] **N+1 Query Pattern Risk**: Refactor project loading to use eager loading (.Include) for track counts to prevent performance degradation.
- [ ] **Soft Deletes & Audit Trail**: Implement `IsDeleted` and `DeletedAt` for imports to allow recovery and history tracking.
- [ ] **Status Management Standardization**: Create a centralized `StatusConverter` to map DB strings to internal enums consistently.
- [ ] **Real-time Deduplication Sync**: Ensure `PlaylistTrack` and `LibraryEntry` states stay in sync immediately upon download failure/success.
- [ ] **Library Resilience**: Implement automated daily backups of `%appdata%/SLSKDONET/library.db`.
- [ ] **Batch Duplicate Fix**: Update `DownloadManager.QueueProject` to check against `addedInBatch` set during hash check loop.
- [ ] **UI Thread Safety**: Wrap `PlaylistTrackViewModel` property updates (via event bus) in `Dispatcher.UIThread.Post`.
- [ ] **Coordinate Precision**: Refactor `LibraryPage.axaml.cs` drag-drop to use `VisualRoot` / `PointToClient` for transformations.
- [ ] **Selection Robustness**: Replace `Task.Delay` in `LibraryViewModel` with a reactive "Wait until Project exists in collection" logic.
- [ ] **Source of Truth Sync**: Update `TrackListViewModel` to cross-reference `DownloadManager` for active tracks not yet in global index.

### 🎯 Metadata Gravity Well 2.0 - Refinements (SpotiSharp Integration)
**Source**: Deep dive analysis comparing ORBIT implementation with SpotiSharp reference architecture

#### Phase 1: Authentication & Connection (HIGH Priority)
- [x] ~~Implement `GetAuthenticatedClientAsync()` with proper PKCE authentication~~ (COMPLETE - Dec 22, 2025)
- [x] ~~Use `PKCEAuthenticator` for automatic token refresh~~ (COMPLETE - Dec 22, 2025)
- [x] ~~Fix Soulseek race condition: `LoggedIn` vs `Connected` check~~ (COMPLETE - Dec 22, 2025)
- [ ] **Ready Event Pattern**: Implement "Ready" event in `SoulseekAdapter` that `DownloadManager` listens for before popping tracks from queue.

#### Phase 2: Intelligence & Enrichment (HIGH Priority)
- [ ] **SpotifyUriResolver**: Create `Utils/SpotifyUriResolver.cs` with Regex to identify Track/Album/Playlist IDs from various formats.
  - Handle `open.spotify.com/track/...` mobile share links
  - Handle `spotify:track:...` URI strings  
  - Generic "Intake" flexibility for user input
- [ ] **Native Batch Enrichment**: Refactor `SpotifyMetadataService.cs` to use native library for ISRC.
  - Use `client.Tracks.GetSeveral(new TracksRequest(ids))` to capture ISRC field
  - Already using `client.Tracks.GetSeveralAudioFeatures()` for BPM/Key ✅
- [ ] **Smart Duration Gating**: Implement `SearchResultMatcher.cs` fuzzy duration logic.
  - If Spotify says 210s, create "Success Range" of 207-213s
  - Score 1.0 for in-range, 0.0 for outliers (e.g., 8min Extended Mix)
  - Prevents false matches on remixes/radio edits

#### Phase 3: File Integrity & Normalization (MEDIUM Priority)
- [ ] **Filename Normalization Service**: Create `Utils/FilenameNormalizer.cs`.
  - Strip illegal OS characters: `\ / : * ? " < > |`
  - Remove redundant "feat." tags from Title if already in Artist field
  - Implement `Cleanup()` method for artist names (SpotiSharp pattern)
- [ ] **FFmpeg Progress Feedback**: Enhance `SonicIntegrityService.cs` with real-time progress.
  - Capture `StandardError` stream during FFmpeg analysis
  - Parse `time=` output to calculate percentage
  - Fire `TrackProgressChangedEvent` for Downloads Page progress bar

#### UX Polish & Reliability (MEDIUM Priority)
- [ ] **Downloads Page Hydration**: Fix "Empty Downloads Page" bug.
  - On `DownloadCenterViewModel.Initialize()`, query database for current state:
    ```csharp
    ActiveDownloads = _db.Tracks.Where(t => t.State == Downloading).ToList();
    ```
  - Ensures UI reflects DB state, not just live events
- [ ] **Deterministic Track ID**: Implement hash-based deduplication.
  - Generate `TrackEntity.Id` using hash of ISRC (if available) or `artist-title` lowercase string
  - Prevents duplicate downloads across multiple playlist jobs
- [ ] **Circuit Breaker UI Indicator**: Add `_isSpotifyServiceDegraded` flag.
  - Set flag on 403 errors instead of just breaking loop
  - Bind "Warning Icon" in header to show "Spotify Sync Unavailable" status
  - User feedback instead of silent failures

### 🏗️ Architectural Improvements & Advanced Features

#### Download Stability (HIGH Priority)
- [ ] **Stalled Download Fix**: Improve `SoulseekAdapter.cs` timeout logic.
  - Distinguish between `TransferStates.Queued` and `InProgress`
  - Disable 60-second timeout while `Queued` (can last hours on Soulseek)
  - Only start stall timer once state shifts to `InProgress`
  - Prevents premature download cancellation

#### Intelligence & Scoring (MEDIUM Priority)
- [ ] **Uploader Trust Scoring**: Add `QueueLength` and `HasFreeUploadSlot` to `ScoringWeights.cs`.
  - Rank 256kbps with open slot higher than 320kbps with 50-person queue
  - Optimize for "Time-to-Listen" vs absolute fidelity
  - Captures data already available in `ParseTrackFromFile()`
- [ ] **VBR Fraud Detection**: Extend `SonicIntegrityService` to detect upscaled files.
  - Check frequency cutoff (< 16kHz indicates upscaled 128kbps→FLAC)
  - Add `FidelityStatus` field: `Genuine | Upscaled | Suspicious`
  - Visual indicator in UI for low-quality masquerading files

#### Library UI & UX (HIGH Priority)
- [ ] **Draggable Column Reordering**: Implement drag-to-reorder for TreeDataGrid columns.
  - Allow users to prioritize Status, BPM, or other columns first
  - Persist column order to user preferences
  - Improve visual hierarchy and customization
- [ ] **Scoped Album Card ViewModels**: Refactor Bento Grid for performance.
  - Each `AlbumCard` gets its own scoped ViewModel
  - Handles own `DownloadCommand` instead of binding to central command
  - Prevents UI lag with 100+ albums updating simultaneously
- [ ] **Skeleton Screens**: Add loading placeholders during API/search latency.
  - Semi-transparent "ghost rows" during Spotify/Soulseek searches
  - Makes app feel 2x faster with perceived performance boost

#### Advanced Features (MEDIUM Priority)
- [ ] **Self-Healing Upgrade Scout**: Background worker to find better quality versions.
  - Scans library for tracks < 192kbps
  - Automatically initiates Soulseek search for 320kbps/FLAC upgrade
  - User approval required before replacing files

### What's Working
- ✅ Search with ranking (Soulseek P2P)
- ✅ Playlists (6 loaded from database)
- ✅ Library management
- ✅ Track playback (LibVLC)
- ✅ Download tracking
- ✅ Drag-drop reordering
- ✅ Import (Spotify URLs, CSV)
- ✅ Album grouping
- ✅ Context menu
- ✅ Keyboard shortcuts
- ✅ Window state persistence
- ✅ System tray

### Database
- ✅ SQLite with full history tracking
- ✅ PlaylistJobs, PlaylistTracks, ActivityLogs
- ✅ Automatic migrations
- ✅ Foreign keys, indexes

---

## 🌌 ANTIGRAVITY IMPLEMENTATION STRATEGY

**Critical Decision**: Build Spotify Metadata Foundation FIRST to avoid 12+ hours of rework

**Why**: Adding Spotify IDs later requires database migrations, import refactoring, and metadata backfill. Build the gravity well now, everything else orbits around it.

---

## Phase 0: Metadata Gravity Well (8-12 hours) 🔴⭐ FOUNDATION - COMPLETE ✅

### 0.1 Database Schema Evolution (3 hours)
**Priority**: ⭐⭐⭐ CRITICAL - LOAD BEARING - COMPLETE ✅

**What to Build**:
- [x] Add `SpotifyTrackId` (string, nullable) to `PlaylistTrackEntity`
- [x] Add `SpotifyAlbumId` (string, nullable)
- [x] Add `SpotifyArtistId` (string, nullable)
- [x] Add `AlbumArtUrl` (string, nullable)
- [x] Add `ArtistImageUrl` (string, nullable)
- [x] Add `Genres` (string, JSON array)
- [x] Add `Popularity` (int, 0-100)
- [x] Add `CanonicalDuration` (int, milliseconds)
- [x] Add `ReleaseDate` (DateTime, nullable)
- [x] Create database migration script

**Files to Modify**:
- `Data/Entities/PlaylistTrackEntity.cs`
- `Data/AppDbContext.cs` (add migration)
- `Models/PlaylistTrack.cs` (add properties)

**Implementation Steps**:
1. Add new columns to entity (all nullable for backward compatibility)
2. Create EF Core migration: `dotnet ef migrations add AddSpotifyMetadata`
3. Test migration on copy of database
4. Add properties to domain model
5. Update ViewModels to display new fields

**Risk Mitigation**:
- Backup database before migration
- Make all columns nullable (optional)
- Test on development database first

---

### 0.2 Spotify Metadata Service (4 hours)
**Priority**: ⭐⭐⭐ CRITICAL - COMPLETE ✅

**What to Build**:
- [x] `SpotifyMetadataService` class (`SpotifyEnrichmentService`)
- [x] `GetTrackMetadata(artist, title)` - lookup by search
- [x] `GetTrackById(spotifyId)` - lookup by ID
- [x] `EnrichPlaylistTrack(track)` - attach metadata to track
- [x] Metadata cache (via `IsEnriched` flag and DB storage)
- [x] Rate limiting (30 req/sec Spotify limit)
- [x] Batch requests (up to 50 tracks per call)

**Files to Create**:
- `Services/SpotifyMetadataService.cs` (new)
- `Data/Entities/SpotifyMetadataCacheEntity.cs` (new)
- `Models/SpotifyMetadata.cs` (new)

**Implementation Steps**:
1. Create metadata cache entity and migration
2. Build `SpotifyMetadataService` with SpotifyAPI.Web
3. Implement search endpoint wrapper
4. Add cache lookup before API calls
5. Implement rate limiting with `SemaphoreSlim`
6. Add batch request support
7. Test with real Spotify API

**API Endpoints Used**:
- `GET /search?q=artist:X track:Y&type=track` - Search for track
- `GET /tracks/{id}` - Get track by ID
- `GET /tracks?ids=X,Y,Z` - Batch get (up to 50)

---

### 0.3 Import Integration (3 hours)
**Priority**: ⭐⭐⭐ HIGH - COMPLETE ✅

**What to Build**:
- [x] Update `SpotifyImportProvider` to store Spotify IDs
- [x] Update `CsvImportProvider` to lookup metadata (via background worker)
- [x] Update `DownloadManager` to enrich post-download (integrated into Discovery)
- [x] Add "Fetching metadata..." status messages (via UI column)
- [x] Background metadata enrichment for existing tracks

**Files to Modify**:
- `Services/ImportProviders/SpotifyImportProvider.cs`
- `Services/ImportProviders/CsvImportProvider.cs`
- `Services/DownloadManager.cs`
- `ViewModels/ImportPreviewViewModel.cs`

**Implementation Steps**:
1. Inject `SpotifyMetadataService` into import providers
2. Call `EnrichPlaylistTrack()` during import
3. Add progress indicator for metadata fetch
4. Store Spotify IDs in database
5. Test CSV import with metadata lookup
6. Test Spotify import with ID preservation

---

### 0.4 Artwork Pipeline (2 hours)
**Priority**: ⭐⭐ MEDIUM

**What to Build**:
- [ ] Download and cache album art from Spotify URLs
- [ ] Store images in `%APPDATA%/QMUSICSLSK/artwork/`
- [ ] Image cache service
- [ ] Placeholder image for missing artwork

**Files to Create**:
- `Services/ArtworkCacheService.cs` (new)

**Implementation Steps**:
1. Create artwork cache directory
2. Download images from `AlbumArtUrl`
3. Save as `{spotifyAlbumId}.jpg`
4. Return local file path for UI binding

---

## Phase 12: Search Experience 2.0 (Technical Breakdown) 🟡 NEW

### 12.1 Reactive Search (Streaming) (4 hours)
- [x] Refactor `SearchOrchestrationService` to return `IAsyncEnumerable<AlbumResultViewModel>` (Done previously)
- [x] Implement incremental results "pop-in" (500ms first paint) (Done previously)
- [x] Use `SourceList<T>` (DynamicData) for thread-safe UI updates
- [x] Search throttling (100ms buffer) to prevent UI stutter (Done via Smart Buffer)
- [x] **Album Cards**: Implement grid layout for album results (`AlbumCard.axaml`)
- [x] **Reactive Status**: Bind search results to `DownloadManager` for live status updates

### 12.2 Advanced Filtering HUD (3 hours)
- [ ] Create `SearchFilterViewModel`
- [ ] Add **Bitrate Slider** (range selector)
- [ ] Add **Format Chips** (MP3/FLAC/WAV toggles)
- [ ] Add **User Trust Toggle** (filter locked/slow peers)
- [ ] Bind filter logic to `DynamicData` filter pipeline

### 12.3 Batch Actions & Selection (3 hours)
- [ ] Enable `SelectionMode="Multiple"` in Search Grid
- [ ] Implement "Floating Action Bar" (appears on selection > 1)
- [ ] Add `DownloadSelectedCommand` (batch download)
- [ ] Add `AddToPlaylistCommand` (batch add)

### 12.4 Critical Fixes (2 hours)
- [ ] **Overlay Bug**: Replace `GetType().Name` calculation with explicit `IsImportOverlayActive` flags
- [ ] **Theming**: Replace `#1A1A1A` with `{DynamicResource RegionColor}`
- [ ] **Reflection Removal**: Replace `GetType().GetProperty("PlayerViewModel")` with properly injected `IPlayerService`

---


## Phase 1.5: Advanced Ranking Configuration (4 hours) ✨ NEW

**Priority**: ⭐⭐ MEDIUM - User Control

**What to Build**:
- [ ] Settings page with ranking weight sliders
- [ ] Configuration storage in `AppConfig`
- [ ] Dynamic weight injection into `ResultSorter`
- [ ] Preset system ("Quality First", "DJ Mode", "Balanced")
- [ ] Real-time ranking preview with example files

**Files to Modify**:
- `Configuration/AppConfig.cs` - Add ranking weight properties
- `Services/ResultSorter.cs` - Accept configurable weights
- `Views/SettingsPage.axaml` - Add ranking configuration UI
- `ViewModels/SettingsViewModel.cs` - Bind sliders to config

**Configuration Schema**:
```csharp
public class RankingWeights
{
    public int BpmProximityWeight { get; set; } = 150;
    public int BitrateQualityWeight { get; set; } = 200;
    public int DurationMatchWeight { get; set; } = 100;
    public int TitleSimilarityWeight { get; set; } = 200;
}
```

**UI Components**:
- Slider: "BPM Match Importance" (0-500, default 150)
- Slider: "Bitrate Quality Importance" (0-500, default 200)
- Slider: "Duration Match Importance" (0-200, default 100)
- Button: "Reset to Defaults"
- ComboBox: Presets dropdown

**Impact**: Allows users to tune ranking for their use case (audiophile vs DJ)

---

## Phase 2: Code Quality & Maintainability (Refactoring) (8-12 hours) ✨ NEW

**Priority**: ⭐⭐ MEDIUM - Technical Debt Reduction

### 2.1 Extract Method - ResultSorter (2 hours)
**Reference**: [Refactoring.Guru - Extract Method](https://refactoring.guru/extract-method)

**Files to Modify**:
- `Services/ResultSorter.cs`
- `Services/DownloadDiscoveryService.cs`

**What to Extract**:
- [ ] `CalculateBitrateScore(Track track)` - Isolate bitrate quality calculation
- [ ] `CalculateDurationPenalty(Track result, Track target)` - Duration mismatch logic
- [ ] `EvaluateUploaderTrust(Track track)` - Free slot + queue length scoring
- [ ] `ExtractBpmFromFilename(string filename)` - Already exists, good example

**Impact**: Each scoring component becomes independently testable

---

### 2.2 Replace Magic Numbers (1 hour)
**Reference**: [Refactoring.Guru - Replace Magic Number](https://refactoring.guru/replace-magic-number-with-symbolic-constant)

**Files to Create**:
- `Configuration/ScoringConstants.cs` (new)

**Constants to Define**:
```csharp
public static class ScoringConstants
{
    // Duration Gating
    public const int DurationToleranceSeconds = 30;
    public const int SmartDurationToleranceSeconds = 15;
    
    // Scoring Weights (configurable via AppConfig)
    public const int BpmProximityWeight = 150;
    public const int BitrateQualityWeight = 200;
    public const int DurationMatchWeight = 100;
    public const int TitleSimilarityWeight = 200;
    
    // Filesize Validation
    public const int MinBytesPerSecond = 8000; // ~64kbps
    public const double FilesizeSuspicionThreshold = 0.5; // 50% of expected
}
```

---

### 2.3 Replace Conditional with Polymorphism - Tagger (3 hours)
**Reference**: [Refactoring.Guru - Replace Conditional](https://refactoring.guru/replace-conditional-with-polymorphism)

**Files to Create**:
- `Services/Tagging/IAudioTagger.cs` (interface)
- `Services/Tagging/Id3Tagger.cs` (MP3, AAC)
- `Services/Tagging/VorbisTagger.cs` (FLAC, OGG)
- `Services/Tagging/TaggerFactory.cs` (factory pattern)

**Files to Modify**:
- `Services/MetadataTaggerService.cs` - Use factory instead of conditionals

**Impact**: Easier to add new formats (WAV, ALAC, etc.)

---

### 2.4 Introduce Parameter Object (2 hours)
**Reference**: [Refactoring.Guru - Introduce Parameter Object](https://refactoring.guru/introduce-parameter-object)

**Files to Create**:
- `Models/TrackIdentityProfile.cs` (new)

**Schema**:
```csharp
public class TrackIdentityProfile
{
    public string Artist { get; set; }
    public string Title { get; set; }
    public string? Album { get; set; }
    public double? BPM { get; set; }
    public int? CanonicalDuration { get; set; }
    public string? MusicalKey { get; set; }
}
```

**Files to Modify**:
- `Services/SpotifyMetadataService.cs` - Accept `TrackIdentityProfile` instead of 5+ parameters

---

### 2.5 Extract Class - Orchestrator Split (3 hours)
**Reference**: [Refactoring.Guru - Extract Class](https://refactoring.guru/extract-class)

**Files to Create**:
- `Services/LibraryOrganizationService.cs` - File renaming and directory structure
- `Services/ArtworkPipeline.cs` - Album art fetching and caching
- `Services/MetadataPersistenceOrchestrator.cs` - DB + tag sync

**Files to Modify**:
- `Services/MetadataEnrichmentOrchestrator.cs` - Delegate to new services

**Impact**: Single Responsibility Principle, easier testing

---

### 2.6 Strategy Pattern - Ranking Modes (2 hours)
**Reference**: [Refactoring.Guru - Strategy](https://refactoring.guru/design-patterns/strategy)

**Files to Create**:
- `Services/Ranking/ISortingStrategy.cs` (interface)
- `Services/Ranking/AudiophileStrategy.cs` - Bitrate-heavy
- `Services/Ranking/DjStrategy.cs` - BPM/Key-heavy
- `Services/Ranking/BalancedStrategy.cs` - Default weights

**Files to Modify**:
- `Services/ResultSorter.cs` - Accept `ISortingStrategy` parameter
- `Configuration/AppConfig.cs` - Store selected strategy

**Impact**: Runtime switching between ranking modes

---

### 2.7 Observer Pattern - Event-Driven Architecture (2 hours)
**Reference**: [Refactoring.Guru - Observer](https://refactoring.guru/design-patterns/observer)

**Files to Modify**:
- `Services/EventBusService.cs` - Enhance existing event bus
- `Services/TrackAnalysisOrchestrator.cs` - Emit progress events
- `ViewModels/*` - Subscribe to events instead of direct calls

**Events to Define**:
```csharp
public class TrackAnalysisProgressEvent
{
    public string TrackId { get; set; }
    public int PercentComplete { get; set; }
    public string CurrentStep { get; set; } // "Extracting BPM", "Detecting Key"
}

public class DownloadProgressEvent
{
    public string JobId { get; set; }
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
}
```

**Impact**: Multi-core analysis engine decoupled from UI, multiple observers can listen

---

### 2.8 Null Object Pattern - Metadata Handling (1 hour)
**Reference**: [Refactoring.Guru - Null Object](https://refactoring.guru/introduce-null-object)

**Files to Create**:
- `Models/NullSpotifyMetadata.cs`

**Implementation**:
```csharp
public class NullSpotifyMetadata : SpotifyMetadata
{
    public NullSpotifyMetadata()
    {
        BPM = 0;
        MusicalKey = "Unknown";
        Confidence = 0.0;
        SpotifyId = null;
        AlbumArtUrl = null;
    }
    
    public override bool IsValid => false;
}
```

**Files to Modify**:
- `Services/SpotifyMetadataService.cs` - Return `NullSpotifyMetadata` instead of null
- `Services/ResultSorter.cs` - Remove null checks, use `metadata.IsValid`

**Impact**: Cleaner scoring logic, no `?.` operators, fewer `NullReferenceException` crashes

---

### 2.9 Command Pattern - Undo/Redo for Library (3 hours)
**Reference**: [Refactoring.Guru - Command](https://refactoring.guru/design-patterns/command)

**Files to Create**:
- `Commands/ILibraryCommand.cs` (interface)
- `Commands/DeleteTrackCommand.cs`
- `Commands/UpgradeTrackCommand.cs`
- `Commands/CommandHistory.cs` (undo stack)

**Implementation**:
```csharp
public interface ILibraryCommand
{
    void Execute();
    void Undo();
    string Description { get; }
}

public class DeleteTrackCommand : ILibraryCommand
{
    private readonly Track _track;
    private readonly string _originalPath;
    
    public void Execute()
    {
        // Delete file, mark as deleted in DB
    }
    
    public void Undo()
    {
        // Restore from recycle bin or backup
    }
}
```

**Impact**: Ctrl+Z support for Self-Healing Library, safer bulk operations

---

### 2.10 Proxy Pattern - Lazy-Loading Artwork (2 hours)
**Reference**: [Refactoring.Guru - Proxy](https://refactoring.guru/design-patterns/proxy)

**Files to Create**:
- `Services/ArtworkProxy.cs`

**Implementation**:
```csharp
public class ArtworkProxy
{
    private BitmapImage? _realImage;
    private readonly string _artworkUrl;
    private static readonly BitmapImage PlaceholderImage = LoadPlaceholder();
    
    public BitmapImage GetImage(bool isVisible)
    {
        if (!isVisible) return PlaceholderImage;
        
        if (_realImage == null)
        {
            _realImage = LoadFromCache(_artworkUrl) ?? DownloadImage(_artworkUrl);
        }
        
        return _realImage;
    }
}
```

**Impact**: Smooth scrolling in 1000+ track libraries, 80% reduction in memory usage

---

### 2.11 Template Method - Import Provider Skeleton (2 hours)
**Reference**: [Refactoring.Guru - Template Method](https://refactoring.guru/design-patterns/template-method)

**Files to Create**:
- `Services/ImportProviders/BaseImportProvider.cs`

**Implementation**:
```csharp
public abstract class BaseImportProvider
{
    // Template method
    public async Task<List<Track>> ImportAsync()
    {
        var rawTracks = await ParseSourceAsync(); // Abstract
        var enrichedTracks = await EnrichWithSpotifyAsync(rawTracks); // Concrete
        await PersistToDatabase(enrichedTracks); // Concrete
        return enrichedTracks;
    }
    
    protected abstract Task<List<Track>> ParseSourceAsync();
}
```

**Impact**: All import providers automatically follow "Gravity Well" enrichment

---

### 2.12 State Pattern - Download Job State Machine (3 hours)
**Reference**: [Refactoring.Guru - State](https://refactoring.guru/design-patterns/state)

**Files to Create**:
- `Services/DownloadStates/IDownloadState.cs`
- `Services/DownloadStates/QueuedState.cs`
- `Services/DownloadStates/DownloadingState.cs`
- `Services/DownloadStates/VerifyingState.cs`
- `Services/DownloadStates/EnrichingState.cs`

**Implementation**:
```csharp
public interface IDownloadState
{
    Task HandleAsync(DownloadJob job);
    IDownloadState GetNextState(DownloadJob job);
}

public class DownloadingState : IDownloadState
{
    public async Task HandleAsync(DownloadJob job)
    {
        // Download file logic
    }
    
    public IDownloadState GetNextState(DownloadJob job)
    {
        return job.DownloadComplete 
            ? new VerifyingState() 
            : this;
    }
}
```

**Impact**: Cleaner state transitions, easier to add VBR verification step

---

## Phase 4: Performance Optimization (Multi-core & Hardware Acceleration) (6-8 hours) ✨ NEW

**Priority**: ⭐⭐ HIGH - Critical for Phase 5 (Self-Healing Library)

### 4.1 Parallel Library Scanning (2 hours)
**Reference**: [.NET 8 Parallel Programming](https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/)

**Files to Modify**:
- `Services/LibraryService.cs` - Refactor scan loops to use `Parallel.ForEachAsync`

**Implementation**:
```csharp
await Parallel.ForEachAsync(tracks, new ParallelOptions
{
    MaxDegreeOfParallelism = Environment.ProcessorCount,
    CancellationToken = cancellationToken
}, async (track, ct) =>
{
    await AnalyzeTrackAsync(track, ct);
});
```

**Impact**: 10,000 track scan: 2 hours → 10 minutes

---

### 4.2 Background Worker Architecture (3 hours)

**Files to Create**:
- `Services/TrackAnalysisOrchestrator.cs` - Channel-based worker pool
- `Services/Workers/AnalysisWorker.cs` - Isolated analysis thread

**Architecture**:
```csharp
public class TrackAnalysisOrchestrator
{
    private readonly Channel<TrackAnalysisRequest> _queue;
    private readonly SemaphoreSlim _workerSemaphore;
    
    public async Task EnqueueAsync(Track track)
    {
        await _queue.Writer.WriteAsync(new TrackAnalysisRequest(track));
    }
    
    private async Task WorkerLoop()
    {
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; // UI priority
        // Process queue...
    }
}
```

**Impact**: UI never freezes during heavy operations

---

### 4.3 Performance Mode Settings (1 hour)

**Files to Modify**:
- `Configuration/AppConfig.cs` - Add `PerformanceMode` enum
- `Views/SettingsPage.axaml` - Add performance mode selector

**Modes**:
- **Eco**: `MaxDegreeOfParallelism = 2` (silent)
- **Balanced**: `MaxDegreeOfParallelism = ProcessorCount / 2` (default)
- **Turbo**: `MaxDegreeOfParallelism = ProcessorCount` (fastest)

---

### 4.4 Memory Mapped Files (2 hours)

**Files to Modify**:
- `Services/MetadataService.cs` - Use `MemoryMappedFile` for large file analysis

**Implementation**:
```csharp
using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open);
using var accessor = mmf.CreateViewAccessor();
// Direct memory access without loading entire file
```

**Impact**: 50% reduction in RAM usage for batch operations

---

### 4.5 Hardware Acceleration (Optional, 2-4 hours)

**Files to Create**:
- `Services/HardwareAcceleration/GpuTranscoder.cs` - FFmpeg with NVENC/QuickSync

**Platform Detection**:
```csharp
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    // Use NVENC or QuickSync
}
else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
{
    // Use VideoToolbox
}
```

**Impact**: Real-time preview generation, faster fingerprinting

---
5. Add placeholder for missing artwork

---

## Phase 0B: OAuth UI Completion (3 hours) 🟡 HIGH

### 0B.1 OAuth PKCE Core Service ✅ COMPLETE
**Priority**: ⭐⭐⭐ CRITICAL

**Status**: ✅ All core services implemented

**Completed**:
- [x] PKCE code verifier generation (128 chars, base64url)
- [x] PKCE code challenge generation (SHA256)
- [x] Authorization URL builder with scopes
- [x] Token exchange endpoint integration
- [x] Refresh token logic
- [x] Token expiration detection

**Files Created**:
- ✅ `Utils/PKCEHelper.cs`
- ✅ `Services/LocalHttpServer.cs`
- ✅ `Services/SpotifyAuthService.cs`
- ✅ `Services/ISecureTokenStorage.cs`
- ✅ `Services/Platform/WindowsTokenStorage.cs`
- ✅ `Services/Platform/MacOSTokenStorage.cs`
- ✅ `Services/Platform/LinuxTokenStorage.cs`
- ✅ `Services/SecureTokenStorageFactory.cs`

---

### 0B.2 UI Integration (3 hours)
**Priority**: ⭐⭐⭐ HIGH

**What to Build**:
- [ ] "Sign In with Spotify" button in Settings
- [ ] OAuth flow dialog with status messages
- [ ] User profile display when authenticated
- [ ] "Sign Out" button
- [ ] "My Playlists" button in Import page
- [ ] "My Saved Tracks" button in Import page

**Files to Modify**:
- `Views/Avalonia/SettingsPage.axaml` (add Spotify auth section)
- `ViewModels/SpotifyImportViewModel.cs` (add auth commands)

**Files to Create**:
- `Views/Avalonia/SpotifySignInDialog.axaml` (new)

**Implementation Steps**:
1. Add Spotify section to Settings page
2. Create `SpotifySignInDialog` with status messages
3. Add `SignInCommand` to `SpotifyImportViewModel`
4. Add `SignOutCommand` to `SpotifyImportViewModel`
5. Add `IsAuthenticated` property with UI binding
6. Add `UserDisplayName` property to show logged-in user
7. Wire up "My Playlists" and "Saved Tracks" buttons
8. Test sign-in/sign-out flow in UI

---

### 0.4 Spotify API Integration (2 hours)
**Priority**: ⭐⭐⭐ HIGH

**What to Build**:
- [ ] User profile endpoint (`/me`)
- [ ] User playlists endpoint (`/me/playlists`)
- [ ] User saved tracks endpoint (`/me/tracks`)
- [ ] Search endpoint with user context
- [ ] Authenticated SpotifyClient factory

**Files to Modify**:
- `Services/InputParsers/SpotifyInputSource.cs` (add user endpoints)
- `Services/ImportProviders/SpotifyImportProvider.cs` (use auth when available)

**Implementation Steps**:
1. Inject `SpotifyAuthService` into `SpotifyInputSource`
2. Add `GetCurrentUserAsync()` method
3. Add `GetUserPlaylistsAsync()` method
4. Add `GetUserSavedTracksAsync()` method
5. Update `CreateClientAsync()` to use auth tokens when available
6. Add fallback to Client Credentials for public playlists
7. Test with authenticated Spotify account

---

### 0.5 Configuration & Testing (1 hour)
**Priority**: ⭐⭐

**What to Build**:
- [ ] Add OAuth settings to AppConfig
- [ ] Update Settings UI with redirect URI config
- [ ] Add Spotify Developer Dashboard setup docs
- [ ] End-to-end testing checklist

**Files to Modify**:
- `Configuration/AppConfig.cs` (add OAuth properties)
- `README.md` (add OAuth setup instructions)

**Files to Create**:
- `DOCS/SPOTIFY_OAUTH_SETUP.md` (new - setup guide)

**Implementation Steps**:
1. Add `SpotifyRedirectUri` to AppConfig (default: localhost:5000)
2. Add `SpotifyCallbackPort` to AppConfig
3. Add `SpotifyRememberAuth` toggle to AppConfig
4. Create setup guide for Spotify Developer Dashboard
5. Document redirect URI configuration
6. Create testing checklist
7. Test on Windows, macOS, and Linux

---

## Phase 1: Critical Features ✅ 80% COMPLETE (was 0%)

### 1.1 Queue Management ✅ 90% COMPLETE (was 0%)
**Priority**: ⭐⭐⭐ CRITICAL

**Completed Today**:
- [x] Play queue view (QueuePanel.axaml in right sidebar)
- [x] Add to queue button (context menu + 📋 button + drag-drop)
- [x] Clear queue button
- [x] Drag-drop reorder queue (custom ghost items)
- [x] Remove from queue
- [x] Drag-to-player for immediate playback
- [x] MoveTrack(globalId, targetIndex) method

**Remaining**:
- [ ] Persist queue order to database (10%)
- [ ] Restore queue on app restart

**Files Created**:
- ✅ `Views/Avalonia/Controls/QueuePanel.axaml`
- ✅ `Views/Avalonia/Controls/QueuePanel.axaml.cs`
- ✅ `Services/DragAdornerService.cs`
- ✅ `Views/Avalonia/PlayerControl.axaml.cs`

**Backend Status**: ✅ Complete (PlayerViewModel has full queue logic)

---

### 1.2 Now Playing View (3 hours)
**Priority**: ⭐⭐⭐ CRITICAL

**What to Build**:
- [ ] Full-screen now playing view
- [ ] Large album art display (300x300px)
- [ ] Track progress bar with time
- [ ] Next/Previous buttons
- [ ] Shuffle toggle
- [ ] Repeat toggle (off/one/all)
- [ ] Volume slider
- [ ] Like/favorite button

**Files to Create/Modify**:
- `Views/Avalonia/NowPlayingPage.axaml` (new)
- `PlayerViewModel.cs` (add shuffle, repeat logic)
- `MainViewModel.cs` (add navigation to now playing)

**Backend Status**: ✅ PlayerViewModel ready

**Implementation Steps**:
1. Create `NowPlayingPage.axaml` with large layout
2. Add album art image binding
3. Implement progress bar with seek
4. Add shuffle/repeat state to PlayerViewModel
5. Wire up all playback controls
6. Add navigation from mini player
7. Test all playback modes

---

### 1.3 Playlist History UI (30 minutes)
**Priority**: ⭐⭐⭐ QUICK WIN

**What to Build**:
- [ ] Small 📜 icon next to each playlist name
- [ ] Click → show activity log panel
- [ ] Timeline view with timestamps
- [ ] Filter by action type
- [ ] Show: Added, Removed, Moved, Created, Deleted

**Files to Create/Modify**:
- `Views/Avalonia/PlaylistHistoryPanel.axaml` (new)
- `ViewModels/PlaylistHistoryViewModel.cs` (new)
- `LibraryPage.axaml` (add history icon)

**Backend Status**: ✅ ActivityLogs table ready

**Implementation Steps**:
1. Create `PlaylistHistoryViewModel` loading from `ActivityLogs`
2. Create `PlaylistHistoryPanel.axaml` with timeline UI
3. Add small icon to playlist list items
4. Wire up click event
5. Test with existing activity data

---

### 1.4 Album Detail View (45 minutes)
**Priority**: ⭐⭐ QUICK WIN

**What to Build**:
- [ ] Click album in search results → show detail
- [ ] Album detail panel/page
- [ ] Track list with all album tracks
- [ ] Download all button
- [ ] Album metadata (artist, year, track count)

**Files to Create/Modify**:
- `Views/Avalonia/AlbumDetailPanel.axaml` (new)
- `ViewModels/AlbumDetailViewModel.cs` (new)
- `SearchPage.axaml` (add click handler)

**Backend Status**: ✅ AlbumResults ready

**Implementation Steps**:
1. Create `AlbumDetailViewModel` with track list
2. Create `AlbumDetailPanel.axaml` UI
3. Add click event to album grid
4. Show panel/navigate to detail
5. Test download all functionality

---

## Phase 2: Enhanced Features (6 hours) 🟡

### 2.1 Search Filters (1 hour)
**Priority**: ⭐⭐

**What to Build**:
- [ ] Format filter dropdown (MP3, FLAC, AAC, etc.)
- [ ] Bitrate filter (128k, 192k, 320k, lossless)
- [ ] File size filter
- [ ] Duration filter
- [ ] Clear filters button

**Files to Modify**:
- `SearchPage.axaml` (add filter UI)
- `MainViewModel.cs` (add filter logic)

**Backend Status**: ✅ Filter logic exists

---

### 2.2 Smart Playlists (3 hours)
**Priority**: ⭐⭐

**What to Build**:
- [ ] Recently Added (last 30 days)
- [ ] Most Played (track play count)
- [ ] Favorites/Liked Songs
- [ ] Failed Downloads
- [ ] High Quality (FLAC only)
- [ ] Auto-update on library changes

**Files to Create**:
- `ViewModels/SmartPlaylistViewModel.cs`
- `Services/SmartPlaylistService.cs`

**Backend Status**: ⚠️ Need play count tracking

**Implementation**:
1. Add `PlayCount` to PlaylistTrack entity
2. Create smart playlist logic
3. Add to library sidebar
4. Auto-refresh on changes

---

### 2.3 Social Features (2 hours)
**Priority**: ⭐⭐

**What to Build**:
- [ ] Export playlist to M3U/JSON
- [ ] Import playlist from M3U/JSON
- [ ] Share playlist (copy to clipboard)
- [ ] Collaborative playlists (advanced)

**Files to Create**:
- `Services/PlaylistExportService.cs`
- `Services/PlaylistImportService.cs`

---

## Phase 2.5: Download Orchestration & Active Management (10-12 hours) 🟠 HIGH PRIORITY

**Goal**: Transform download system from "Import → Fire-and-Forget" to "Active Orchestration" with concurrency control, resumability, and real-time visibility.

### 2.5.1 Orchestration Engine: Throttling & Queuing (3 hours)
**Priority**: ⭐⭐⭐ CRITICAL

**What to Build**:
- [ ] `SemaphoreSlim(4, 4)` throttling in `DownloadManager` to limit concurrent downloads to 4
- [ ] `ProcessQueueLoop` with fire-and-forget pattern for continuous queue processing
- [ ] Folder creation logic: `DefaultLibraryPath/ArtistName/AlbumName/` before first track starts
- [ ] `.part` file handling for resumable downloads (download to `.tmp` → rename on completion)
- [ ] File size verification for resume support (check existing `.part` file size)

**Concurrency Strategy**:
```csharp
private readonly SemaphoreSlim _downloadSemaphore = new(4, 4);

public async Task ProcessQueueLoop(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        // 1. Wait for a track to be available in the ConcurrentQueue
        if (_downloadQueue.TryDequeue(out var ctx))
        {
            // 2. Wait for one of the 4 slots to open up
            await _downloadSemaphore.WaitAsync(ct);

            // 3. Fire and forget the download task so the loop can pick up the next track
            _ = Task.Run(async () => 
            {
                try 
                {
                    await ProcessTrackWithFolderLogicAsync(ctx);
                }
                finally 
                {
                    _downloadSemaphore.Release(); // Free up the slot for the next track
                }
            }, ct);
        }
        else
        {
            await Task.Delay(500, ct); // Idle wait
        }
    }
}
```

**Files to Modify**:
- `Services/DownloadManager.cs` - Add semaphore throttling
- `Services/DownloadManager.cs` - Implement `ProcessQueueLoop`
- `Services/DownloadManager.cs` - Add `ProcessTrackWithFolderLogicAsync`

**Implementation Steps**:
1. Add `SemaphoreSlim _downloadSemaphore` field
2. Implement continuous `ProcessQueueLoop` method
3. Add folder structure creation: `Path.Combine(LibraryRoot, Artist, Album)`
4. Implement `.part` file logic for resumability
5. Add file size check for partial downloads
6. Test with 50+ track batch to verify only 4 download simultaneously

---

### 2.5.2 Persistent Transfer State & Resumability (2 hours)
**Priority**: ⭐⭐⭐ CRITICAL

**What to Build**:
- [ ] Download to `.part` or `.tmp` files to prevent corruption
- [ ] Check for existing `.part` files before starting download
- [ ] Resume logic: if `.part` exists, request remaining bytes (HTTP Range header)
- [ ] Hash verification for partial files (optional: skip if protocol doesn't support)
- [ ] Atomic rename on completion: `TrackName.mp3.part` → `TrackName.mp3`
- [ ] Database state: persist `Paused` state to DB on app close
- [ ] Auto-resume: on app restart, scan DB for `Paused`/`Pending` tracks and re-enqueue

**Resumability Logic**:
```csharp
private async Task DownloadFileAsync(PlaylistTrack track, string targetPath)
{
    var partPath = targetPath + ".part";
    long startPosition = 0;
    
    // Check if partial file exists
    if (File.Exists(partPath))
    {
        var fileInfo = new FileInfo(partPath);
        startPosition = fileInfo.Length;
        _logger.LogInformation($"Resuming download from byte {startPosition}");
    }
    
    // Download with resume support
    // (Implementation depends on Soulseek API support for partial transfers)
    await DownloadFromPositionAsync(track, partPath, startPosition);
    
    // Atomic rename on success
    File.Move(partPath, targetPath, overwrite: false);
}
```

**Files to Modify**:
- `Services/DownloadManager.cs` - Add `.part` file handling
- `Services/DownloadManager.cs` - Implement resume logic
- `Data/Entities/PlaylistTrackEntity.cs` - Ensure `Paused` state persists
- `App.axaml.cs` - Add startup logic to re-enqueue paused/pending tracks

**Implementation Steps**:
1. Modify download logic to use `.part` extension
2. Add file existence + size check before download
3. Implement HTTP Range header support (if protocol allows)
4. Add atomic rename on successful completion
5. Update `PlaylistTrackState` to persist `Paused` state
6. Add `RestorePendingDownloadsAsync()` method called on app startup
7. Test resume after app crash/restart

---

### 2.5.3 Global Download Dashboard UI (4 hours)
**Priority**: ⭐⭐⭐ HIGH

**What to Build**:
- [ ] **Global Status Bar**: Persistent download indicator in sidebar/top nav
  - Visual: Download icon (↓) with badge showing active count (e.g., "↓ 4")
  - Click → opens Download Center overlay
- [ ] **Download Center View**: New overlay/page aggregating all active/pending tracks
  - **Active List**: 4 tracks currently "In Flight" with real-time progress bars
  - **Pending Queue**: Scrollable list of "Next Up" tracks
  - **Global Controls**: "Pause All" / "Resume All" buttons
  - **Individual Controls**: Pause/Cancel specific tracks
- [ ] **Continue Logic**: When user hits "Continue" on paused album
  - Re-add tracks to `_downloadQueue`
  - `DownloadFileAsync` checks for existing `.part` files
  - Attempt to append or verify stream to prevent full re-download

**UI Components**:
```xml
<!-- Global Status Indicator (TopBar or Sidebar) -->
<Button Classes="download-indicator">
    <StackPanel Orientation="Horizontal">
        <PathIcon Data="{StaticResource DownloadIcon}" />
        <TextBlock Text="{Binding ActiveDownloadCount}" />
    </StackPanel>
</Button>

<!-- Download Center Panel -->
<Panel IsVisible="{Binding IsDownloadCenterOpen}">
    <!-- Active Downloads (max 4) -->
    <ItemsControl Items="{Binding ActiveDownloads}">
        <ItemTemplate>
            <Border Classes="download-card">
                <ProgressBar Value="{Binding PercentComplete}" />
                <TextBlock Text="{Binding TrackTitle}" />
            </Border>
        </ItemTemplate>
    </ItemsControl>
    
    <!-- Pending Queue -->
    <ItemsControl Items="{Binding PendingDownloads}">
        <!-- Pending track list -->
    </ItemsControl>
    
    <!-- Global Controls -->
    <StackPanel Orientation="Horizontal">
        <Button Command="{Binding PauseAllCommand}">Pause All</Button>
        <Button Command="{Binding ResumeAllCommand}">Resume All</Button>
    </StackPanel>
</Panel>
```

**Files to Create**:
- `Views/Avalonia/DownloadCenterPanel.axaml` (new)
- `ViewModels/DownloadCenterViewModel.cs` (new)

**Files to Modify**:
- `Views/Avalonia/MainWindow.axaml` - Add global download indicator
- `ViewModels/MainViewModel.cs` - Add `IsDownloadCenterOpen` property
- `Services/DownloadManager.cs` - Add `GetActiveDownloads()` and `GetPendingDownloads()` queries

**Implementation Steps**:
1. Create `DownloadCenterViewModel` that subscribes to `TrackStateChangedEvent`
2. Filter events to only show tracks with state `Downloading` or `Pending`
3. Create `DownloadCenterPanel.axaml` with active/pending sections
4. Add global status indicator to main window sidebar
5. Implement "Pause All" / "Resume All" commands
6. Add "Continue" button logic to re-enqueue paused tracks
7. Test with 50 tracks to verify only 4 active at once

---

### 2.5.4 Album Card Progress Indicators (1 hour)
**Priority**: ⭐⭐ MEDIUM

**What to Build**:
- [ ] **Summary Progress**: Add "12/15 Tracks Downloaded" text to album cards
- [ ] **Visual Progress Bar**: Small progress bar at bottom of album card
- [ ] **State Badge**: Color-coded badge (Downloading/Paused/Complete/Failed)
- [ ] **Hover Details**: Tooltip showing detailed download status

**Files to Modify**:
- `Views/Avalonia/LibraryPage.axaml` - Update album card template
- `ViewModels/ProjectViewModel.cs` - Add `DownloadProgress` computed property
- `Services/DownloadManager.cs` - Publish album-level progress events

**Implementation Steps**:
1. Add `TracksDownloaded` and `TotalTracks` properties to `ProjectViewModel`
2. Create computed property: `DownloadProgressText` (e.g., "12/15")
3. Add progress bar to album card XAML
4. Bind progress bar to `(TracksDownloaded / TotalTracks) * 100`
5. Test with multiple albums downloading simultaneously

---

### 2.5.5 Pause/Resume Persistence (2 hours)
**Priority**: ⭐⭐ MEDIUM

**What to Build**:
- [ ] Update `PlaylistTrackState` in DB when "Paused" is clicked
- [ ] On app startup, scan DB for `Paused` or `Pending` tracks
- [ ] Auto re-enqueue paused/pending tracks into `_downloadQueue`
- [ ] Show "Resuming X downloads..." notification on startup

**Files to Modify**:
- `Services/DownloadManager.cs` - Add `PauseTrackAsync(trackId)` method
- `Services/DownloadManager.cs` - Add `RestorePendingDownloadsAsync()` method
- `Services/DatabaseService.cs` - Add `GetPausedTracksAsync()` query
- `App.axaml.cs` - Call `RestorePendingDownloadsAsync()` on startup

**Implementation Steps**:
1. Create `PauseTrackAsync` that updates DB state to `Paused`
2. Create `GetPausedTracksAsync()` query in `DatabaseService`
3. Implement `RestorePendingDownloadsAsync()` to re-enqueue tracks
4. Call restore method in `App.OnStartup`
5. Add notification message: "Resuming 12 paused downloads..."
6. Test by pausing downloads, closing app, and restarting

---

**Total Estimated Time**: 10-12 hours  
**Impact**: Complete overhaul of download system with professional-grade orchestration, reliability, and user visibility

---

## Phase 3: Architecture & Refactoring (12 hours) 🟣 TECHNICAL

### 3.1 DownloadManager Refactor (4 hours)
**Priority**: ⭐⭐⭐ HIGH
**Goal**: Split "God Object" into focused services.

**What to Build**:
- [ ] `DownloadDiscoveryService`: Handle search & selection logic
- [ ] `MetadataEnrichmentOrchestrator`: dedicated background enrichment
- [ ] `DownloadStateMachine`: Manage Pending -> Searching -> Downloading transitions

**Files**:
- `Services/DownloadDiscoveryService.cs` (new)
- `Services/MetadataEnrichmentOrchestrator.cs` (new)
- `Services/DownloadManager.cs` (cleanup)

### 3.2 Event-Driven UI Architecture (3 hours)
**Priority**: ⭐⭐⭐ HIGH
**Goal**: Decouple logic from UI threads and eliminate stuttering.

**What to Build**:
- [ ] Migrate `DownloadManager` to publish `TrackStatusChangedEvent`
- [ ] Migrate `LibraryService` to publish `LibraryUpdatedEvent`
- [ ] Update ViewModels to subscribe via `IEventBus`
- [ ] Remove `Dispatcher.UIThread` calls from Services

**Files**:
- `Services/EventBusService.cs` (existing)
- `Events/TrackEvents.cs` (new)
- `ViewModels/PlaylistTrackViewModel.cs` (subscribe)

### 3.3 Data Access Layer Thinning (1 hour)
**Priority**: ⭐⭐ MEDIUM
**Goal**: Cleaner service code by extracting mapping logic.

**What to Build**:
- [ ] `Extensions/MappingExtensions.cs`
- [ ] Move `EntityToPlaylistTrack` logic to extensions
- [ ] Move `PlaylistTrackToEntity` logic to extensions

### 3.4 Generic OAuth Loopback (2 hours)
**Priority**: ⭐⭐ MEDIUM
**Goal**: Reusable OAuth listener for future integrations.

**What to Build**:
- [ ] Generic `OAuthLoopbackServer`
- [ ] Configurable HTML success/failure pages
- [ ] `NameValueCollection` return type from `WaitForCallbackAsync`

### 3.5 Input Processing Service (3 hours)
**Priority**: ⭐ LOW
**Goal**: Decouple ViewModels from specific input formats.

**What to Build**:
- [ ] `InputAbstractionService`: Unify CSV, Clipboard, and URL handling
- [ ] `SearchIntent`: Standardized input object

---

## Phase 4: Polish Features (22 hours) 🟢

### 3.1 Lyrics Display (4 hours)
- [ ] Fetch lyrics from API (Genius, Musixmatch)
- [ ] Display in now playing view
- [ ] Scroll sync (optional)
- [ ] Cache lyrics locally

### 3.2 Crossfade (4 hours)
- [ ] Crossfade duration setting (0-12s)
- [ ] Smooth transitions between tracks
- [ ] LibVLC audio mixing

### 3.3 Equalizer (6 hours)
- [ ] 10-band EQ UI
- [ ] Presets (Rock, Pop, Jazz, etc.)
- [ ] Save custom presets
- [ ] LibVLC audio filters

### 3.4 Visualizer (8 hours)
- [ ] Audio spectrum analyzer
- [ ] Waveform display
- [ ] Multiple visualizer styles
- [ ] SkiaSharp rendering

---

## Phase 2B: Data Management & Resilience (8 hours) 🟡

### 2.4 DB Backup & Restore (2 hours)
**Priority**: ⭐⭐⭐ CRITICAL

**What to Build**:
- [ ] Backup library.db to external drive
- [ ] Backup config.ini with database
- [ ] Restore from backup
- [ ] Scheduled auto-backup option
- [ ] Backup verification

**Files to Create**:
- `Services/BackupService.cs` (new)
- `Views/Avalonia/BackupPage.axaml` (new)

**Implementation**:
1. Use SQLite `VACUUM INTO` or `File.Copy`
2. Create BackupService with atomic backup
3. Add backup UI to Settings page
4. Implement restore with validation
5. Add auto-backup scheduler

---

### 2.5 DB Import/Export JSON (2 hours)
**Priority**: ⭐⭐

**What to Build**:
- [ ] Export all playlists to JSON
- [ ] Export library entries to JSON
- [ ] Import from JSON (merge or replace)
- [ ] Portable library format

**Files to Modify**:
- `Services/LibraryService.cs` (add Export/Import methods)

**Implementation**:
1. Serialize PlaylistJob + PlaylistTrack to JSON
2. Include metadata and relationships
3. Import with duplicate detection
4. Validate JSON schema on import

---

### 2.6 Library Indexing Optimization (1 hour)
**Priority**: ⭐⭐

**What to Build**:
- [ ] Add database indexes for faster queries
- [ ] Index on PlaylistId, Status, TrackUniqueHash
- [ ] Index on CreatedAt for sorting
- [ ] Measure query performance improvement

**Files to Modify**:
- `Data/AppDbContext.cs` (add indexes)

**Implementation**:
```csharp
modelBuilder.Entity<PlaylistTrackEntity>()
    .HasIndex(t => t.PlaylistId);
modelBuilder.Entity<PlaylistTrackEntity>()
    .HasIndex(t => t.Status);
modelBuilder.Entity<PlaylistTrackEntity>()
    .HasIndex(t => t.TrackUniqueHash);
modelBuilder.Entity<PlaylistJobEntity>()
    .HasIndex(j => j.CreatedAt);
```

---

### 2.7 True Pause/Resume Downloads (3 hours)
**Priority**: ⭐⭐⭐

**What to Build**:
- [ ] Check for .part files on resume
- [ ] Resume from file size offset
- [ ] Validate partial downloads
- [ ] Handle corrupted .part files

**Files to Modify**:
- `Services/DownloadManager.cs`
- `Services/SoulseekAdapter.cs` (add startOffset support)

**Implementation**:
1. Check if .part file exists before download
2. Get file size as resume offset
3. Pass offset to Soulseek download
4. Verify file integrity after resume
5. Delete .part if corrupted

---

## Phase 4: Advanced Features (Future)

### 4.1 Rekordbox Export
- [ ] Export to Rekordbox XML
- [ ] Preserve playlists, cues, metadata

### 4.2 Mobile Sync
- [ ] Export library to mobile device
- [ ] Sync playlists

### 4.3 Cloud Backup
- [ ] Backup library to cloud
- [ ] Restore from backup

---

## Phase 5: Quality Control & Replacement System (16 hours) 🔵

### 5.1 Audio Fingerprinting (4 hours)
**Priority**: ⭐⭐⭐ ADVANCED

**What to Build**:
- [ ] Integrate SoundFingerprinting library
- [ ] Generate fingerprint hash for each track
- [ ] Store fingerprint in database
- [ ] Compare fingerprints for duplicates

**Files to Create**:
- `Services/FingerprintService.cs` (new)
- Add NuGet: `SoundFingerprinting` (MIT license)

**Implementation**:
1. Add SoundFingerprinting NuGet package
2. Create FingerprintService
3. Generate hash post-download
4. Store in LibraryEntry.FingerprintHash
5. Compare hashes for perceptual duplicates

---

### 5.2 Track Quality Data Model (2 hours)
**Priority**: ⭐⭐⭐

**What to Build**:
- [ ] Add FingerprintHash to LibraryEntry
- [ ] Add BitrateScore property
- [ ] Add FingerprintQuality property
- [ ] Add QualityFlags enum

**Files to Modify**:
- `Models/LibraryEntry.cs`
- `Models/PlaylistTrack.cs`
- `Data/Entities/` (corresponding entities)

**Implementation**:
```csharp
public string? FingerprintHash { get; set; }
public int BitrateScore { get; set; }
public double FingerprintQuality { get; set; }
public QualityFlags Flags { get; set; }
```

---

### 5.3 Quality Analysis Service (6 hours)
**Priority**: ⭐⭐⭐

**What to Build**:
- [ ] Post-download quality analysis
- [ ] Compare against existing library
- [ ] Flag duplicates for review
- [ ] Suggest higher quality replacements
- [ ] Auto-replace option (with confirmation)

**Files to Create**:
- `Services/QualityAnalysisService.cs` (new)

**Implementation**:
1. Run analysis after download completes
2. Check fingerprint against all LibraryEntry
3. Compare quality metrics (bitrate, format, length)
4. Flag duplicates with lower quality
5. Suggest replacement candidates
6. Log analysis results

---

### 5.4 Quality Review UI (4 hours)
**Priority**: ⭐⭐

**What to Build**:
- [ ] "Library Health" page
- [ ] List flagged duplicates
- [ ] Show quality comparison
- [ ] Replace/Keep/Delete actions
- [ ] Batch operations

**Files to Create**:
- `Views/Avalonia/QualityReviewPage.axaml` (new)
- `ViewModels/QualityReviewViewModel.cs` (new)

**Implementation**:
1. Create QualityReviewViewModel
2. Load flagged tracks from database
3. Display side-by-side comparison
4. Implement replace logic
5. Add to navigation

---

### 5.5 Advanced Audio Analysis (Musical Key & Cues) (8 hours)
**Priority**: ⭐⭐⭐ DIFFERETIATOR

**What to Build**:
- [ ] Chromagram Analysis (Essentia/LibRosa wrapper)
- [ ] Key Detection (Camelot wheel notation)
- [ ] Cue Point Extraction (Rekordbox XML & ID3 GEOB tags)
- [ ] `CuePoint` data model

**Files to Create**:
- `Services/AudioAnalysisService.cs` (new)
- `Services/RekordboxXmlImporter.cs` (new)
- `Models/CuePoint.cs` (new)

**Implementation**:
1. Integrate audio analysis library (Essentia/LibRosa)
2. Implement key detection algorithm
3. Store Average Harmonic Profile in database
4. Extract cue points from existing files/XML
5. Parse `Pioneer DJ Cue` GEOB tags from ID3

---

### 5.6 Self-Healing Migration System (10 hours)
**Priority**: ⭐⭐⭐ REVOLUTIONARY

**What to Build**:
- [ ] "The Replacement Logic": Move cues from MP3 to FLAC
- [ ] Cross-Correlation time alignment (fix silence offsets)
- [ ] Automatic cue point shifting (+/- ms)
- [ ] Rekordbox XML Export with new file paths

**Implementation Advice**:
1. **Acoustic Match**: Verify `FingerprintService` match first.
2. **Time Alignment**: Calculate offset between signals.
3. **Migration**: Apply offset to old cue points -> save to new track.
4. **Validation**: "Library Health" dashboard approval step.


---

## Implementation Priority

### Week 1 (6 hours)
1. Queue management (2h)
2. Now playing view (3h)
3. Playlist history UI (30m)
4. Album detail view (45m)

**Result**: 90% Spotify feature parity

### Week 2 (6 hours)
5. Search filters (1h)
6. Smart playlists (3h)
7. Social features (2h)

**Result**: Enhanced library features

### Week 3-4 (22 hours)
8. Lyrics (4h)
9. Crossfade (4h)
10. Equalizer (6h)
11. Visualizer (8h)

**Result**: Premium features

---

## Technical Debt

### Code Quality
- [ ] Add unit tests for ViewModels
- [ ] Add integration tests for services
- [ ] Document all public APIs
- [ ] Code coverage >80%

### Performance
- [ ] Lazy load large playlists
- [ ] Virtualize track lists
- [ ] Optimize database queries
- [ ] Cache album art

### UX
- [ ] Loading indicators
- [ ] Error messages
- [ ] Tooltips everywhere
- [ ] Keyboard shortcuts for all actions

---

## Success Metrics

### Current
- 70% feature parity with Spotify
- 6 playlists loaded
- Full database tracking
- Cross-platform ready

### After Phase 1
- 90% feature parity
- Queue management
- Full now playing view
- History tracking UI

### After Phase 2
- 95% feature parity
- Smart playlists
- Advanced filters
- Social features

### After Phase 3
- 100% feature parity
- Premium features
- Professional polish

---

## Unique Advantages Over Spotify

1. **P2P Downloads** - Own your music forever
2. **No Subscription** - Free forever
3. **High Quality** - FLAC support
4. **Smart Ranking** - Quality detection
5. **Offline by Default** - Already downloads
6. **Privacy** - No tracking
7. **Open Source** - Full control

---

## Notes

- All backend logic is production-ready
- Database handles 1000s of tracks
- Cross-platform (Windows, macOS, Linux)
- Modern Avalonia UI
- Clean MVVM architecture

**Next Step**: Implement Phase 1 (6 hours) for 90% parity
