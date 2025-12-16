# 🎵 QMUSICSLSK – The AI-Powered Spotify Clone for Soulseek

> **"I'm not a developer. I'm a Project Manager guiding AI agents to build the ultimate music app."**  
> *– A non-developer's journey to building complex software through AI direction*

[![Platform](https://img.shields.io/badge/platform-Windows%20(in%20dev)%20%7C%20macOS%2FLinux%20(planned)-blue)](https://github.com/MeshDigital/QMUSICSLSK)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple)](https://dotnet.microsoft.com/)
[![UI](https://img.shields.io/badge/UI-Avalonia-orange)](https://avaloniaui.net/)
[![License](https://img.shields.io/badge/license-GPL--3.0-green)](LICENSE)
[![Status](https://img.shields.io/badge/status-Active%20Development-brightgreen)](https://github.com/MeshDigital/QMUSICSLSK)

---

## 🚀 What Is This?

A **cross-platform music downloader and library manager** that turns Soulseek into your personal Spotify. Import playlists, download tracks, organize your library, and play music—all with a beautiful, responsive UI.

**Platform Status:**
- 🚧 **Windows 10/11**: In active development - core features working (import, download, library, player)
- 🔮 **macOS/Linux**: Built on cross-platform components (Avalonia UI), not yet tested

**Current Goal**: Stable, feature-complete Windows version  
**End Goal**: True multi-platform support (Windows, macOS, Linux)

**The Story**: This entire project is built by a **non-developer serving as Project Manager**. I don't write the code—I guide AI agents (Claude, Gemini, ChatGPT) to build it. I define the vision, manage the roadmap, and direct the agents through successful development, proving that you don't need to be a coder to build complex software.

---

## ✨ Current Features (Phase 1-3 Complete!)

### 🎯 Phase 1: Foundation & Critical Fixes ✅
- ✅ **Cross-Platform UI**: Migrated from WPF to Avalonia (Windows/macOS/Linux)
- ✅ **Spotify Integration**: Import playlists directly from Spotify URLs
- ✅ **CSV Import**: Bulk import from CSV files
- ✅ **Smart Download Manager**: Concurrent downloads with progress tracking
- ✅ **SQLite Library**: Persistent music library with metadata
- ✅ **Built-in Player**: LibVLC-powered audio playback
- ✅ **Drag & Drop**: Organize tracks between playlists
- ✅ **File Path Resolution**: Auto-fix broken file paths

### 📥 Phase 2: Advanced Import Features ✅
- ✅ **Paste Tracklist**: Copy/paste tracklists from YouTube, SoundCloud, etc.
- ✅ **Timestamp Removal**: Auto-removes timestamps like `[00:00]` from pasted text
- ✅ **Advanced Search Filters**: Bitrate, file format, ranking presets
- ✅ **Import Preview**: Review tracks before downloading

### 📱 Phase 3: Responsive UI & Player ✅
- ✅ **Responsive Layout**: Works from 360px (mobile) to 4K displays
- ✅ **Auto-Collapse Navigation**: Sidebar collapses on small screens (<800px)
- ✅ **Adaptive DataGrids**: Columns hide/show based on screen size
- ✅ **Shuffle & Repeat**: Full playback queue management
- ✅ **Dynamic Player Docking**: Toggle player between bottom bar and sidebar (in progress)

---

## 🔮 Future Roadmap

### Phase 4: Quality & Polish (Planned)
- Error handling & user-friendly messages
- Performance optimization
- Unit tests & documentation

### Phase 5: Self-Healing Library (The Big One!)
- **🧬 Acoustic Fingerprinting**: Detect duplicates by audio DNA, not filename
- **⬆️ Auto-Upgrade**: "You have a 128kbps MP3. Replace with FLAC?"
- **🔧 Broken Link Repair**: Moved files? App finds them automatically
- **🎭 Fake Quality Detection**: Detect upsampled 320kbps files
- **📁 USB/Folder Import**: Import existing libraries with quality analysis
- **🏥 Library Health Dashboard**: View duplicates, low-quality tracks, corrupt files

**Inspired by**: `audio-duplicates` (C++ performance) + `rekordbox-library-fixer` (TypeScript intelligence)

### Platform Roadmap
- ✅ **Phase 0**: Cross-platform foundation (Avalonia UI migration complete)
- 🚧 **Current**: Windows 10/11 in active development - core features functional
- 🔮 **Next**: Complete Windows feature set and stability
- 🔮 **Future**: macOS testing and Linux support

**Strategy**: Build on cross-platform components (Avalonia, .NET 8.0) from day one, ensuring Windows stability first, then expand to macOS/Linux with minimal code changes.

---

## 🎨 Screenshots

### Main Interface (Responsive Design)
*Desktop view with player sidebar*

### Import Preview
*Review tracks before downloading*

### Library Health Dashboard (Coming Soon)
*Duplicate detection and quality analysis*

---

## 🚀 Quick Start

### Prerequisites
- **Windows 10/11** (primary platform - in active development)
- .NET 8.0 SDK ([Download](https://dotnet.microsoft.com/download))
- Soulseek account (free at [slsknet.org](https://www.slsknet.org))

> **Note**: This is an **active development project**. Core features work (import, download, library, player), but expect bugs and incomplete features. Built with Avalonia (cross-platform framework) for future macOS/Linux support.

### Installation

```bash
# Clone the repository
git clone https://github.com/MeshDigital/QMUSICSLSK.git
cd QMUSICSLSK

# Restore dependencies
dotnet restore

# Build the application
dotnet build

# Run it!
dotnet run
```

### Spotify API Setup (Simplified!)
 
 **Good News!** You no longer need to create a developer account manually.
 
 1. Go to **Settings** in the app.
 2. Scroll to **Spotify Integration**.
 3. Click **"Connect with Spotify"**.
 4. Log in via your browser.
 
 **That's it!** You can now import playlists and your **Liked Songs** directly.
 
 > **Advanced Users**: If you prefer to use your own Client ID, you can still expand "Advanced Options" in Settings to provide your own credentials.
 
 > **Note**: Your credentials are stored securely using Windows Data Protection API (DPAPI). They never leave your computer.

**Detailed Guide**: [How to Generate Spotify API Key](https://codewolfy.com/how-to-generate-spotify-api-key/)

### First-Time Setup

1. **Sign in** with your Soulseek credentials (stored securely)
2. **Configure Spotify API** (see above) for playlist import
3. **Set download directory** in Settings
4. **Import music** from Spotify, CSV, or paste a tracklist
5. **Start downloading** and enjoy!

---

## 🎵 How It Works
1. **Import** → Paste a Spotify playlist URL or tracklist from YouTube
2. **Preview** → Review imported tracks, adjust search queries
3. **Download** → Tracks are queued and downloaded automatically
4. **Organize** → Drag tracks between playlists in Library view
5. **Play** → Built-in player with shuffle, repeat, and queue management

### Smart Features
- **Timestamp Removal**: Paste `[00:00] Artist - Title` → Auto-cleans to `Artist - Title`
- **Duplicate Detection**: Won't download the same track twice
- **Bitrate Filtering**: Only download 320kbps or FLAC
- **Auto-Retry**: Failed downloads retry automatically

---

## 🤖 The AI Development Story

### "I'm Not a Real Developer"

This project is a **proof of concept** that you don't need to be a "real developer" to build complex software. I act as the **Project Manager and Lead Architect**, while AI agents serve as the engineering team. Every line of code, every architectural decision, every bug fix—**all created through AI execution under human guidance**.

**Tools Used:**
- **Claude** (Anthropic) - Primary coding assistant
- **Gemini** (Google) - Architecture planning
- **ChatGPT** (OpenAI) - Problem-solving

**Development Process:**
1. Describe feature in plain English
2. AI generates implementation plan
3. Review, iterate, refine
4. AI writes the code
5. Test, debug, repeat

**Result**: A **cross-platform music app** with features rivaling commercial software, built by someone who "vibes their way through coding."

### Why This Matters

This project demonstrates:
- **AI democratizes software development** - You don't need a CS degree
- **Non-developers can build real products** - Just need vision and persistence
- **AI pair programming works** - When you know what you want, AI helps you build it

---

## 🏗️ Architecture

### Tech Stack
- **UI Framework**: Avalonia (cross-platform XAML)
- **Backend**: .NET 8.0 (C#)
- **Database**: SQLite + Entity Framework Core
- **Audio**: LibVLC (VLC media player core)
- **Network**: Soulseek.NET
- **Pattern**: MVVM (Model-View-ViewModel)

### Project Structure
```
QMUSICSLSK/
├── Views/Avalonia/          # UI (XAML + code-behind)
├── ViewModels/              # Business logic
├── Services/                # Download, import, player services
├── Models/                  # Data models
├── Configuration/           # App config & settings
└── Database/                # SQLite + EF Core
```

---

## 📊 Progress Tracker

### Completed Features
- [x] Cross-platform UI (Avalonia)
- [x] Spotify playlist import
- [x] CSV import
- [x] Paste tracklist feature
- [x] Advanced search filters
- [x] Responsive layout (360px - 4K)
- [x] Player queue (shuffle, repeat)
- [x] Drag & drop organization

### In Progress
- [ ] Dynamic player docking (bottom bar / sidebar)
- [ ] Queue visualization panel
- [ ] "Add to Queue" from Library/Search

### Planned (Phase 5)
- [ ] Acoustic fingerprinting
- [ ] Duplicate detection
- [ ] Auto-upgrade (MP3 → FLAC)
- [ ] Broken link repair
- [ ] USB/folder import with quality analysis
- [ ] Library health dashboard

**Total Tasks**: 38 planned for Phase 5 (Self-Healing Library)

---

## 🐛 Troubleshooting

### Audio Playback Issues
- **Problem**: "Player Initialization Failed"
- **Solution**: Ensure `libvlc` folder exists in output directory
- **Check**: Console output for LibVLC errors

### Import Not Working
- **Problem**: Spotify import fails
- **Solution**: Check Spotify API credentials in Settings
- **Alternative**: Use CSV import or paste tracklist

### UI Not Responsive
- **Problem**: Window too small
- **Solution**: Minimum window size is 360x500px
- **Note**: Navigation auto-collapses below 800px width

See [TROUBLESHOOTING.md](TROUBLESHOOTING.md) for more details.

---

## 🤝 Contributing

**This is an AI-assisted project**, so contributions are welcome from both humans and AI enthusiasts!

### How to Contribute
1. Fork the repository
2. Create a feature branch
3. Use AI to help implement your feature
4. Submit a pull request with detailed description

### Contribution Ideas
- UI/UX improvements
- Bug fixes
- New import sources (YouTube Music, Apple Music, etc.)
- Performance optimizations
- Documentation improvements

---

## 📝 Documentation

- [FEATURES.md](FEATURES.md) - Complete feature list
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) - Common issues
- [ARCHITECTURE.md](ARCHITECTURE.md) - System design
- [DEVELOPMENT.md](DEVELOPMENT.md) - Developer guide

---

## 🔒 Security & Privacy

- **Passwords**: Encrypted using Windows Data Protection API (DPAPI)
- **Local Storage**: All data stored locally in SQLite
- **No Telemetry**: Zero tracking, zero analytics
- **Open Source**: Audit the code yourself

---

## 📜 License

GPL-3.0 - See [LICENSE](LICENSE) for details.

---

## 🙏 Acknowledgments

### Technology
- [Avalonia UI](https://avaloniaui.net/) - Cross-platform XAML framework
- [Soulseek.NET](https://github.com/jpdillingham/Soulseek.NET) - Soulseek client library
- [LibVLC](https://www.videolan.org/vlc/libvlc.html) - Audio playback
- [SoundFingerprinting](https://github.com/AddictedCS/soundfingerprinting) - Acoustic fingerprinting (planned)

### AI Assistants
- **Claude** (Anthropic) - Primary development partner
- **Gemini** (Google) - Architecture & planning
- **ChatGPT** (OpenAI) - Problem-solving

### Inspiration
- [audio-duplicates](https://github.com/Phidelux/audio-duplicates) - C++ performance patterns
- [rekordbox-library-fixer](https://github.com/m4b3l/rekordbox-library-fixer) - Library health concepts

---

## 🌟 Star History

If this project inspires you to build something with AI, give it a star! ⭐

---

## 💬 Contact

- **GitHub Issues**: [Report bugs or request features](https://github.com/MeshDigital/QMUSICSLSK/issues)
- **Discussions**: [Join the conversation](https://github.com/MeshDigital/QMUSICSLSK/discussions)

---

## 🎯 Project Status

**Current Version**: 1.0.0-beta  
**Status**: Active Development  
**Last Updated**: December 2025

### 🔄 Recent Changes (v1.0.0)

- ✅ **Secure & Stable**: Fixed critical startup crashes and implemented secure credential storage.
- ✅ **Architectural Overhaul**: Refactored `MainViewModel` into `SearchViewModel` and `ConnectionViewModel`, ensuring modularity and stability.
- ✅ **New Feature**: "Add to Queue" directly from Library view.
- ✅ Fixed database concurrency exception in drag-and-drop
- ✅ Added UI refresh after playlist modifications
- ✅ Implemented file path resolution from DownloadManager
- ✅ Added taskbar icon with transparent background
- ✅ **Cross-Platform UI**: Migrated to Avalonia (Windows/macOS/Linux)
- ✅ **New Feature**: **Spotify Single Sign-On (SSO)** - One-click login.
- ✅ **New Feature**: **Smart Playlists** (Recently Added, Most Played, Failed Downloads).
- ✅ **New Feature**: **Import "Liked Songs"** - Sync your Spotify library directly.
- ✅ **Secure**: Fixed critical startup crashes and implemented secure credential storage.
- ✅ **Architectural Overhaul**: Refactored `MainViewModel` into `SearchViewModel` and `ConnectionViewModel`.
- ✅ **UI**: Added "Add to Queue" from Library view.
- ✅ Fixed database concurrency exception in drag-and-drop
- ✅ Added UI refresh after playlist modifications
- ✅ Implemented file path resolution from DownloadManager
- 📋 Planning Phase 5: Self-Healing Library

---

## 🔥 The Bottom Line

**This is what happens when a non-developer gets access to AI and refuses to give up.**

From "I don't know how to code" to "I'm building a cross-platform music app with acoustic fingerprinting"—all through the power of AI pair programming and sheer determination.

**If I can do it, you can too.** 🚀

---

> **Note**: The legacy Windows-only WPF version is available on the `wpf-legacy` branch.  
> This branch is no longer actively maintained.

**Built with ❤️ and AI** | **Vibing since 2024**
