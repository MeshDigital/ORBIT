# ORBIT (formerly SLSKDONET): v1.0 Stabilization Roadmap

**Last Updated**: December 25, 2025  
**Repository**: https://github.com/MeshDigital/ORBIT  
**Current Phase**: **Phase 5 Complete** (Self-Healing & Rekordbox Analysis) → Preparing for Phase 6 (UI Virtualization)

> [!IMPORTANT]
> **Status Update**: Phase 4 "Rekordbox Integration" is COMPLETE. ORBIT now features professional DJ export tools including playlist export and Monthly Drop functionality. Focus shifts to **Phase 5: Self-Healing Library**.

---

### Phase 3C: Advanced Queue Orchestration - COMPLETE
**Impact**: Solves "Traffic Jam" issues where large imports block single downloads.
- **Multi-Lane Priority Engine**:
  - **Lane A (Express)**: Priority 0 (2 guaranteed slots).
  - **Lane B (Standard)**: Priority 1 (2 max slots).
  - **Lane C (Background)**: Priority 10+ (Background fill).
- **Preemption Logic**: High priority downloads automatically pause lowest-priority active tasks.
- **Project Prioritization**: "VIP Pass" logic to bump entire playlists to Priority 0.

## ✅ Recently Completed (December 2025)

### Phase 5B: Rekordbox Analysis Preservation & Hi-Fi Player - COMPLETE
**Impact**: Professional DJ experience with instant waveforms and VU meters.
- **NAudio Engine**: Low-latency audio processing for VU meters and pitch shifting.
- **ANLZ Probing**: Automatic discovery of Rekordbox `.DAT/.EXT` companion files.
- **Waveform Control**: Real-time rendering of Rekordbox `PWAV` data.
- **VU Monitoring**: Exponential decay peak meters in the main player.
- **Background Enrichment**: Batch-fetching (100 tracks) of Spotify Energy/Valence/Danceability.

### Phase 5A: Self-Healing Library - COMPLETE
**Impact**: Guaranteed zero data loss during crashes/power failures.
- **Crash Recovery Journal**: Transactional logging (SQLite WAL) for all destructive operations.
- **Atomic Tag Writes**: ACID-compliant metadata updates (`SafeWriteService`).
- **Resilient Downloads**: Thread-safe heartbeat monitoring with stall detection.
- **Automatic Recovery**: Self-healing on startup (checks journal, resumes operations, notifies user).
- **Performance**: <1% CPU overhead, <500ms startup delay.

### Phase 1B: Database Optimization - COMPLETE
**Impact**: 50-100x faster query performance.
- **WAL Mode**: Write-Ahead Logging for high concurrency.
- **Index Optimization**: Added critical covering indexes for library queries.
- **Connection Pooling**: Dedicated connection for high-frequency journal writes.

### Phase 8: Architectural Foundations - COMPLETE
**Impact**: Infrastructure for future sonic integrity features.
- **Producer-Consumer Pattern**: Non-blocking batch analysis architecture.
- **Dependency Validation**: Smart FFmpeg detection with graceful degradation.
- **Maintenance Tasks**: Automated database vacuuming and backup cleanup.

### Phase 0-1: Core Features - COMPLETE
- **Intelligent Ranking**: "The Brain" scoring system (Bitrate > BPM > Availability).
- **Spotify Integration**: PKCE Auth, Playlist import, Metadata enrichment.
- **Modern UI**: Dark-themed Avalonia interface with "Bento Grid" layout.
- **P2P Engine**: Robust Soulseek client implementation.

---

## Phase 3A: Atomic Resumability & UI Transparency - COMPLETE ✅
- **Impact**: No redownloading files after a crash; "Value Gap" bridged.
- **Atomic Downloads**: Full `.part` file workflow integrated with Recovery Journal.
- **Health Dashboard**: Backend recovery status visible on Home Screen.
- **Schema Expansion**: "Dual-Truth" audio features (Spotify vs Manual) added to TrackEntity.

