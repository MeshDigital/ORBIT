![ORBIT Banner](assets/orbit_banner.png)

# 🛰️ ORBIT – Organized Retrieval & Batch Integration Tool

> **"Intelligent music discovery meets DJ-grade metadata management."**  
> *A Soulseek client designed for reliability and musical intelligence*

[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-blue)](https://github.com/MeshDigital/ORBIT)
[![.NET](https://img.shields.io/badge/. NET-8.0-purple)](https://dotnet.microsoft.com/)
[![UI](https://img.shields.io/badge/UI-Avalonia-orange)](https://avaloniaui.net/)
[![License](https://img.shields.io/badge/license-GPL--3.0-green)](LICENSE)
[![Status](https://img.shields.io/badge/status-Active%20Development-brightgreen)](https://github.com/MeshDigital/ORBIT)

> [!WARNING]
> **LEGAL & PRIVACY NOTICE**
> *   **Privacy Risk:** ORBIT connects to the Soulseek P2P network. Your IP address **will be visible to other peers**. We strongly recommend using a reputable **VPN** to protect your identity.
> *   **Copyright:** This software is provided for educational purposes and for managing legally acquired content. The developers do not condone copyright infringement. You are solely responsible for compliance with your local laws.

---

## 💡 Why ORBIT Exists

Traditional P2P clients treat music files as generic data: download whatever appears first, hope it's correct. This worked in 2005 when everyone ripped their own CDs. In 2026, P2P networks are full of:

❌ **Fake Files** - YouTube rips labeled as "320kbps FLAC"  
❌ **Wrong Versions** - Radio edits when you wanted the Extended Mix  
❌ **Corrupt Metadata** - "Unknown Artist - Track 01" from lazy rippers  
❌ **Duplicate Hell** - 5 copies of the same track, all with different tags  

### ORBIT's Philosophy: "Trust, But Verify"

**For DJs:** You can't mix with unreliable metadata. BPM off by 2%? Train wreck. Key clash? Audience cringes.  
**For Collectors:** You want bit-perfect archives, not upscaled 128kbps with .flac extensions.  
**For Everyone:** Life's too short to spend 40 hours manually tagging 500 tracks.

ORBIT applies **forensic intelligence** at every step:
1. **Pre-download**: Math-based verification filters fakes before wasting bandwidth
2. **During download**: Checkpoint-based recovery prevents "90% complete → crash → start over"
3. **Post-download**: Spectral analysis confirms quality, enrichment adds missing metadata
4. **Library management**: Automatic upgrades, harmonic matching, Rekordbox export

**Result:** Professional-grade music collection with minimal manual work.

---

## 🚀 What Is ORBIT?

ORBIT is a Soulseek client built for DJs and music enthusiasts who demand both quality and reliability. It combines intelligent search ranking, automated metadata enrichment, and crash-resilient downloads into a professional tool.

### The Problem with Traditional P2P Clients
- **Fake Files**: 64kbps MP3s labeled as "320kbps FLAC" waste bandwidth and storage
- **Wrong Versions**: You search for the 7-minute Extended Mix, get the 3-minute Radio Edit
- **Blind Downloads**: No way to verify quality before spending 10+ minutes downloading
- **Crash = Lost Progress**: Download 90% of a 500MB file, crash, start over from 0%
- **Manual Tagging Hell**: Spend hours manually adding BPM/Key for DJ software

### ORBIT's Solution
Where traditional P2P clients download the first available file, ORBIT applies **forensic intelligence** to find:
- ✅ **Authentic files** - Mathematical verification detects fake 320kbps (upscaled 128kbps)
- ✅ **Correct versions** - Duration matching ensures Radio Edit ≠ Extended Mix
- ✅ **Highest quality** - FLAC (1411kbps) > 320kbps > 192kbps, with size verification
- ✅ **DJ-ready metadata** - Automatic BPM, Key (Camelot), Energy, Danceability tagging
- ✅ **Crash resilience** - Resume downloads and tag operations after unexpected shutdown

---

## ✨ Core Features

### � Forensic Metadata Intelligence (Phase 14)
**WHY:** P2P networks are full of fake files - 64kbps MP3s labeled as "320kbps", lossy files with .flac extensions. Downloading these wastes bandwidth, disk space, and user trust.

**HOW:** Pre-download verification using only metadata (no audio analysis needed):
- **Compression Mismatch Detection**: Math doesn't lie - 320kbps × 5 minutes = ~12MB. If a file claims 320kbps but is 3MB, it's fake.
- **Lossless Size Verification**: FLAC should be 5-10 MB/minute. Below 2.5 MB/min = impossible for lossless.
- **Trust Score (0-100)**: Every search result gets rated. 85+ = "Golden Match", <40 = "Avoid"
- **Result**: Fake files marked as "Trash Tier" before you waste bandwidth
### 📌 Latest Highlights (Jan 2, 2026)
- **ML.NET Engine**: Upgraded recommendation engine to use **LightGBM** (Machine Learning) instead of simple vector math.
- **The Style Lab**: Train your own personalized genre classifiers by simply dragging and dropping track examples.
- **Audio Analysis Pipeline**: FFmpeg + Essentia sidecar with 45s watchdog, atomic DB updates, and Track Inspector auto-refresh.
- **Library Track Display**: Rich duration/size badges (⏱, 💾) with smart KB/MB formatting and dual-source loading.
- **Glass Box Queue Visibility**: Observable AnalysisQueueService with pause/resume, smart ETA, and animated status bar pulse.

### 🎯 Intelligent Search Ranking
**WHY:** Getting 50 results for "Strobe" is useless if you can't tell which is the real Extended Mix vs a YouTube rip.

**HOW:** Multi-tier scoring system prioritizes authenticity, then quality, then availability:
- **Quality-First Scoring**: Bitrate is the primary factor, musical attributes act as tiebreakers
- **Duration Matching**: Ensures you get the version you're searching for (10:37 Extended ≠ 3:30 Radio Edit)
- **Filename Cleanup**: Ignores noise like `[uploader-tag]`, `(Remastered)`, `[Official Video]`
- **Path-Based Discovery**: Extracts BPM/Key from directory names when files lack tags

### 🛡️ Crash Recovery (Phase 2A)
**WHY:** You download 90% of a 500MB FLAC, Windows updates, reboot, start from 0%. Infuriating.

**HOW:** Journal-first architecture inspired by database transaction logs:
- **Automatic Resume**: Downloads and tag writes resume after unexpected closures
- **Progress Tracking**: 15-second heartbeats monitor active downloads (stall detection: 4 missed heartbeats = abort)
- **Atomic Operations**: File operations complete fully or not at all (no corrupt half-written tags)
- **Zero Data Loss**: SQLite WAL mode prevents database corruption
- **Result**: Download queue survives crashes, reboots, power failures

### 🎧 Spotify Integration & Deep Enrichment
**WHY:** DJs need BPM, Key (Camelot notation), Energy for mixing. Manual tagging = 5 minutes per track = 40+ hours for 500 tracks.

**HOW:** Automated metadata pipeline:
- **Playlist Import**: Paste a Spotify URL → 200 tracks queued in 3 seconds
- **Background Worker**: Fetches BPM, Key, Energy, Valence, Danceability while you work
- **Persistent Queue**: Enrichment tasks stored in SQLite, survives app restarts
- **Duration Validation**: Uses Spotify's canonical duration to verify file versions
- **Infinite Enrichment**: Continues enriching older tracks in library (never stops improving metadata)

### 🧠 Audio Intelligence Layer (Phase 13)
**WHY:** Spectral analysis can PROVE quality, not just guess. A "FLAC" with a 16kHz frequency cutoff is a fake.

**HOW:** FFmpeg + Essentia sidecar for deep audio forensics:
- **Spectral Analysis**: Detects frequency cutoffs (128kbps = 16kHz brick wall, 320kbps = 21kHz)
- **Drop Detection**: Identifies buildups/drops for automatic cue points (DJ phrase mixing)
- **Waveform Generation**: High-fidelity peak+RMS data for seekbar visualization
- **Producer-Consumer Queue**: 2 workers analyze tracks as they download (no UI blocking)
- **45-second timeout**: 3× average analysis time, prevents hung processes

### 💿 DJ Professional Tools
**WHY:** Pro DJ software (Rekordbox, Serato) requires specific formats, key notation, and cue points.

**HOW:** Industry-standard export and harmonic matching:
- **Rekordbox XML Export**: Streaming generation (memory-efficient), correct URI format
- **Camelot Key Notation**: Automatic conversion (C Major = 8B) for harmonic mixing
- **Harmonic Match Service**: "Mixes Well With" - finds compatible tracks using Camelot Wheel theory
- **BPM Tolerance**: ±6% variance (128 BPM = 120-136 BPM range, professional DJ standard)
- **Monthly Drop**: One-click export of tracks added in last 30 days

---

## 🧠 The Brain: Forensic Ranking System

ORBIT doesn't just download files - it applies multi-layered forensics to detect fakes and find the best match.

### Phase 1: Pre-Download Forensics (Metadata Only)
**Problem:** Can't analyze audio before download in P2P, but CAN verify metadata consistency.

**Solution:** Mathematical verification using file size, bitrate, and duration:

```
COMPRESSION MISMATCH DETECTION:
Expected Size = (Bitrate × Duration) / 8

Example: "320kbps MP3, 5 minutes"
Expected: 320,000 bits/sec × 300 sec = 96,000,000 bits = 12 MB
Actual: 3 MB

Verdict: FAKE (20% of expected size = upscaled 64kbps)
```

### Tier System (Lowest to Highest Priority)

#### Trash Tier (Auto-Hidden)
**WHY:** Forensic check happens FIRST - prevents wasting time scoring mathematically impossible files
- Size < 70% of expected (fake 320kbps)
- FLAC < 2.5 MB/min (impossible for lossless, real FLAC = 5-10 MB/min)
- Duration mismatch >30 seconds (wrong version)

#### Bronze Tier (Last Resort)
- No free upload slot + queue >500 (30+ min wait or timeout)
- Bitrate <128kbps (audible compression artifacts)

#### Silver Tier (Acceptable)
- 192kbps+ MP3 (acceptable on casual listening)
- Available with reasonable queue (<100)

#### Gold Tier (High Quality)
- 320kbps MP3 (transparent encoding, <5% can distinguish from lossless in blind tests)
- Size math checks out (±10% tolerance for VBR/container overhead)

#### Diamond Tier (Perfect)
- FLAC/WAV (bit-perfect, 1411kbps uncompressed equivalent)
- Size verified (5-10 MB/min range)
- Free upload slot (instant download)
- Optional: BPM/Key match for DJ mixing

### Real-World Example
```
Search: "Deadmau5 - Strobe" (10:37 Extended Mix, 128 BPM)

Results:
┌─────────────────────────────────────────────────────────────┐
│ A: "Strobe.flac" | 112 MB | FLAC | Free Slot               │
│    Math: 112MB / 10.6min = 10.6 MB/min ✓ (lossless range)  │
│    VERDICT: 💎 DIAMOND (Score: 1.0) → SELECTED              │
├─────────────────────────────────────────────────────────────┤
│ B: "Strobe 320.mp3" | 25 MB | 320kbps | Queue: 5           │
│    Math: 320kbps × 637s = 25.5MB ✓ (within 10% tolerance)  │
│    VERDICT: 🥇 GOLD (Score: 0.85)                           │
├─────────────────────────────────────────────────────────────┤
│ C: "Strobe.flac" | 9 MB | FLAC | Free Slot                 │
│    Math: 9MB / 10.6min = 0.85 MB/min ✗ (FAKE!)            │
│    VERDICT: 🗑️ TRASH (Score: 0.1) → HIDDEN                 │
└─────────────────────────────────────────────────────────────┘

User sees only A and B. C is hidden (forensically proven fake).
```

### Phase 2: Post-Download Verification (Audio Analysis)
**Once downloaded**, ORBIT performs spectral analysis to confirm quality:

```
FREQUENCY CUTOFF DETECTION (FFmpeg + Essentia):

MP3 128kbps → 16kHz brick wall (encoder removes everything above)
MP3 320kbps → 21kHz content preserved
FLAC        → 22kHz full spectrum

Detection Method:
- Measure energy at 16kHz, 19kHz, 21kHz
- Real 320: -30 to -45 dB at 19kHz (natural rolloff)
- Fake 128: < -70 dB at 16kHz (hard cutoff = brick wall)

If "320kbps" shows 16kHz cutoff → Flag as "Upscaled Fake"
```

### Why Not Just Download Everything?
**Storage Cost:**
- Fake FLAC collection: 500 tracks × 50 MB = 25 GB
- Real FLAC collection: 500 tracks × 50 MB = 25 GB (but authentic)
- **Difference:** Both take space, but one sounds like 128kbps

**Bandwidth Cost:**
- Downloading 500 fake files = wasted 10+ hours, 25 GB bandwidth
- ORBIT filters fakes pre-download = save hours, disk space, frustration

---

## 🏗️ Architecture

### Tech Stack
- **UI Framework**: Avalonia (cross-platform XAML)
- **Backend**: .NET 8.0 (C#)
- **Database**: SQLite + Entity Framework Core
- **Audio Playback**: NAudio (hi-fi, low-latency)
- **Audio Analysis**: Essentia + FFmpeg sidecar (drop detection, cues, features)
- **P2P Network**: Soulseek.NET
- **Metadata**: TagLib# (audio tagging)

### Design Patterns & Why We Use Them

#### Producer-Consumer Pattern (AnalysisQueueService, SonicIntegrityService)
**WHY:** Audio analysis is CPU-intensive (80-100% per process). Running 50 analyses at once = UI freeze + thermal throttling.

**HOW:** Unbounded channel queue + 2 worker threads
- Downloads enqueue analysis requests (never blocks)
- 2 workers process queue with SemaphoreSlim throttling
- **Result:** Smooth UI, controlled CPU usage, 45s timeout prevents hung processes

#### Journal-First Pattern (CrashRecoveryJournal)
**WHY:** Crashes during downloads = lost progress. Crashes during tag writes = corrupt files.

**HOW:** Inspired by database transaction logs (prepare → log → execute → commit)
- Every download: write checkpoint to recovery journal
- Every 15 seconds: heartbeat update
- On crash/restart: read journal → resume from last checkpoint
- **Result:** Resume downloads mid-byte, zero data loss

#### Strategy Pattern (Search Ranking)
**WHY:** Different users have different priorities (quality vs speed, lossless vs efficient storage).

**HOW:** Swappable ranking algorithms (QualityFirst, BalancedMode, SpeedFirst)
- Each strategy = different weight distribution
- Easy to A/B test new ranking logic
- User can switch modes without code changes

#### Atomic Operations (SafeWrite Pattern)
**WHY:** Writing tags mid-crash = corrupt ID3 header = unplayable file.

**HOW:** Write to .tmp → verify → atomic move → cleanup
- TagLib writes to `file.mp3.tmp`
- Verify write succeeded (file size, no exceptions)
- Atomic filesystem move (OS guarantees all-or-nothing)
- **Result:** Either fully tagged or untouched, never corrupted

### Project Structure
```
ORBIT/
├── Views/Avalonia/          # UI components (XAML + code-behind)
├── ViewModels/              # Business logic & state management
├── Services/                # Core engines
│   ├── DownloadManager.cs       # Queue orchestration + heartbeat
│   ├── SearchResultMatcher.cs   # Ranking algorithm
│   ├── CrashRecoveryJournal.cs  # Recovery checkpoint logging
│   ├── MetadataEnrichmentOrchestrator.cs # Persistent enrichment queue consumer
│   ├── EnrichmentTaskRepository.cs # Task queue persistence logic
│   └── SonicIntegrityService.cs # Spectral analysis (Phase 8)
├── Models/                  # Data models & events
├── Configuration/           # Scoring constants, app settings
├── Utils/                   # String matching, filename normalization
└── DOCS/                    # Technical documentation
```

---

## 📊 Development Roadmap: The "Why" Behind Each Phase

### ✅ Phase 0: Foundation (Weeks 1-2)
**PROBLEM:** Need basic infrastructure before advanced features can exist.
- Built cross-platform UI (Avalonia XAML)
- Integrated Soulseek.NET for P2P networking
- SQLite database for library persistence
- Basic audio player (NAudio engine)

### ✅ Phase 1: Intelligent Ranking (Week 3)
**PROBLEM:** Soulseek returns 50+ results per search, but 80% are wrong versions or fake files.
- Implemented quality-first scoring (FLAC > 320kbps > 128kbps)
- Duration gating (10:37 Extended ≠ 3:30 Radio Edit)
- Filename noise stripping (`[YouTube-to-MP3]` → ignored)
- Path token search (find "128bpm" in `/Electronic/Techno/128bpm/track.mp3`)

### ✅ Phase 1A: Atomic File Operations (Week 4)
**PROBLEM:** App crashes mid-tag-write = corrupt MP3 headers = unplayable files.
- SafeWrite pattern: write to `.tmp` → verify → atomic move
- Disk space checking (prevent "out of space" mid-write)
- Timestamp preservation (keep original file dates)

### ✅ Phase 1B: Database Optimization (Week 4)
**PROBLEM:** 10,000+ library tracks = slow queries, UI lag on scroll.
- SQLite WAL mode (allows concurrent reads during writes)
- Index audit on `LibraryEntries` (40x speedup on filtered queries)
- 10MB cache + auto-checkpoint at 1000 pages

### ✅ Phase 2A: Crash Recovery (Week 5-6)
**PROBLEM:** Download 90% of 500MB FLAC → Windows Update reboot → start from 0%.
- Recovery journal with 15-second heartbeat checkpoints
- Idempotent resume logic (safe to "resume" already-complete downloads)
- Stall detection: 4 missed heartbeats (1 minute) = abort + retry
- Dead-letter queue: 3 failed retries = human intervention needed

### ✅ Phase 3A: Atomic Downloads (Week 7)
**PROBLEM:** P2P downloads stall randomly (peer disconnect, network drop, slot expiration).
- Adaptive timeout: 60s standard, 120s for >90% complete (give benefit of doubt)
- Peer blacklisting: user disconnects 3 times = skip in future searches
- Health monitor: tracks success rate per peer

### ✅ Phase 3B: Dual-Truth Schema (Week 8)
**PROBLEM:** Spotify says "128 BPM", file tag says "130 BPM", which is correct?
- IntegrityLevel enum: Pending → Bronze → Silver → Gold
- Spotify columns (trusted source) vs manual overrides (user corrections)
- Manual fields take precedence (user knows better than algorithm)

### ✅ Phase 4: Rekordbox Integration (Week 9)
**PROBLEM:** DJs spend 2+ hours manually importing playlists into Rekordbox.
- XmlWriter streaming (memory-efficient, handles 10,000+ tracks)
- Key conversion: Standard notation → Camelot (C Major = 8B)
- Monthly Drop feature: one-click export of last 30 days

### ✅ Phase 13: Audio Intelligence Upgrade (Week 18)
**PROBLEM:** Basic metadata (bitrate, sample rate) doesn't prove quality. Need spectral verification.
- Essentia integration for BPM, Key, Energy, Danceability (ML models)
- FFmpeg spectral analysis (frequency cutoff detection)
- Drop detection algorithm (identifies buildups for cue points)
- Waveform generation (peak + RMS for seekbar visualization)

### ✅ Phase 14: Forensic Core (Week 19)
**PROBLEM:** P2P is full of fakes. Need pre-download verification (can't analyze before downloading).
- MetadataForensicService: math-based size verification
- TieredTrackComparer: forensic check happens BEFORE quality scoring
- Trust score (0-100): 85+ = Golden Match, <40 = Avoid
- **Impact:** Fake files hidden from results, saves bandwidth + storage

### 🚧 Phase 2B: Code Quality (In Progress)
**PROBLEM:** Codebase grew organically, needs refactoring for maintainability.
- Extract reusable patterns (Strategy, Observer, Null Object)
- Parameter Object refactoring (reduce 8-parameter constructors)
- Inline WHY comments (explain thresholds, magic numbers)

### 🔮 Phase 5: Self-Healing Library (Planned Q2 2026)
**PROBLEM:** You downloaded 128kbps in 2020, now want 320kbps, but manually searching = tedious.
- **UpgradeScout**: Scans library for low-quality tracks
- **Auto-upgrade**: Background task searches for better versions
- **Smart replace**: Keep playlists/play counts, swap file atomically

### 🔮 Phase 6: Advanced UI Polish (Planned Q3 2026)
**PROBLEM:** Power users need transparency, beginners need simplicity.
- **Glass-box logging**: Show WHY a track was ranked #1 vs #5
- **Mission Control**: Dashboard for queue health, analysis progress
- **Forensic inspector**: Visualize spectral analysis (SPEK-style spectrograms)

### Timeline Summary
- **Weeks 1-4**: Foundation + intelligent search
- **Weeks 5-8**: Crash recovery + reliability
- **Weeks 9-12**: DJ integrations (Rekordbox, harmonic matching)
- **Weeks 13-19**: Audio intelligence + forensics
- **Q2 2026**: Self-healing + automation
- **Q3 2026**: UI polish + transparency

---

## 🚀 Quick Start

### Prerequisites
- **Windows 10/11** (macOS/Linux support in progress)
- .NET 8.0 SDK ([Download](https://dotnet.microsoft.com/download))
- Soulseek account (Free at [slsknet.org](https://www.slsknet.org))
- **Optional**: FFmpeg (for Phase 8 spectral analysis features)

### Installation
```bash
git clone https://github.com/MeshDigital/ORBIT.git
cd ORBIT
dotnet restore
dotnet build
dotnet run
```

### First-Time Setup
1. Launch ORBIT
2. Navigate to **Settings**
3. Enter your Soulseek credentials
4. **Optional**: Connect Spotify (PKCE auth - no API keys required)
5. Import a playlist via URL or search directly

### FFmpeg Setup (Optional - for Sonic Integrity)
- **Windows**: Download from [ffmpeg.org](https://ffmpeg.org), add to PATH
- **macOS**: `brew install ffmpeg`
- **Linux**: `sudo apt install ffmpeg` or equivalent

---

## 📖 Documentation

### Core Documentation
- [**Architecture Overview**](DOCS/ARCHITECTURE.md) - Design decisions and patterns
- [**The Brain: Smart Gating**](DOCS/THE_BRAIN_SMART_GATING.md) - Duration validation logic
- [**Metadata Persistence**](DOCS/METADATA_PERSISTENCE.md) - DJ-ready tagging explained
- [**Ranking Examples**](DOCS/RANKING_EXAMPLES.md) - Real-world scoring scenarios
- [**Spotify Auth**](DOCS/SPOTIFY_AUTH.md) - PKCE implementation details

### Technical Artifacts
- [**TODO.md**](TODO.md) - Active development tasks
- [**ROADMAP.md**](ROADMAP.md) - Long-term vision and priorities
- [**CHANGELOG.md**](CHANGELOG.md) - Version history

---

## 🤝 Contributing

Contributions are welcome! Whether you're fixing bugs, adding features, or improving documentation, your help is appreciated.

### Development Workflow
1. Fork the repository
2. Create a feature branch (`git checkout -b feature/your-feature`)
3. Commit your changes (`git commit -m 'feat: add your feature'`)
4. Push to your branch (`git push origin feature/your-feature`)
5. Open a Pull Request

### Code Standards
- Follow C# naming conventions
- Write XML documentation for public APIs
- Include unit tests for new features
- Keep commits atomic and well-described

---

## 🔧 Built With

- [Microsoft ML.NET](https://dotnet.microsoft.com/en-us/apps/machinelearning-ai/ml-dotnet) - On-device machine learning
- [Avalonia UI](https://avaloniaui.net/) - Cross-platform XAML framework
- [Entity Framework Core](https://docs.microsoft.com/ef/) - Object-relational mapping
- [Soulseek.NET](https://github.com/jpdillingham/Soulseek.NET) - P2P networking
- [TagLib#](https://github.com/mono/taglib-sharp) - Audio metadata
- [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) - Media playback
- [Xabe.FFmpeg](https://ffmpeg.xabe.net/) - Audio analysis

---

## 📜 License

GPL-3.0 - See [LICENSE](LICENSE) for details.

---

## 💬 Contact

- **Issues**: [Report bugs or request features](https://github.com/MeshDigital/ORBIT/issues)
- **Discussions**: [Join the community](https://github.com/MeshDigital/ORBIT/discussions)

---

**Built for music enthusiasts who demand quality and reliability** | **Since 2024**
