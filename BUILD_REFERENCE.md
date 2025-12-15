# SLSKDONET - Quick Build & Reference Guide

## Project Structure Overview

```
SLSKDONET/
├── Models/                          # Data models
│   ├── Track.cs                    # Search result
│   ├── DownloadJob.cs              # Download state
│   ├── SearchQuery.cs              # Query with parsing
│   └── FileCondition.cs            # Filtering system
│
├── Services/                        # Business logic
│   ├── SoulseekAdapter.cs          # Soulseek.NET wrapper
│   ├── DownloadManager.cs          # Download orchestration
│   ├── FileNameFormatter.cs        # Template-based naming
│   ├── InputParsers/
│   │   └── InputSources.cs         # CSV, String, List parsers
│   └── (More coming: RateLimiter, IndexManager, etc.)
│
├── Configuration/                   # Config management
│   ├── AppConfig.cs                # Settings model
│   └── ConfigManager.cs            # INI file I/O
│
├── Views/                           # WPF UI
│   ├── MainWindow.xaml            # Main UI layout
│   ├── MainWindow.xaml.cs         # Code-behind
│   └── MainViewModel.cs            # MVVM logic
│
├── Utils/                           # Utilities
│   ├── FileFormattingUtils.cs      # Size/time formatting
│   └── ValidationUtils.cs          # Input validation
│
├── App.xaml / App.xaml.cs          # WPF application
├── Program.cs                       # Entry point
├── SLSKDONET.csproj               # Project file
│
└── Documentation/
    ├── README.md                    # User guide
    ├── DEVELOPMENT.md               # Developer guide
    ├── LEARNING_FROM_SLSK_BATCHDL.md # Analysis & learning
    └── SLSKDONET_LEARNINGS.md       # Implementation summary
```

## Key Classes & Responsibilities

### Models
| Class | Purpose | Key Methods |
|-------|---------|-------------|
| `Track` | Search result data | `GetExtension()`, `GetFormattedSize()` |
| `DownloadJob` | Download state machine | Progress tracking, state management |
| `SearchQuery` | Query with multi-format parsing | `Parse()` (static factory) |
| `DownloadMode` | Enum: Normal, Album, Aggregate, AlbumAggregate | - |
| `FileCondition` | Abstract filter base | `Evaluate()` (override in subclasses) |
| `FileConditionEvaluator` | Ranking system | `FilterAndRank()`, `ScorePreferred()` |

### Services
| Class | Purpose | Key Methods |
|-------|---------|-------------|
| `SoulseekAdapter` | Soulseek.NET wrapper | `ConnectAsync()`, `SearchAsync()`, `DownloadAsync()` |
| `DownloadManager` | Job orchestration | `EnqueueDownload()`, `StartAsync()`, `CancelAll()` |
| `FileNameFormatter` | Template formatting | `Format()`, `GetFallbackName()` |
| `SearchQueryNormalizer` | Text cleanup | `RemoveFeatArtists()`, `RemoveYoutubeMarkers()`, `ApplyRegex()` |
| `CsvInputSource` | CSV parsing | `ParseAsync()` (IInputSource) |
| `StringInputSource` | Direct search | `ParseAsync()` (IInputSource) |
| `ListInputSource` | List file parsing | `ParseAsync()` (IInputSource) |

### Configuration
| Class | Purpose | Key Methods |
|-------|---------|-------------|
| `AppConfig` | Settings model | Properties with defaults |
| `ConfigManager` | INI file management | `Load()`, `Save()`, `GetDefaultConfigPath()` |

### UI (WPF)
| Class | Purpose | Key Properties |
|-------|---------|-----------|
| `MainViewModel` | MVVM logic | `IsConnected`, `SearchQuery`, `SearchResults`, `Downloads` |
| `MainWindow` | WPF window | Data context binding |

### Key Dependencies
| Package | Purpose |
|---------|---------|
| `Avalonia` | UI Framework |
| `Soulseek` | Soulseek Protocol |
| `Simple-Avalonia-DragnDrop-Service` | Drag & Drop Utils |
| `LibVLCSharp` | Audio Playback |
| `EntityFrameworkCore` | Database (SQLite) |

## Build Instructions

### Prerequisites
```bash
# Ensure you have .NET 8.0 SDK installed
dotnet --version  # Should show 8.0.0 or higher
```

### Build
```bash
cd SLSKDONET
dotnet clean
dotnet restore
dotnet build
```

### Run
```bash
dotnet run
```

### Publish (Release Build)
```bash
dotnet publish -c Release -o ./publish
# Executable at: ./publish/SLSKDONET.exe
```

## Configuration

### Location
```
Windows: %AppData%\SLSKDONET\config.ini
Linux:   ~/.config/SLSKDONET/config.ini
```

### First Run
1. App creates default config at the location above
2. Edit to add your Soulseek credentials
3. Restart app and login through GUI

### Sample config.ini
```ini
[Soulseek]
Username = your-username
Password = your-password
ListenPort = 49998
UseUPnP = false
ConnectTimeout = 20000
SearchTimeout = 6000

[Download]
Directory = C:\Users\YourName\Downloads
MaxConcurrent = 2
NameFormat = {artist} - {title}
```

## Usage Workflow