## Phase 3B: Download Health Monitor - COMPLETE ✅
**Phase Goal**: Automated self-healing for network instability.
- **Stall Detection**: Adaptive timeout logic (60s standard, 120s for >90% progress)
- **Auto-Retry**: Automatic peer switching for stalled transfers
- **Peer Blacklisting**: Temporary blacklist for problematic peers
- **Dual-Truth Schema**: IntegrityLevel enum (Pending → Bronze → Silver → Gold)
- **Clear Dead-Letters**: UI feature for manual recovery

## Phase 4: Rekordbox Integration - COMPLETE ✅
**Phase Goal**: Professional DJ export functionality.
- **RekordboxService**: Streaming XML generation using XmlWriter
- **KeyConverter**: Musical key normalization (Standard → Camelot → OpenKey)
- **XmlSanitizer**: Metadata safety for Rekordbox compatibility
- **Playlist Export**: Context menu on playlist items
  - ✅ **Database Sync**: Synced Spotify features between `LibraryEntry` and `PlaylistTrack`.
- ✅ **Phase 3C: Advanced Queue Orchestration**:
  - ✅ **Multi-Lane Priority**: Express (0), Standard (1), Background (10) lanes.
  - ✅ **Preemption**: High priority tasks pause lower priority downloads when slots are full.
  - ✅ **Persistence**: Priority levels saved to DB for crash resilience.s (last 30 days)
- **SaveFileDialog**: Integrated using Avalonia StorageProvider

## ✅ Phase 5A: Self-Healing Library - COMPLETE
**Phase Goal**: Automatic quality upgrades for library tracks.

### Core Components
- ✅ **LibraryScanner**: Batch processing with yield return (50 tracks/batch)
- ✅ **MetadataCloner**: Cross-format metadata transfer (ID3 ↔ Vorbis ↔ APE)
- ✅ **UpgradeScout**: P2P search with ±2s duration matching
- ✅ **FileLockMonitor**: Dual-layer safety (PlayerViewModel + OS-level exclusive lock)
- ✅ **UpgradeOrchestrator**: 8-step atomic swap with state machine

### Features
- **8-Step Transactional Swap**: Lock check, P2P search, shadow download, metadata clone, journal checkpoint, backup, swap, database update
- **State Machine**: 9 states with rollback logic (Pending → Downloading → CloningMetadata → ReadyToSwap → BackingUp → Swapping → UpdatingDatabase → Completed/Failed)
- **FLAC-Only Mode**: Conservative upgrade strategy (128/192kbps MP3 → FLAC)
- **7-Day Scan Cooldown**: Prevents redundant rescanning
- **7-Day Backup Retention**: Timestamped folders for rollback safety
- **Gold Status Exclusion**: Respects user-verified tracks
- **Quality Gain Tracking**: Bitrate delta + percent improvement for UI gamification
- **Crash Recovery**: Journal integration for mid-swap safety
- **Shadow Downloads**: Isolated `.orbit/tmp/upgrades` directory
- **Cross-Volume Detection**: MFT atomic update vs verified copy+delete

## ✅ Phase 5B: Rekordbox Analysis Preservation (RAP) - COMPLETE
**Phase Goal**: Preserve Rekordbox analysis during file upgrades.
- ✅ **Binary ANLZ Parsing**: Read beat grids, waveforms, hot cues from `.DAT/.EXT` files
- ✅ **XOR Descrambling**: Decrypted PSSI song structure phrases (Intro/Verse/Chorus/Outro)
- ✅ **High-Fidelity Player**: NAudio-based engine with VU meters and real-time waveforms
- ✅ **Companion Probing**: Automatic lookup of analysis data in `ANLZ` subfolders or USB structures

