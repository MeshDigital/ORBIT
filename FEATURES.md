# SLSKDONET Features

## 🎵 Audio Playback

### Built-in Music Player
- **LibVLC Integration**: Professional-grade audio playback
- **Format Support**: MP3, FLAC, WAV, OGG, and more
- **Playback Controls**: Play, pause, stop, volume control
- **Drag-and-Drop**: Drag tracks from Library to player sidebar
- **Double-Click Play**: Quick playback from track lists

### Player Features
- Real-time progress bar
- Track information display (artist, title, album)
- Volume slider
- Initialization diagnostics (shows errors if LibVLC missing)

---

## 📚 Library Management

### Playlist Organization
- **Create Playlists**: Organize tracks into custom playlists
- **Drag-and-Drop**: Move tracks between playlists visually (Powered by Simple-Avalonia-DragnDrop-Service)
- **Track Reordering**: Drag to reorder tracks within playlists
- **Playlist Deletion**: Remove playlists with confirmation
- **Track Removal**: Remove individual tracks from playlists

### Library Views
- **All Tracks**: View all downloaded tracks across playlists
- **Per-Playlist**: View tracks in specific playlists
- **Filter Options**: Filter by status (All, Downloaded, Pending)
- **Search**: Search tracks within current view
- **Sort Options**: Sort by artist, title, status, date added

### Persistence
- **SQLite Database**: All data persisted locally
- **Automatic Saves**: Changes saved immediately
- **Activity Logging**: Track additions/removals logged
- **Crash Recovery**: Library state survives app restarts

---

## 📥 Import System

### Spotify Integration
- **Playlist Import**: Import public Spotify playlists by URL
- **Track Extraction**: Automatic artist/title extraction
- **Metadata Preservation**: Album and track info retained
- **Batch Import**: Import entire playlists at once

### CSV Import
- **File Support**: Import from CSV files
- **Auto-Detection**: Automatic column detection
- **Flexible Format**: Supports various CSV structures
- **Preview**: Preview tracks before import

### Manual Import
- **Direct Entry**: Add tracks manually via search
- **Quick Add**: Simple artist - title format
- **Bulk Entry**: Add multiple tracks at once

---

## ⬇️ Download Management

### Queue System
- **Concurrent Downloads**: Multiple simultaneous downloads (configurable)
- **Progress Tracking**: Real-time progress for each track
- **Speed Display**: Current download speed shown
- **State Management**: Pending → Searching → Downloading → Completed

### Download Controls
- **Start/Pause**: Control individual downloads
- **Cancel**: Cancel downloads with cleanup
- **Hard Retry**: Delete partial files and retry
- **Resume**: Resume paused downloads

### Smart Features
- **Auto-Retry**: Automatic retry on failure
- **Timeout Handling**: Intelligent timeout detection
- **File Validation**: Check file integrity after download
- **Duplicate Detection**: Avoid downloading duplicates

---

## 🎨 User Interface

### Modern Design
- **Dark Theme**: Easy on the eyes, Windows 11 style
- **Clean Layout**: Intuitive navigation
- **Responsive**: No UI freezes during operations
- **Animations**: Smooth transitions and feedback

### Navigation
- **Search Page**: Find and queue tracks
- **Library Page**: Manage playlists and play music
- **Downloads Page**: Monitor active downloads
- **Settings Page**: Configure application
- **History Page**: View import history

### Visual Feedback
- **Drag Adorners**: Visual feedback during drag operations
- **Progress Bars**: Download and playback progress
- **Status Icons**: Track state indicators
- **Tooltips**: Helpful hover information

---

## 🔧 Configuration

### Soulseek Settings
- Username and password (encrypted storage)
- Server and port configuration
- Connection timeout settings

### Download Settings
- Download directory selection
- Max concurrent downloads (1-10)
- Filename format template
- Preferred audio formats

### UI Settings
- Player sidebar visibility
- Active downloads panel toggle
- Filter preferences
- View modes (grid/list)

---

## 🐛 Diagnostics

### Console Output (Debug Mode)
- **Detailed Logging**: All operations logged to console
- **Drag Events**: `[DRAG]` prefixed messages
- **Playback Events**: `[PLAYBACK]` prefixed messages
- **Service Logs**: `info:`, `warn:`, `fail:` messages
- **No Visual Studio Required**: Works standalone

### UI Diagnostics
- **Version Display**: Application version in status bar
- **Connection Status**: Real-time connection state
- **Initialization Checks**: Player and service status
- **Error Messages**: Clear user-facing error messages

### Troubleshooting Tools
- Console log redirection to file
- Database diagnostic queries
- LibVLC initialization checks
- Drag-and-drop event tracing

---

## 🔒 Security & Privacy

### Data Protection
- **Password Encryption**: Windows DPAPI for credentials
- **Local Storage**: All data stored locally
- **No Telemetry**: No data sent to external servers
- **Secure Connections**: SSL/TLS for Soulseek network

### File Safety
- **Sandboxed Downloads**: Downloads to configured directory only
- **Filename Sanitization**: Prevents path traversal attacks
- **Virus Scanning**: Compatible with Windows Defender
- **Metadata Privacy**: No personal data in database

---

## 🚀 Performance

### Optimizations
- **Async Operations**: Non-blocking UI
- **Database Indexing**: Fast queries on large libraries
- **Lazy Loading**: Load data on demand
- **Memory Management**: Efficient collection handling

### Scalability
- Handles 10,000+ track libraries
- Supports hundreds of playlists
- Efficient search with large result sets
- Fast drag-and-drop even with many tracks

---

## 🔌 Extensibility

### Plugin Points
- **Import Providers**: Add new import sources
- **Metadata Services**: Custom metadata fetching
- **Audio Backends**: Alternative player implementations
- **UI Themes**: Custom styling support

### Developer Features
- Dependency injection container
- MVVM architecture
- Event-driven design
- Comprehensive logging

---

**Version**: 1.0.0