### Basic Flow
1. **Start App**: `dotnet run`
2. **Login Tab**: Enter credentials, click Login
3. **Search Tab**: Enter query, click Search
4. **View Results**: Results appear in grid
5. **Queue Download**: Select result, click "Add to Downloads"
6. **Start Download**: Switch to Downloads tab, click "Start Downloads"

### Advanced Usage (Coming in Phase 2)
- **CSV Import**: Load tracks.csv → bulk download
- **Album Mode**: Search album, download entire folder
- **Filters**: Set preferred bitrate/format
- **Profiles**: Switch config profiles for different scenarios

## Development Quick Reference

### Adding a New Feature

**Example: Add new file condition**

```csharp
// 1. Create new condition class in Models/FileCondition.cs
public class MyCondition : FileCondition {
    public override int Priority => 2;
    public override bool Evaluate(Track file) {
        // Your logic here
        return true;
    }
}

// 2. Register in evaluator (wherever it's created)
evaluator.AddPreferred(new MyCondition());
```

### Adding a New Input Source

```csharp
// 1. Implement IInputSource
public class MyInputSource : IInputSource {
    public InputType InputType => InputType.String; // or custom
    
    public async Task<List<SearchQuery>> ParseAsync(string input) {
        // Parse and return queries
        return new List<SearchQuery> { /* results */ };
    }
}

// 2. Register in App.xaml.cs or create factory
```

### Debugging

**Enable Debug Logging**
```csharp
// In App.xaml.cs ConfigureServices()
config.SetMinimumLevel(LogLevel.Debug);
```

**Inspect View Model**
- Set breakpoint in `MainViewModel` method
- Watch window: `SearchResults.Count`, `Downloads`
- Output window: Check log messages

## Common Issues

### NuGet Package Errors
```bash
dotnet nuget locals all --clear
dotnet restore
dotnet build
```

### WPF Designer Issues
```bash
# Restart Visual Studio
# Or clean and rebuild
dotnet clean
dotnet build
```

### "Already connected" Error
- Soulseek only allows one connection per account
- Use separate account for app vs. GUI client
- Wait 30+ seconds for timeout if force-closing

### Search Returns No Results
- Try simpler query (e.g., just artist name)
- Verify content exists on network
- Check search timeout setting (increase if network slow)

## Testing Checklist

- [ ] Build succeeds without warnings
- [ ] App starts without crashes
- [ ] Config file created on first run
- [ ] Can login with valid credentials
- [ ] Can search and get results
- [ ] Can queue downloads
- [ ] Download progresses and completes
- [ ] Can handle connection loss gracefully
- [ ] UI remains responsive during operations

## File I/O Operations

### Configuration Files
- **Location**: `%AppData%/SLSKDONET/config.ini`
- **Format**: INI with [Sections]
- **Managed by**: `ConfigManager`

### Download Index (Planned)
- **Location**: `{DownloadDir}/.slskdonet-index.json`
- **Format**: JSON with download history
- **Managed by**: `IndexManager` (phase 2)

### CSV Import
- **Format**: Standard CSV with headers
- **Auto-detect**: Artist, Title, Album, Length columns
- **Fallback**: Can specify column names manually

## Performance Notes

From slsk-batchdl analysis:
- **Default concurrent downloads**: 2 (configurable up to 8)
- **Search limit**: 34 searches per 220 seconds (Soulseek ban protection)
- **Search timeout**: 6000ms (configurable)
- **Connect timeout**: 20000ms (configurable)
- **Results per search**: 100 files (configurable in code)

Current implementation matches these defaults.

## Next Development Tasks

### Phase 2
- [ ] Implement album download mode
- [ ] Add configuration profiles
- [ ] Implement search rate limiting
- [ ] Add download index persistence

### Phase 3
- [ ] Interactive album selection UI
- [ ] On-complete action executor
- [ ] Aggregate download modes
- [ ] Advanced search normalization

### Phase 4
- [ ] Spotify integration
- [ ] YouTube integration
- [ ] Unit test suite
- [ ] Installer creation

## Useful Commands

```bash
# Quick build and run
dotnet build && dotnet run

# Build release version
dotnet publish -c Release

# Run with specific config location
# (Via UI after adding feature)

# Clean all build artifacts
dotnet clean

# Update packages
dotnet add package <PackageName> --version <Version>

# List outdated packages
dotnet outdated
```

## Documentation Links

- **README.md**: User guide and features
- **DEVELOPMENT.md**: Architecture, patterns, setup
- **LEARNING_FROM_SLSK_BATCHDL.md**: What we learned from reference project
- **SLSKDONET_LEARNINGS.md**: Detailed implementation analysis

## Support & Resources

### External Documentation
- [Soulseek.NET on GitHub](https://github.com/jpdillingham/Soulseek.NET)
- [.NET WPF Docs](https://docs.microsoft.com/en-us/dotnet/desktop/wpf/)
- [MVVM Pattern Guide](https://docs.microsoft.com/en-us/windows/uwp/get-started/building-a-basic-universal-windows-app-with-xaml-and-c-sharp)

### Code Examples
- CSV parsing: `Services/InputParsers/InputSources.cs`
- File filtering: `Models/FileCondition.cs`
- Template formatting: `Services/FileNameFormatter.cs`
- Configuration: `Configuration/ConfigManager.cs`

## License

GPL-3.0 (same as slsk-batchdl inspiration)
