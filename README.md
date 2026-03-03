<div align="center">
  <h1>🛰️ ORBIT</h1>
  <p><strong>Organized Retrieval & Batch Integration Tool</strong></p>
  <p>A technical, reliability-focused P2P client and music workstation</p>
</div>

ORBIT interfaces with the Soulseek network but prioritizes strict file verification, structural analysis, and metadata fidelity over blind downloading. Originally designed as a "High-Fidelity Downloader," the project is actively evolving into a "Creative Workstation" (DAW lite), integrating ML audio analysis to support DJ and curation workflows.

> **LEGAL & PRIVACY NOTICE**
> ORBIT connects to the Soulseek P2P network. Your IP address is visible to other peers. We strongly advise using a VPN. This tool is provided for educational purposes and managing legally acquired content.

---

## Technical Overview

Traditional file sharing clients treat music as opaque blobs. ORBIT analyzes content at ingestion and post-download stages to ensure structural integrity and enrich files with musical metadata.

### Core Architecture
- **UI Framework**: Avalonia (cross-platform XAML)
- **Backend & Networking**: .NET 8.0 (C#) / Soulseek.NET
- **Database**: SQLite (WAL mode, optimized for concurrent reads/writes)
- **Audio Processing**: NAudio, Xabe.FFmpeg, Essentia
- **Machine Learning**: Microsoft ML.NET (LightGBM classifiers for 512-dimensional Essentia BLOBs)

### Key Features
1. **Pre-Download Heuristics**: Calculates expected file size vs actual size to preemptively filter low-quality upscales disguised as 320kbps.
2. **Audio Spectral Analysis**: FFmpeg and Essentia sidecars analyze audio for frequency cutoffs to detect transcode fraud.
3. **Spotify Crate Sync**: An autonomous background daemon that monitors Spotify playlists and deduplicates tracks using a 2-Tier SQL+Fuzzy matching engine.
4. **Acapella Factory**: High-performance AI stem separation utilizing 30-second memory-safe chunking and ONNX model isolation.
5. **Creative Workstation Capabilities**: SkiaSharp-powered hardware-accelerated visualizers with "Liquid" Lerp playhead smoothing for professional-grade prep work.
6. **Rekordbox XML Exporter**: Production-ready streaming export engine with XOR binary parity and Windows path normalization for seamless CDJ integration.

---

## Current Status & Recent Updates (March 2026)

ORBIT has transitioned from a file utility into a professional-grade audio workstation.

**Latest High-Fidelity Deliverables**:
- **Studio Pro Unification**: Unified the DAW-grade Skia visualizers and Lerp-based playhead interpolation across the global library.
- **Acapella Factory**: Launched a memory-safe batch stem separation engine for surgical track preparation.
- **Rekordbox XML Exporter**: Implemented a memory-safe streaming export pipeline for massive libraries, resolving historical Windows path encoding issues.
- **Spotify Crate Sync**: Added a background daemon for persistent playlist monitoring with native 2-tier deduplication.

**Next Steps**:
- Advanced Drop/Phrase detection for automatic Cue-Point placement.
- Surgical Processing Integration (Direct FFmpeg trimming and bit-depth conversion).
- Mobile Companion (Wireless setlist viewing).

---

## Installation & Setup

1. Clone and build:
   ```bash
   git clone https://github.com/MeshDigital/ORBIT.git
   cd ORBIT
   dotnet restore
   dotnet build
   dotnet run
   ```
2. **First Run**: Configure your Soulseek credentials in the Settings menu.
3. **Dependencies**: Requires `ffmpeg` installed locally and in your PATH for spectral analysis services to function correctly.

## Project Structure
- `Views/Avalonia/` - XAML views and controls
- `ViewModels/` - Reactive state logic
- `Services/` - Core daemon services (DownloadManager, SonicIntegrityService, MissionControl)
- `Models/` - Database schemas and event records
- `DOCS/` & `TODO.md` - Advanced strategy plans and backlog items

## Contributing
Contributions for code refactoring, performance improvements, and algorithm optimization are welcome. Please ensure new logic adheres to atomic state patterns to prevent mid-download corruption.

License: GPL-3.0
