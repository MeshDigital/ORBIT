![ORBIT Banner](assets/orbit_banner.png)

# 🛰️ ORBIT – Organized Retrieval & Batch Integration Tool

> **"Intelligent music discovery meets DJ-grade metadata management."**  
> *The professional Soulseek client with a brain*

[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-blue)](https://github.com/MeshDigital/ORBIT)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple)](https://dotnet.microsoft.com/)
[![UI](https://img.shields.io/badge/UI-Avalonia-orange)](https://avaloniaui.net/)
[![License](https://img.shields.io/badge/license-GPL--3.0-green)](LICENSE)
[![Status](https://img.shields.io/badge/status-Active%20Development-brightgreen)](https://github.com/MeshDigital/ORBIT)

---

## 🚀 What Is ORBIT?

**ORBIT** is a next-generation Soulseek client that combines **intelligent search ranking**, **musical metadata enrichment**, and **DJ-ready tagging** into a single, professional tool.

Unlike traditional P2P clients that blindly download the first result, ORBIT uses **"The Brain"** – a sophisticated ranking system that:
- ✅ **Prioritizes quality** (FLAC > 320kbps > 128kbps)
- ✅ **Detects fake files** (VBR validation, filesize verification)
- ✅ **Matches musical intent** (BPM/Key matching for DJs)
- ✅ **Validates versions** (Radio Edit vs Extended Mix)

**The Result:** Your library is filled with the *exact* files you want, not just the first match.

---

## ✨ Core Features

### 🎯 Intelligent Search Ranking
- **Quality-Gated Intelligence**: Bitrate is primary, BPM/Key are tiebreakers
- **VBR Validation**: Detects fake upconverted files (128→320, MP3→FLAC)
- **Filename Noise Stripping**: Ignores `[uploader-tag]`, `[Official Video]`, `(Remastered)`
- **Path-Based Token Search**: Finds BPM/Key in directory names with confidence decay
- **Duration Gating**: Ensures you get the Radio Edit when you want it, not the 10-minute Club Mix

### 🎧 Spotify Integration
- **Playlist Import**: Paste a Spotify URL, ORBIT finds the files on Soulseek
- **Metadata Enrichment**: Automatic BPM, Key, Album Art, and Genre tagging
- **Canonical Duration**: Uses Spotify's duration to validate file versions
- **"Liked Songs" Support**: Import your entire Spotify library

### 💿 DJ-Ready Metadata
- **Camelot Key Notation**: Automatic key detection and tagging (e.g., "8A")
- **BPM Persistence**: Writes BPM to file tags (ID3v2.4 for MP3, Vorbis for FLAC)
- **Custom Tags**: Spotify IDs embedded for future self-healing features
- **Rekordbox Compatible**: Tags work seamlessly in Rekordbox, Serato, Traktor

### 🎨 Modern UI
- **Spotify-like Interface**: Beautiful, dark-themed, responsive design
- **Real-Time Progress**: Live download tracking with queue management
- **Library Management**: Organize playlists, drag-and-drop tracks
- **Built-in Player**: Preview tracks before downloading

---

## 🧠 The Brain: How ORBIT Thinks

ORBIT's ranking system uses a **multi-tiered scoring algorithm** inspired by `slsk-batchdl`:

### Tier 0: Availability (Speed)
- Free upload slot: +2000 pts
- Queue length penalty: -10 pts per item
- Long queue penalty: -500 pts for >50 items

### Tier 1: Quality Floor (Primary Discriminator)
- **Lossless (FLAC)**: 450 pts
- **High (320kbps)**: 300 pts
- **Medium (192kbps)**: 150 pts
- **Low (128kbps)**: 64 pts (proportional)

### Tier 2: Musical Intelligence (Tiebreaker)
- BPM match: +100 pts
- Key match: +75 pts
- Harmonic key: +50 pts

### Tier 3: Guard Clauses (Strict Gating)
- Duration mismatch: **-∞** (hidden)
- Fake file detected: **-∞** (hidden)
- VBR validation failed: **-∞** (hidden)

**Example:**
```
Search: "Deadmau5 - Strobe" (128 BPM, 10:37)

File A: FLAC, 1411kbps, "Strobe (128bpm).flac"
→ Quality: 450 + BPM: 100 = 550 pts ✅ WINNER

File B: MP3, 320kbps, "Strobe.mp3"
→ Quality: 300 + BPM: 50 = 350 pts

File C: MP3, 128kbps, "Strobe (128bpm).mp3"
→ Quality: 64 + BPM: 100 = 164 pts

File D: "FLAC", 1411kbps, "Strobe.flac" (9 MB - FAKE)
→ VBR Validation: FAIL = -∞ (HIDDEN)
```

---

## 🏗️ Architecture

### Tech Stack
- **UI Framework**: Avalonia (cross-platform XAML)
- **Backend**: .NET 8.0 (C#)
- **Database**: SQLite + Entity Framework Core
- **Audio**: LibVLC (VLC media player core)
- **Soulseek**: Soulseek.NET library

### Design Patterns
ORBIT follows professional software engineering patterns:
- **Strategy Pattern**: Swappable ranking modes (Audiophile vs DJ)
- **Observer Pattern**: Event-driven progress updates
- **Command Pattern**: Undo/Redo for library actions (planned)
- **Null Object Pattern**: Clean metadata handling
- **Template Method**: Consistent import provider workflow

### Project Structure
```
ORBIT/
├── Views/Avalonia/          # UI (XAML + code-behind)
├── ViewModels/              # Business logic & State
├── Services/                # Core engines (Download, Ranking, Metadata)
├── Models/                  # Data models
├── Configuration/           # Scoring constants, app config
├── Utils/                   # Filename normalization, string matching
└── DOCS/                    # Technical documentation
```

---

## 📊 Roadmap

### ✅ Phase 0: Foundation (Complete)
- [x] Cross-platform UI (Avalonia)
- [x] Spotify Playlist & "Liked Songs" Import
- [x] Soulseek Download Manager
- [x] Local Library Database
- [x] Built-in Audio Player
- [x] Metadata Enrichment (BPM, Key, Album Art)
- [x] DJ-Ready Tagging (ID3v2.4, Vorbis)

### ✅ Phase 1: Intelligent Search Ranking (Complete)
- [x] Quality-gated intelligence (bitrate primary, BPM tiebreaker)
- [x] Filename noise stripping
- [x] Path-based token search
- [x] VBR validation (anti-fraud)
- [x] Duration gating

### ✅ Phase 2: Code Quality & Maintainability (In Progress)
- [x] Replace Magic Numbers (ScoringConstants)
- [x] Extract Method (Composing Methods)
- [ ] Introduce Parameter Object (ScoringContext)
- [ ] Strategy Pattern (Ranking Modes)
- [ ] Observer Pattern (Event-driven architecture)
- [ ] Null Object Pattern (Metadata handling)

### 🚧 Phase 3: USB/Local Import (Planned)
- [ ] Import existing music collections
- [ ] Duplicate detection via acoustic fingerprinting
- [ ] Metadata synchronization

### 🔮 Phase 4: Performance Optimization (Planned)
- [ ] Multi-core library scanning
- [ ] Background worker architecture
- [ ] Hardware acceleration
- [ ] Memory-mapped files

### 🔮 Phase 5: Self-Healing Library (Future Vision)
- [ ] Automatic quality upgrades (128kbps → FLAC)
- [ ] Rekordbox cue point preservation
- [ ] Beatgrid realignment via cross-correlation
- [ ] Two-way Rekordbox XML sync

---

## 🤖 AI-Augmented Development

**ORBIT is built through AI collaboration.**

This project demonstrates that you don't need to be a coder to build professional-grade software. The development process:

1. **Vision**: Define the feature ("I want files ranked by quality AND BPM")
2. **Architecture**: AI agents propose design patterns (Strategy, Observer, etc.)
3. **Implementation**: AI writes C# code following best practices
4. **Iteration**: Refine through testing and user feedback

**Result**: A production-ready application built by a Product Manager directing AI intelligence.

---

## 🚀 Quick Start

### Prerequisites
- **Windows 10/11** (macOS/Linux support planned)
- .NET 8.0 SDK ([Download](https://dotnet.microsoft.com/download))
- Soulseek Login (Free at [slsknet.org](https://www.slsknet.org))

### Installation
```bash
git clone https://github.com/MeshDigital/ORBIT.git
cd ORBIT
dotnet restore
dotnet build
dotnet run
```

### Spotify Setup
1. Go to **Settings > Connect with Spotify**
2. Click "Sign In" (PKCE flow - no API keys needed)
3. Authorize ORBIT in your browser
4. Done! Import playlists via URL

---

## 📖 Documentation

- [**The Brain: Smart Duration Gating**](DOCS/THE_BRAIN_SMART_GATING.md) - How ORBIT validates file versions
- [**Metadata Persistence**](DOCS/METADATA_PERSISTENCE.md) - DJ-ready tagging explained
- [**Ranking Examples**](DOCS/RANKING_EXAMPLES.md) - Concrete scoring scenarios
- [**Spotify Auth**](DOCS/SPOTIFY_AUTH.md) - PKCE implementation details

---

## 🤝 Contributing

**Contributions welcome from humans and AI!**

- **Human Developers**: Pick up issues tagged `good-first-issue`
- **AI Agents**: Prioritize robustness, testability, and design patterns

### Development Workflow
1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'feat: add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

---

## 📜 License

GPL-3.0 - See [LICENSE](LICENSE) for details.

---

## 💬 Contact

- **GitHub Issues**: [Report bugs](https://github.com/MeshDigital/ORBIT/issues)
- **Discussions**: [Join the chat](https://github.com/MeshDigital/ORBIT/discussions)

---

**Built with ❤️ and AI** | **Intelligent music discovery since 2024**