### ANLZ Tags Supported
- ✅ `PQTZ` - Beat Grid (quantized tick markers)
- ✅ `PCOB` - Cue Points and Hot Cues
- ✅ `PWAV` - Waveform Preview
- ✅ `PSSI` - Song Structure (XOR-encrypted phrases)

### High-Fidelity Infrastructure
- **NAudio Backend**: Replaced LibVLC for better control over audio buffers and threading
- **MeteringSampleProvider**: Real-time VU meter data (Left/Right peak)
- **WaveformControl**: Custom Avalonia component for professional Waveform rendering
- **Pitch UI**: Real-time tempo adjustment (0.9x to 1.1x)


## 🎯 Archived Priority: UI Placeholder Refactor (Completed Phase 3A)

With the backend hardened, we must align the frontend reality with user expectations.

### 1. Health Dashboard Integration
- **Goal**: Visualize the `CrashRecoveryJournal` status in the UI.
- **Implementation**:
    - Update `HomeViewModel` to query `DeadLetter` status.
    - Active "System Health" widget on Dashboard (Green/Orange/Red).
    - "Review Manual Fixes" action for dead-lettered files.

### 2. UI Transparency Audit
- **Goal**: Eliminate "dead clicks".
- **Implementation**:
    - Disable buttons for `Rekordbox Export` and `Spectral Analysis`.
    - Add "Coming v1.1" tooltips.
    - Connect `TrackInspector` spectral fields to actual data sources (or hide if empty).

---

## 🔮 Future Phases

### Month 2: UI Transparency & Speed (January 2026)

#### 1. UI Refactor: "The Transparency Phase" (Highest Priority)
- **Goal**: Connect placeholder UI elements to real logic or clearly mark as "Coming Soon".
- **Features**:
    - **Health Dashboard**: Live status of Crash Journal (DeadLetters) & API Health.
    - **Logic Connection**: Hook up "Spectral Analysis" and "Export" buttons.
    - **Disabled States**: Prevent "dead clicks" on unimplemented features.

#### 2. UI Virtualization (Critical)
- **Goal**: Support libraries with 50,000+ tracks.
- **Tech**: VirtualizingStackPanel, lazy-loading viewmodels.
- **Metric**: 60 FPS scrolling at 10k items.

#### 1. Upgrade Scout
- **Goal**: Automatically find better versions of existing tracks.
- **Logic**: If Library has 128kbps MP3 → Search P2P for FLAC matching duration/metadata → Background Download → Atomic Swap.

#### 2. Sonic Integrity (Phase 8 Implementation)
- **Goal**: Verify true audio quality.
- **Tech**: FFmpeg spectral analysis to detect "transcoded" fake lossless files.

### Phase 9: Hardware Export
- **Goal**: Sync to DJ hardware.
- **Target**: Rekordbox XML export, FAT32 USB sync for Denon/Pioneer.

---

## 📊 Performance Targets vs Reality

| Metric | Target | Current Status |
| :--- | :--- | :--- |
| **Startup Time** | < 2s | **~1.5s** (Excellent) |
| **Crash Recovery** | 100% | **100%** (Verified Phase 2A) |
| **UI Responsiveness** | 60 FPS | **30 FPS** (Needs Virtualization) |
| **Search Speed** | < 5s | **~2-4s** (Good) |
| **Memory Usage** | < 500MB | **~300-600MB** (Variable) |

---

## 🐛 Known Issues (Backlog)

### High Priority
- **UI Scalability**: Large playlists cause UI stuttering (Phase 6C TreeDataGrid required).
- **N+1 Queries**: Some UI views trigger redundant database fetches.
- **Soft Deletes**: Deleting projects is currently destructive (needs Audit Trail).

### Medium Priority
- **Duplicate Detection**: Batch imports sometimes miss duplicates within the same batch.
- **Drag-and-Drop**: Positioning issues on high-DPI displays.

---

**Last Updated**: December 24, 2025  
**Maintained By**: MeshDigital & AI Agents  
**License**: GPL-3.0
