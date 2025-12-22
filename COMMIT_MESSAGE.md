# Commit Message

## Title
feat: Implement Quality Guard & The Brain 2.0 + Fix 21 Build Errors

## Description

### Quality Guard Features Added
- **Fuzzy Normalization**: Intelligent string matching with feat./ft. normalization, special character handling, and whitespace collapsing
- **Adaptive Relaxation Strategy**: Progressive quality threshold widening (Tier 1: 256kbps, Tier 2: highest available)
- **VBR Fraud Detection**: Excludes upscaled/fake high-quality files from search results

### Configuration & UI
- Added 4 new settings to AppConfig: EnableFuzzyNormalization, EnableRelaxationStrategy, EnableVbrFraudDetection, RelaxationTimeoutSeconds
- Created "Quality Guard & The Brain" section in Settings UI with toggles and timeout slider
- Exposed properties in SettingsViewModel for UI binding

### Core Logic Integration
- SearchResultMatcher: Integrated fuzzy normalization in CalculateSimilarity method
- DownloadDiscoveryService: Implemented 2-tier relaxation strategy in FindBestMatchAsync
- ResultSorter: Wired VBR fraud detection flag, excludes suspicious files (score = -∞)
- App.axaml.cs: Initialize ResultSorter with config on startup

### Build Fixes (21 errors resolved)
**C# Errors (19):**
- DashboardService: Fixed PlaylistJobEntity mapping (Source/Name/Status → SourceTitle/SourceType)
- DownloadManager & DashboardService: Added Microsoft.EntityFrameworkCore using directives
- SettingsViewModel: Commented out obsolete SelectedRankingMode (RankingPreset property doesn't exist)
- App.axaml.cs: Removed RankingPreset references, using CustomWeights directly
- SpotifyEnrichmentService: Fixed Spotify API v7.2.1 compatibility (Browse.GetRecommendations, SeedTracks initialization)
- HomeViewModel: Commented out DownloadSpeed property (CurrentSpeedText doesn't exist)
- DatabaseService: Restored missing PendingOrchestrations table schema check
- PlaylistTrackEntity: Added missing Bitrate and Format properties
- DatabaseService: Added schema patches for new Bitrate/Format columns

**XAML Errors (2):**
- HomePage.axaml: Removed invalid Spacing property from Grid elements (lines 28, 127)
- DownloadsPage.axaml: Fixed XML structure and tag nesting

### Files Modified
**Configuration:**
- Configuration/AppConfig.cs

**ViewModels:**
- ViewModels/SettingsViewModel.cs
- ViewModels/HomeViewModel.cs

**Services:**
- Services/SearchResultMatcher.cs
- Services/DownloadDiscoveryService.cs
- Services/ResultSorter.cs
- Services/DatabaseService.cs
- Services/DashboardService.cs
- Services/DownloadManager.cs
- Services/SpotifyEnrichmentService.cs
- App.axaml.cs

**Data Models:**
- Data/TrackEntity.cs

**UI:**
- Views/Avalonia/SettingsPage.axaml
- Views/Avalonia/DownloadsPage.axaml
- Views/Avalonia/HomePage.axaml

### Build Status
✅ **0 Errors | 15 Warnings (non-critical)**

### Testing Required
- Settings persistence across app restarts
- Relaxation strategy with obscure tracks
- VBR fraud detection with known fake files

---

## Git Commands to Run

```bash
# Stage all changes
git add -A

# Commit with this message
git commit -m "feat: Implement Quality Guard & The Brain 2.0 + Fix 21 Build Errors

- Add Fuzzy Normalization for intelligent track matching
- Implement Adaptive Relaxation Strategy (2-tier quality fallback)
- Integrate VBR Fraud Detection to exclude fake files
- Add Quality Guard settings UI section with 4 new controls
- Fix 19 C# build errors (entity mappings, missing usings, obsolete properties, Spotify API compatibility)
- Fix 2 XAML errors (invalid Grid.Spacing properties)
- Add Bitrate/Format properties to PlaylistTrackEntity
- Restore missing database schema checks

Build: ✅ 0 errors, 15 warnings"

# Push to remote
git push origin main
```

---

## Alternative Short Commit Message

```bash
git commit -m "feat: Quality Guard & Brain 2.0 + build fixes

- Fuzzy normalization, adaptive relaxation, VBR fraud detection
- New settings UI section with 4 controls
- Fixed 21 build errors (19 C#, 2 XAML)
- Build: ✅ 0 errors"
```
