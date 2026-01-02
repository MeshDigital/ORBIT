using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO; // Added for Path
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services;

public class DatabaseService
{
    private readonly ILogger<DatabaseService> _logger;
    
    // Semaphore to serialize database write operations and prevent SQLite locking issues
    private static readonly SemaphoreSlim _writeSemaphore = new SemaphoreSlim(1, 1);

    public DatabaseService(ILogger<DatabaseService> logger)
    {
        _logger = logger;
    }

    public async Task InitAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("[{Ms}ms] Database Init: Starting", sw.ElapsedMilliseconds);
        
        using var context = new AppDbContext();
        await context.Database.EnsureCreatedAsync();
        _logger.LogInformation("[{Ms}ms] Database Init: EnsureCreated completed", sw.ElapsedMilliseconds);

        // Phase 1B: Configure WAL mode for better concurrency
        SqliteConnection? connection = context.Database.GetDbConnection() as SqliteConnection;
        if (connection != null)
        {
            context.ConfigureSqliteOptimizations(connection);
        }

        // Phase 1B: Run index audit (DEBUG builds only)
        #if DEBUG
        try
        {
            var auditReport = await AuditDatabaseIndexesAsync();
            if (auditReport.MissingIndexes.Any())
            {
                _logger.LogWarning("⚠️ Found {Count} missing indexes. Auto-applying...", 
                    auditReport.MissingIndexes.Count);
                await ApplyIndexRecommendationsAsync(auditReport);
            }
        }
        catch (Exception)
        {
            _logger.LogWarning("Index audit failed (non-fatal)");
        }
        #endif

        // Manual Schema Migration for existing databases
        try 
        {
            // Optimize: Check all columns at once using PRAGMA table_info
            if (connection != null)
            {
                await connection.OpenAsync();
            
            // Phase 0B: Rename PlaylistJobs -> Projects (if table exists)
            using (var cmd = connection.CreateCommand())
            {
                // Check if old table exists
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='PlaylistJobs'";
                var oldTableExists = await cmd.ExecuteScalarAsync();
                
                if (oldTableExists != null)
                {
                    _logger.LogWarning("Schema Migration: Renaming 'PlaylistJobs' table to 'Projects'");
                    cmd.CommandText = "ALTER TABLE PlaylistJobs RENAME TO Projects";
                    await cmd.ExecuteNonQueryAsync();
                    _logger.LogInformation("[{Ms}ms] Database Init: Table renamed to 'Projects'", sw.ElapsedMilliseconds);
                }
            }
                using var cmdSchema = connection!.CreateCommand();
            cmdSchema.CommandText = "PRAGMA table_info(PlaylistTracks)";
            var existingColumns = new HashSet<string>();
            
            using (var reader = await cmdSchema.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    existingColumns.Add(reader.GetString(1)); // Column name is at index 1
                }
            }
            
            _logger.LogInformation("[{Ms}ms] Database Init: Column check completed", sw.ElapsedMilliseconds);
            
            // Add missing columns to PlaylistTracks
            var columnsToAdd = new List<(string Name, string Definition)>();
            
            if (!existingColumns.Contains("SortOrder"))
                columnsToAdd.Add(("SortOrder", "SortOrder INTEGER DEFAULT 0"));
            if (!existingColumns.Contains("Rating"))
                columnsToAdd.Add(("Rating", "Rating INTEGER DEFAULT 0"));
            if (!existingColumns.Contains("IsLiked"))
                columnsToAdd.Add(("IsLiked", "IsLiked INTEGER DEFAULT 0"));
            if (!existingColumns.Contains("PlayCount"))
                columnsToAdd.Add(("PlayCount", "PlayCount INTEGER DEFAULT 0"));
            if (!existingColumns.Contains("LastPlayedAt"))
                columnsToAdd.Add(("LastPlayedAt", "LastPlayedAt TEXT NULL"));
            
            // Phase 0: Spotify Metadata Columns
            if (!existingColumns.Contains("SpotifyTrackId"))
                columnsToAdd.Add(("SpotifyTrackId", "SpotifyTrackId TEXT NULL"));
            if (!existingColumns.Contains("ISRC"))
                columnsToAdd.Add(("ISRC", "ISRC TEXT NULL"));
            if (!existingColumns.Contains("SpotifyAlbumId"))
                columnsToAdd.Add(("SpotifyAlbumId", "SpotifyAlbumId TEXT NULL"));
            if (!existingColumns.Contains("SpotifyArtistId"))
                columnsToAdd.Add(("SpotifyArtistId", "SpotifyArtistId TEXT NULL"));
            if (!existingColumns.Contains("AlbumArtUrl"))
                columnsToAdd.Add(("AlbumArtUrl", "AlbumArtUrl TEXT NULL"));
            if (!existingColumns.Contains("ArtistImageUrl"))
                columnsToAdd.Add(("ArtistImageUrl", "ArtistImageUrl TEXT NULL"));
            if (!existingColumns.Contains("Genres"))
                columnsToAdd.Add(("Genres", "Genres TEXT NULL"));
            if (!existingColumns.Contains("Popularity"))
                columnsToAdd.Add(("Popularity", "Popularity INTEGER NULL"));
            if (!existingColumns.Contains("CanonicalDuration"))
                columnsToAdd.Add(("CanonicalDuration", "CanonicalDuration INTEGER NULL"));
            if (!existingColumns.Contains("ReleaseDate"))
                columnsToAdd.Add(("ReleaseDate", "ReleaseDate TEXT NULL"));

            // Phase 0.1: Musical Intelligence & Antigravity
            if (!existingColumns.Contains("MusicalKey"))
                columnsToAdd.Add(("MusicalKey", "MusicalKey TEXT NULL"));
            if (!existingColumns.Contains("BPM"))
                columnsToAdd.Add(("BPM", "BPM REAL NULL"));
            if (!existingColumns.Contains("CuePointsJson"))
                columnsToAdd.Add(("CuePointsJson", "CuePointsJson TEXT NULL"));
            if (!existingColumns.Contains("AudioFingerprint"))
                columnsToAdd.Add(("AudioFingerprint", "AudioFingerprint TEXT NULL"));
            if (!existingColumns.Contains("BitrateScore"))
                columnsToAdd.Add(("BitrateScore", "BitrateScore INTEGER NULL"));
            if (!existingColumns.Contains("AnalysisOffset"))
                columnsToAdd.Add(("AnalysisOffset", "AnalysisOffset REAL NULL"));
            
            // New Flag
            if (!existingColumns.Contains("IsEnriched"))
                columnsToAdd.Add(("IsEnriched", "IsEnriched INTEGER DEFAULT 0"));
            
            // Phase 13: Search Filter Overrides
            if (!existingColumns.Contains("PreferredFormats"))
                columnsToAdd.Add(("PreferredFormats", "PreferredFormats TEXT NULL"));
            if (!existingColumns.Contains("MinBitrateOverride"))
                columnsToAdd.Add(("MinBitrateOverride", "MinBitrateOverride INTEGER NULL"));
            if (!existingColumns.Contains("Bitrate"))
                columnsToAdd.Add(("Bitrate", "Bitrate INTEGER DEFAULT 0"));
            if (!existingColumns.Contains("Format"))
                columnsToAdd.Add(("Format", "Format TEXT NULL"));
            if (!existingColumns.Contains("Integrity"))
                columnsToAdd.Add(("Integrity", "Integrity INTEGER DEFAULT 0"));
            
            // Phase 5: Self-Healing (Upgrade Detection)
            if (!existingColumns.Contains("PreviousBitrate"))
                columnsToAdd.Add(("PreviousBitrate", "PreviousBitrate INTEGER DEFAULT 0"));
            
            if (!existingColumns.Contains("Energy"))
                columnsToAdd.Add(("Energy", "Energy REAL NULL"));
            if (!existingColumns.Contains("Danceability"))
                columnsToAdd.Add(("Danceability", "Danceability REAL NULL"));
            if (!existingColumns.Contains("Valence"))
                columnsToAdd.Add(("Valence", "Valence REAL NULL"));

            // Phase 8: Sonic Integrity
            if (!existingColumns.Contains("WaveformData"))
                columnsToAdd.Add(("WaveformData", "WaveformData BLOB NULL"));
            if (!existingColumns.Contains("RmsData"))
                columnsToAdd.Add(("RmsData", "RmsData BLOB NULL"));
            if (!existingColumns.Contains("SpectralHash"))
                columnsToAdd.Add(("SpectralHash", "SpectralHash TEXT NULL"));
            if (!existingColumns.Contains("QualityConfidence"))
                columnsToAdd.Add(("QualityConfidence", "QualityConfidence REAL NULL"));
            if (!existingColumns.Contains("FrequencyCutoff"))
                columnsToAdd.Add(("FrequencyCutoff", "FrequencyCutoff INTEGER NULL"));
            if (!existingColumns.Contains("IsTrustworthy"))
                columnsToAdd.Add(("IsTrustworthy", "IsTrustworthy INTEGER NULL"));
            if (!existingColumns.Contains("QualityDetails"))
                columnsToAdd.Add(("QualityDetails", "QualityDetails TEXT NULL"));
            
            if (!existingColumns.Contains("CompletedAt"))
                columnsToAdd.Add(("CompletedAt", "CompletedAt TEXT NULL"));
            
            // Phase 15: Style Lab
            if (!existingColumns.Contains("DetectedSubGenre"))
                columnsToAdd.Add(("DetectedSubGenre", "DetectedSubGenre TEXT NULL"));

            foreach (var (name, definition) in columnsToAdd)
            {
                _logger.LogWarning("Schema Patch: Adding missing column '{Column}' to PlaylistTracks", name);
                // Security: Column definition is from a controlled internal list of hardcoded strings
                #pragma warning disable EF1002 
                await context.Database.ExecuteSqlRawAsync($"ALTER TABLE PlaylistTracks ADD COLUMN {definition}");
                #pragma warning restore EF1002
            }
            
            // Check Projects table (formerly PlaylistJobs)
            cmdSchema.CommandText = "PRAGMA table_info(Projects)";
            existingColumns.Clear();
            
            using (var reader = await cmdSchema.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    existingColumns.Add(reader.GetString(1));
                }
            }
            
            if (!existingColumns.Contains("IsDeleted"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'IsDeleted' to PlaylistJobs");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE Projects ADD COLUMN IsDeleted INTEGER DEFAULT 0");
            }

            if (!existingColumns.Contains("AlbumArtUrl"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'AlbumArtUrl' to PlaylistJobs");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE Projects ADD COLUMN AlbumArtUrl TEXT NULL");
            }

            if (!existingColumns.Contains("SourceUrl"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'SourceUrl' to PlaylistJobs");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE Projects ADD COLUMN SourceUrl TEXT NULL");
            }

            if (!existingColumns.Contains("MissingCount"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'MissingCount' to PlaylistJobs");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE Projects ADD COLUMN MissingCount INTEGER DEFAULT 0");
            }

            if (!existingColumns.Contains("IsUserPaused"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'IsUserPaused' to PlaylistJobs");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE Projects ADD COLUMN IsUserPaused INTEGER DEFAULT 0");
            }

            if (!existingColumns.Contains("DateStarted"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'DateStarted' to PlaylistJobs");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE Projects ADD COLUMN DateStarted TEXT NULL");
            }

            if (!existingColumns.Contains("DateUpdated"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'DateUpdated' to PlaylistJobs");
                var now = DateTime.UtcNow.ToString("o");
                await context.Database.ExecuteSqlAsync($"ALTER TABLE Projects ADD COLUMN DateUpdated TEXT DEFAULT {now}");
            }

            // Check LibraryEntries table
            cmdSchema.CommandText = "PRAGMA table_info(LibraryEntries)";
            existingColumns.Clear();
            using (var reader = await cmdSchema.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    existingColumns.Add(reader.GetString(1));
                }
            }
            if (!existingColumns.Contains("Integrity"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'Integrity' to LibraryEntries");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE LibraryEntries ADD COLUMN Integrity INTEGER DEFAULT 0");
            }
            if (!existingColumns.Contains("PreviousBitrate"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'PreviousBitrate' to LibraryEntries");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE LibraryEntries ADD COLUMN PreviousBitrate INTEGER DEFAULT 0");
            }
            if (!existingColumns.Contains("WaveformData"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'WaveformData' to LibraryEntries");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE LibraryEntries ADD COLUMN WaveformData BLOB NULL");
            }
            if (!existingColumns.Contains("RmsData"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'RmsData' to LibraryEntries");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE LibraryEntries ADD COLUMN RmsData BLOB NULL");
            }
            if (!existingColumns.Contains("SpectralHash"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'SpectralHash' to LibraryEntries");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE LibraryEntries ADD COLUMN SpectralHash TEXT NULL");
            }
            if (!existingColumns.Contains("QualityConfidence"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'QualityConfidence' to LibraryEntries");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE LibraryEntries ADD COLUMN QualityConfidence REAL NULL");
            }
            if (!existingColumns.Contains("FrequencyCutoff"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'FrequencyCutoff' to LibraryEntries");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE LibraryEntries ADD COLUMN FrequencyCutoff INTEGER NULL");
            }
            if (!existingColumns.Contains("IsTrustworthy"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'IsTrustworthy' to LibraryEntries");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE LibraryEntries ADD COLUMN IsTrustworthy INTEGER NULL");
            }
            if (!existingColumns.Contains("QualityDetails"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'QualityDetails' to LibraryEntries");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE LibraryEntries ADD COLUMN QualityDetails TEXT NULL");
            }
            if (!existingColumns.Contains("Energy"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'Energy' to LibraryEntries");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE LibraryEntries ADD COLUMN Energy REAL NULL");
            }
            if (!existingColumns.Contains("Danceability"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'Danceability' to LibraryEntries");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE LibraryEntries ADD COLUMN Danceability REAL NULL");
            }
            if (!existingColumns.Contains("Valence"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'Valence' to LibraryEntries");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE LibraryEntries ADD COLUMN Valence REAL NULL");
            }
            if (!existingColumns.Contains("AudioFingerprint"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'AudioFingerprint' to LibraryEntries");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE LibraryEntries ADD COLUMN AudioFingerprint TEXT NULL");
            }

            // Check Tracks table
            cmdSchema.CommandText = "PRAGMA table_info(Tracks)";
            existingColumns.Clear();
            using (var reader = await cmdSchema.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    existingColumns.Add(reader.GetString(1));
                }
            }
            if (!existingColumns.Contains("Integrity"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'Integrity' to Tracks");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE Tracks ADD COLUMN Integrity INTEGER DEFAULT 0");
            }
            if (!existingColumns.Contains("NextRetryTime"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'NextRetryTime' to Tracks");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE Tracks ADD COLUMN NextRetryTime TEXT NULL");
            }
            
            // Phase 8: Sonic Integrity for Tracks
            if (!existingColumns.Contains("SpectralHash"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE Tracks ADD COLUMN SpectralHash TEXT NULL");
            if (!existingColumns.Contains("QualityConfidence"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE Tracks ADD COLUMN QualityConfidence REAL NULL");
            if (!existingColumns.Contains("FrequencyCutoff"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE Tracks ADD COLUMN FrequencyCutoff INTEGER NULL");
            if (!existingColumns.Contains("IsTrustworthy"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE Tracks ADD COLUMN IsTrustworthy INTEGER NULL");
            if (!existingColumns.Contains("QualityDetails"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE Tracks ADD COLUMN QualityDetails TEXT NULL");
            
            // Phase 4: Musical Intelligence for Tracks
            if (!existingColumns.Contains("Energy"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE Tracks ADD COLUMN Energy REAL NULL");
            if (!existingColumns.Contains("Danceability"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE Tracks ADD COLUMN Danceability REAL NULL");
            if (!existingColumns.Contains("Valence"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE Tracks ADD COLUMN Valence REAL NULL");
            if (!existingColumns.Contains("AudioFingerprint"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE Tracks ADD COLUMN AudioFingerprint TEXT NULL");
            if (!existingColumns.Contains("BitrateScore"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE Tracks ADD COLUMN BitrateScore INTEGER NULL");
            if (!existingColumns.Contains("AnalysisOffset"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE Tracks ADD COLUMN AnalysisOffset REAL NULL");
            if (!existingColumns.Contains("SpotifyBPM"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE Tracks ADD COLUMN SpotifyBPM REAL NULL");
            if (!existingColumns.Contains("SpotifyKey"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE Tracks ADD COLUMN SpotifyKey TEXT NULL");
            if (!existingColumns.Contains("ManualBPM"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE Tracks ADD COLUMN ManualBPM REAL NULL");
            if (!existingColumns.Contains("ManualKey"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE Tracks ADD COLUMN ManualKey TEXT NULL");
            if (!existingColumns.Contains("IsEnriched"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE Tracks ADD COLUMN IsEnriched INTEGER DEFAULT 0");

            if (!existingColumns.Contains("CompletedAt"))
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE Tracks ADD COLUMN CompletedAt TEXT NULL");
            
            // Check LibraryHealth table
            cmdSchema.CommandText = "PRAGMA table_info(LibraryHealth)";
            existingColumns.Clear();
            using (var reader = await cmdSchema.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    existingColumns.Add(reader.GetString(1));
                }
            }
            if (!existingColumns.Contains("GoldCount"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'GoldCount' to LibraryHealth");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE LibraryHealth ADD COLUMN GoldCount INTEGER DEFAULT 0");
            }
            if (!existingColumns.Contains("SilverCount"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'SilverCount' to LibraryHealth");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE LibraryHealth ADD COLUMN SilverCount INTEGER DEFAULT 0");
            }
            if (!existingColumns.Contains("BronzeCount"))
            {
                _logger.LogWarning("Schema Patch: Adding missing column 'BronzeCount' to LibraryHealth");
                await context.Database.ExecuteSqlRawAsync("ALTER TABLE LibraryHealth ADD COLUMN BronzeCount INTEGER DEFAULT 0");
            }
            
            _logger.LogInformation("[{Ms}ms] Database Init: Schema patches completed", sw.ElapsedMilliseconds);
            
            // Session 1: Performance Indexes (Phase 2 Performance Overhaul)
            // These indexes provide 50-100x speedup for common queries
            _logger.LogInformation("Creating performance indexes...");
            try
            {
                await context.Database.ExecuteSqlAsync($@"
                    -- PlaylistTracks indexes for fast filtering/sorting
                    CREATE INDEX IF NOT EXISTS idx_playlist_tracks_playlistid 
                    ON PlaylistTracks(PlaylistId);
                    
                    CREATE INDEX IF NOT EXISTS idx_playlist_tracks_status 
                    ON PlaylistTracks(Status);
                    
                    CREATE INDEX IF NOT EXISTS idx_playlist_tracks_globalid 
                    ON PlaylistTracks(TrackUniqueHash);
                    
                    -- Projects index for Import History sorting
                    CREATE INDEX IF NOT EXISTS IX_Project_CreatedAt
                    ON Projects(CreatedAt);
                    
                    -- LibraryEntries index for duplicate detection
                    CREATE INDEX IF NOT EXISTS idx_library_entries_globalid 
                    ON LibraryEntries(UniqueHash);
                    
                    -- Tracks index for Spotify metadata lookups
                    CREATE INDEX IF NOT EXISTS idx_tracks_spotifytrackid 
                    ON Tracks(SpotifyTrackId);
                    
                    -- Phase 5/13: Self-Healing & System Health Dashboard
                    -- Index on Integrity for filtering Failed/Unknown tracks
                    CREATE INDEX IF NOT EXISTS idx_library_entries_integrity 
                    ON LibraryEntries(Integrity);
                ");
                _logger.LogInformation("[{Ms}ms] Database Init: Performance indexes created", sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create performance indexes (non-critical)");
            }
            
            // Check for ActivityLogs table
            try 
            {
               await context.Database.ExecuteSqlRawAsync("SELECT Id FROM ActivityLogs LIMIT 1");
            }
            catch
            {
                 _logger.LogWarning("Schema Patch: Creating missing table 'ActivityLogs'");
                 var createTableSql = @"
                    CREATE TABLE IF NOT EXISTS ActivityLogs (
                        Id TEXT NOT NULL CONSTRAINT PK_ActivityLogs PRIMARY KEY,
                        PlaylistId TEXT NOT NULL,
                        Action TEXT NOT NULL,
                        Details TEXT NOT NULL,
                        Timestamp TEXT NOT NULL,
                        CONSTRAINT FK_ActivityLogs_Projects_PlaylistId FOREIGN KEY (PlaylistId) REFERENCES Projects (Id) ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS IX_ActivityLogs_PlaylistId ON ActivityLogs (PlaylistId);
                 ";
                 await context.Database.ExecuteSqlRawAsync(createTableSql);
            }
            
            // Check for QueueItems table (Phase 0: Queue persistence)
            try 
            {
               await context.Database.ExecuteSqlRawAsync("SELECT Id FROM QueueItems LIMIT 1");
            }
            catch
            {
                 _logger.LogWarning("Schema Patch: Creating missing table 'QueueItems'");
                 var createQueueTableSql = @"
                    CREATE TABLE IF NOT EXISTS QueueItems (
                        Id TEXT NOT NULL CONSTRAINT PK_QueueItems PRIMARY KEY,
                        PlaylistTrackId TEXT NOT NULL,
                        QueuePosition INTEGER NOT NULL,
                        AddedAt TEXT NOT NULL,
                        IsCurrentTrack INTEGER NOT NULL DEFAULT 0,
                        CONSTRAINT FK_QueueItems_PlaylistTracks_PlaylistTrackId FOREIGN KEY (PlaylistTrackId) REFERENCES PlaylistTracks (Id) ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS IX_QueueItems_QueuePosition ON QueueItems (QueuePosition);
                 ";
                 await context.Database.ExecuteSqlRawAsync(createQueueTableSql);
            }

            // Check for SpotifyMetadataCache table
            try
            {
               await context.Database.ExecuteSqlRawAsync("SELECT SpotifyId FROM SpotifyMetadataCache LIMIT 1");
            }
            catch
            {
                 _logger.LogWarning("Schema Patch: Creating missing table 'SpotifyMetadataCache'");
                 var createCacheTableSql = @"
                    CREATE TABLE IF NOT EXISTS SpotifyMetadataCache (
                        SpotifyId TEXT NOT NULL CONSTRAINT PK_SpotifyMetadataCache PRIMARY KEY,
                        DataJson TEXT NOT NULL,
                        CachedAt TEXT NOT NULL,
                        ExpiresAt TEXT NOT NULL
                    );
                 ";
                 await context.Database.ExecuteSqlRawAsync(createCacheTableSql);
            }

            // Ensure PendingOrchestrations table exists
            try
            {
                var createPendingTableSql = @"
                    CREATE TABLE IF NOT EXISTS PendingOrchestrations (
                        GlobalId TEXT NOT NULL CONSTRAINT PK_PendingOrchestrations PRIMARY KEY,
                        AddedAt TEXT NOT NULL
                    );
                ";
                await context.Database.ExecuteSqlRawAsync(createPendingTableSql);
            }
            catch (Exception ex)
            {
                 _logger.LogError(ex, "Failed to ensure PendingOrchestrations table exists");
            }

            // Phase 1: Ensure EnrichmentTasks table exists (Manual Patch)
            try 
            {
                await context.Database.ExecuteSqlRawAsync("SELECT Id FROM EnrichmentTasks LIMIT 1");
            }
            catch 
            {
                _logger.LogWarning("Schema Patch: Creating missing table 'EnrichmentTasks'");
                var createEnrichmentTableSql = @"
                    CREATE TABLE IF NOT EXISTS EnrichmentTasks (
                        Id TEXT NOT NULL CONSTRAINT PK_EnrichmentTasks PRIMARY KEY,
                        TrackId TEXT NOT NULL,
                        AlbumId TEXT NULL,
                        Status INTEGER NOT NULL,
                        RetryCount INTEGER NOT NULL,
                        ErrorMessage TEXT NULL,
                        CreatedAt TEXT NOT NULL,
                        UpdatedAt TEXT NULL
                    );
                    CREATE INDEX IF NOT EXISTS IX_EnrichmentTasks_Status ON EnrichmentTasks (Status);
                    CREATE INDEX IF NOT EXISTS IX_EnrichmentTasks_Status_CreatedAt ON EnrichmentTasks (Status, CreatedAt);
                ";
                await context.Database.ExecuteSqlRawAsync(createEnrichmentTableSql);
            }

            // Phase 3: Ensure AudioAnalysis table exists (Manual Patch)
            try 
            {
                await context.Database.ExecuteSqlRawAsync("SELECT Id FROM audio_analysis LIMIT 1");
            }
            catch 
            {
                try 
                {
                    await context.Database.ExecuteSqlRawAsync("SELECT IsUpscaled FROM audio_analysis LIMIT 1");
                }
                catch
                {
                    _logger.LogWarning("Schema Patch: Adding missing columns to 'audio_analysis'");
                    try
                    {
                        await context.Database.ExecuteSqlRawAsync("ALTER TABLE audio_analysis ADD COLUMN IsUpscaled INTEGER DEFAULT 0 NOT NULL;");
                        await context.Database.ExecuteSqlRawAsync("ALTER TABLE audio_analysis ADD COLUMN SpectralHash TEXT DEFAULT '' NOT NULL;");
                        await context.Database.ExecuteSqlRawAsync("ALTER TABLE audio_analysis ADD COLUMN FrequencyCutoff INTEGER DEFAULT 0 NOT NULL;");
                        await context.Database.ExecuteSqlRawAsync("ALTER TABLE audio_analysis ADD COLUMN QualityConfidence REAL DEFAULT 0 NOT NULL;");
                    }
                    catch (Exception)
                    {
                    // Failed to alter columns - table might not exist,create it
                    _logger.LogWarning("Schema Patch: Creating missing table 'audio_analysis'");
                    var createAudioAnalysisTableSql = @"
                        CREATE TABLE IF NOT EXISTS audio_analysis (
                            Id TEXT NOT NULL CONSTRAINT PK_audio_analysis PRIMARY KEY,
                            TrackUniqueHash TEXT NOT NULL,
                            Bitrate INTEGER NOT NULL,
                            SampleRate INTEGER NOT NULL,
                            Channels INTEGER NOT NULL,
                            Codec TEXT NOT NULL,
                            DurationMs INTEGER NOT NULL,
                            LoudnessLufs REAL NOT NULL,
                            TruePeakDb REAL NOT NULL,
                            DynamicRange REAL NOT NULL,
                            AnalyzedAt TEXT NOT NULL,
                            IsUpscaled INTEGER NOT NULL DEFAULT 0,
                            SpectralHash TEXT NOT NULL DEFAULT '',
                            FrequencyCutoff INTEGER NOT NULL DEFAULT 0,
                            QualityConfidence REAL NOT NULL DEFAULT 0
                        );
                        CREATE INDEX IF NOT EXISTS IX_audio_analysis_TrackUniqueHash ON audio_analysis (TrackUniqueHash);
                    ";
                    await context.Database.ExecuteSqlRawAsync(createAudioAnalysisTableSql);
                }
                }
            }

            // Phase 4: Validating AudioFeatures table
            try
            {
                await context.Database.ExecuteSqlRawAsync("SELECT Id FROM audio_features LIMIT 1");
            }
            catch
            {
                _logger.LogWarning("Schema Patch: Creating missing table 'audio_features'");
                var createAudioFeaturesTableSql = @"
                    CREATE TABLE IF NOT EXISTS audio_features (
                        Id TEXT NOT NULL CONSTRAINT PK_audio_features PRIMARY KEY,
                        TrackUniqueHash TEXT NOT NULL,
                        
                        -- Core Musical Features
                        Bpm REAL NOT NULL DEFAULT 0,
                        BpmConfidence REAL NOT NULL DEFAULT 0,
                        Key TEXT NOT NULL DEFAULT '',
                        Scale TEXT NOT NULL DEFAULT '',
                        KeyConfidence REAL NOT NULL DEFAULT 0,
                        CamelotKey TEXT NOT NULL DEFAULT '',
                        
                        -- Sonic Characteristics
                        Energy REAL NOT NULL DEFAULT 0,
                        Danceability REAL NOT NULL DEFAULT 0,
                        SpectralCentroid REAL NOT NULL DEFAULT 0,
                        SpectralComplexity REAL NOT NULL DEFAULT 0,
                        OnsetRate REAL NOT NULL DEFAULT 0,
                        DynamicComplexity REAL NOT NULL DEFAULT 0,
                        LoudnessLUFS REAL NOT NULL DEFAULT 0,
                        
                        -- Drop Detection & Cue Points
                        DropTimeSeconds REAL NULL,
                        CueIntro REAL NOT NULL DEFAULT 0,
                        CueBuild REAL NULL,
                        CueDrop REAL NULL,
                        CuePhraseStart REAL NULL,
                        
                        -- Identity & Metadata
                        Fingerprint TEXT NOT NULL DEFAULT '',
                        AnalysisVersion TEXT NOT NULL DEFAULT '',
                        AnalyzedAt TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS IX_audio_features_TrackUniqueHash ON audio_features (TrackUniqueHash);
                    CREATE INDEX IF NOT EXISTS IX_audio_features_Bpm ON audio_features (Bpm);
                    CREATE INDEX IF NOT EXISTS IX_audio_features_CamelotKey ON audio_features (CamelotKey);
                    CREATE INDEX IF NOT EXISTS IX_audio_features_DropTimeSeconds ON audio_features (DropTimeSeconds);
                ";
                await context.Database.ExecuteSqlRawAsync(createAudioFeaturesTableSql);
            }

            // Check for LibraryHealth table
            try
            {
                await context.Database.ExecuteSqlRawAsync("SELECT Id FROM LibraryHealth LIMIT 1");
            }
            catch
            {
                _logger.LogWarning("Schema Patch: Creating missing table 'LibraryHealth'");
                var createHealthTableSql = @"
                    CREATE TABLE IF NOT EXISTS LibraryHealth (
                        Id INTEGER PRIMARY KEY,
                        TotalTracks INTEGER NOT NULL,
                        HqTracks INTEGER NOT NULL,
                        UpgradableCount INTEGER NOT NULL,
                        PendingUpdates INTEGER NOT NULL,
                        TotalStorageBytes INTEGER NOT NULL,
                        FreeStorageBytes INTEGER NOT NULL,
                        LastScanDate TEXT NOT NULL,
                        TopGenresJson TEXT NULL
                    );
                ";
                await context.Database.ExecuteSqlRawAsync(createHealthTableSql);
            }

            using (var schemaCmd = connection!.CreateCommand())
            {
                schemaCmd.CommandText = "PRAGMA table_info(LibraryEntries)";
                var libColumns = new HashSet<string>();
                using (var reader = await schemaCmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        libColumns.Add(reader.GetString(1)); // Name is column 1
                    }
                }

                if (libColumns.Count > 0) // Only patch if table exists
                {
                    var newCols = new List<(string Name, string Def)>();
                    if (!libColumns.Contains("SpotifyTrackId")) newCols.Add(("SpotifyTrackId", "TEXT NULL"));
                    if (!libColumns.Contains("ISRC")) newCols.Add(("ISRC", "TEXT NULL"));
                    if (!libColumns.Contains("SpotifyAlbumId")) newCols.Add(("SpotifyAlbumId", "TEXT NULL"));
                    if (!libColumns.Contains("SpotifyArtistId")) newCols.Add(("SpotifyArtistId", "TEXT NULL"));
                    if (!libColumns.Contains("AlbumArtUrl")) newCols.Add(("AlbumArtUrl", "TEXT NULL"));
                    if (!libColumns.Contains("ArtistImageUrl")) newCols.Add(("ArtistImageUrl", "TEXT NULL"));
                    if (!libColumns.Contains("Genres")) newCols.Add(("Genres", "TEXT NULL"));
                    if (!libColumns.Contains("Popularity")) newCols.Add(("Popularity", "INTEGER NULL"));
                    if (!libColumns.Contains("CanonicalDuration")) newCols.Add(("CanonicalDuration", "INTEGER NULL"));
                    if (!libColumns.Contains("ReleaseDate")) newCols.Add(("ReleaseDate", "TEXT NULL"));

                    // Phase 0.1: Musical Intelligence & Antigravity
                    if (!libColumns.Contains("MusicalKey")) newCols.Add(("MusicalKey", "TEXT NULL"));
                    if (!libColumns.Contains("BPM")) newCols.Add(("BPM", "REAL NULL"));
                    if (!libColumns.Contains("Energy")) newCols.Add(("Energy", "REAL NULL"));
                    if (!libColumns.Contains("Valence")) newCols.Add(("Valence", "REAL NULL"));
                    if (!libColumns.Contains("Danceability")) newCols.Add(("Danceability", "REAL NULL"));
                    if (!libColumns.Contains("Danceability")) newCols.Add(("Danceability", "REAL NULL"));
                    if (!libColumns.Contains("AudioFingerprint")) newCols.Add(("AudioFingerprint", "TEXT NULL"));
                    if (!libColumns.Contains("IsEnriched")) newCols.Add(("IsEnriched", "INTEGER DEFAULT 0"));

                    foreach (var (col, def) in newCols)
                    {
                         _logger.LogWarning("Schema Patch: Adding missing column '{Col}' to LibraryEntries", col);
                         #pragma warning disable EF1002
                         await context.Database.ExecuteSqlRawAsync($"ALTER TABLE LibraryEntries ADD COLUMN {col} {def}");
                         #pragma warning restore EF1002
                    }
                }

            }

            // Patch Tracks columns
            using (var schemaCmd = connection!.CreateCommand())
            {
                schemaCmd.CommandText = "PRAGMA table_info(Tracks)";
                var tracksColumns = new HashSet<string>();
                using (var reader = await schemaCmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        tracksColumns.Add(reader.GetString(1));
                    }
                }

                if (tracksColumns.Count > 0)
                {
                    var newCols = new List<(string Name, string Def)>();
                    if (!tracksColumns.Contains("SpotifyTrackId")) newCols.Add(("SpotifyTrackId", "TEXT NULL"));
                    if (!tracksColumns.Contains("ISRC")) newCols.Add(("ISRC", "TEXT NULL"));
                    if (!tracksColumns.Contains("SpotifyAlbumId")) newCols.Add(("SpotifyAlbumId", "TEXT NULL"));
                    if (!tracksColumns.Contains("SpotifyArtistId")) newCols.Add(("SpotifyArtistId", "TEXT NULL"));
                    if (!tracksColumns.Contains("AlbumArtUrl")) newCols.Add(("AlbumArtUrl", "TEXT NULL"));
                    if (!tracksColumns.Contains("ArtistImageUrl")) newCols.Add(("ArtistImageUrl", "TEXT NULL"));
                    if (!tracksColumns.Contains("Genres")) newCols.Add(("Genres", "TEXT NULL"));
                    if (!tracksColumns.Contains("Popularity")) newCols.Add(("Popularity", "INTEGER NULL"));
                    if (!tracksColumns.Contains("CanonicalDuration")) newCols.Add(("CanonicalDuration", "INTEGER NULL"));
                    if (!tracksColumns.Contains("ReleaseDate")) newCols.Add(("ReleaseDate", "TEXT NULL"));
                    // CoverArtUrl was already there but check just in case
                    if (!tracksColumns.Contains("CoverArtUrl")) newCols.Add(("CoverArtUrl", "TEXT NULL"));
                    if (!tracksColumns.Contains("Bitrate")) newCols.Add(("Bitrate", "INTEGER DEFAULT 0"));
                    
                    // Phase 0.1: Musical Intelligence & ORBIT
                    if (!tracksColumns.Contains("MusicalKey")) newCols.Add(("MusicalKey", "TEXT NULL"));
                    if (!tracksColumns.Contains("BPM")) newCols.Add(("BPM", "REAL NULL"));
                    if (!tracksColumns.Contains("Energy")) newCols.Add(("Energy", "REAL NULL"));
                    if (!tracksColumns.Contains("Valence")) newCols.Add(("Valence", "REAL NULL"));
                    if (!tracksColumns.Contains("Danceability")) newCols.Add(("Danceability", "REAL NULL"));
                    if (!tracksColumns.Contains("CuePointsJson")) newCols.Add(("CuePointsJson", "TEXT NULL"));
                    if (!tracksColumns.Contains("AudioFingerprint")) newCols.Add(("AudioFingerprint", "TEXT NULL"));
                    if (!tracksColumns.Contains("BitrateScore")) newCols.Add(("BitrateScore", "INTEGER NULL"));
                    if (!tracksColumns.Contains("AnalysisOffset")) newCols.Add(("AnalysisOffset", "REAL NULL"));
                    
                    // Phase 8: Sonic Integrity & Spectral Analysis
                    if (!tracksColumns.Contains("SpectralHash")) newCols.Add(("SpectralHash", "TEXT NULL"));
                    if (!tracksColumns.Contains("QualityConfidence")) newCols.Add(("QualityConfidence", "REAL NULL"));
                    if (!tracksColumns.Contains("FrequencyCutoff")) newCols.Add(("FrequencyCutoff", "INTEGER NULL"));
                    if (!tracksColumns.Contains("IsTrustworthy")) newCols.Add(("IsTrustworthy", "INTEGER NULL"));
                    if (!tracksColumns.Contains("QualityDetails")) newCols.Add(("QualityDetails", "TEXT NULL"));
                    
                    // New Flag
                    if (!tracksColumns.Contains("IsEnriched")) newCols.Add(("IsEnriched", "INTEGER DEFAULT 0"));

                    foreach (var (col, def) in newCols)
                    {
                         _logger.LogWarning("Schema Patch: Adding missing column '{Col}' to Tracks", col);
                         #pragma warning disable EF1002
                         await context.Database.ExecuteSqlRawAsync($"ALTER TABLE Tracks ADD COLUMN {col} {def}");
                         #pragma warning restore EF1002
                    }
                }
            }

            // Patch PlaylistTracks columns (Phase 8 Support)
            using (var schemaCmd = connection!.CreateCommand())
            {
                schemaCmd.CommandText = "PRAGMA table_info(PlaylistTracks)";
                var ptColumns = new HashSet<string>();
                using (var reader = await schemaCmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        ptColumns.Add(reader.GetString(1));
                    }
                }

                if (ptColumns.Count > 0)
                {
                    var newCols = new List<(string Name, string Def)>();
                    if (!ptColumns.Contains("SpectralHash")) newCols.Add(("SpectralHash", "TEXT NULL"));
                    if (!ptColumns.Contains("QualityConfidence")) newCols.Add(("QualityConfidence", "REAL NULL"));
                    if (!ptColumns.Contains("FrequencyCutoff")) newCols.Add(("FrequencyCutoff", "INTEGER NULL"));
                    if (!ptColumns.Contains("IsTrustworthy")) newCols.Add(("IsTrustworthy", "INTEGER NULL"));
                    if (!ptColumns.Contains("QualityDetails")) newCols.Add(("QualityDetails", "TEXT NULL"));

                    foreach (var (col, def) in newCols)
                    {
                         _logger.LogWarning("Schema Patch: Adding missing column '{Col}' to PlaylistTracks", col);
                         #pragma warning disable EF1002 // SQL Injection: Safe - Internal string from hardcoded list
                         await context.Database.ExecuteSqlRawAsync($"ALTER TABLE PlaylistTracks ADD COLUMN {col} {def}");
                         #pragma warning restore EF1002
                    }
                }
            }
            
            await connection!.CloseAsync();
        }
    }
        catch (Exception)
        {
            _logger.LogError("Failed to patch database schema");
        }

        _logger.LogInformation("[{Ms}ms] Database initialized and schema verified.", sw.ElapsedMilliseconds);
    }

    // ===== PendingOrchestration Methods =====

    public async Task AddPendingOrchestrationAsync(string globalId)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var sql = "INSERT OR IGNORE INTO PendingOrchestrations (GlobalId, AddedAt) VALUES (@id, @now)";
            await context.Database.ExecuteSqlRawAsync(sql, 
                new SqliteParameter("@id", globalId),
                new SqliteParameter("@now", DateTime.UtcNow.ToString("o")));
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task RemovePendingOrchestrationAsync(string globalId)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var sql = "DELETE FROM PendingOrchestrations WHERE GlobalId = @id";
            await context.Database.ExecuteSqlRawAsync(sql, new SqliteParameter("@id", globalId));
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task<List<string>> GetPendingOrchestrationsAsync()
    {
        using var context = new AppDbContext();
        var ids = new List<string>();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT GlobalId FROM PendingOrchestrations";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }
        return ids;
    }

    // ===== Track Methods =====

    public async Task<List<TrackEntity>> LoadTracksAsync()
    {
        using var context = new AppDbContext();
        return await context.Tracks.ToListAsync();
    }

    public async Task<TrackEntity?> FindTrackAsync(string globalId)
    {
        using var context = new AppDbContext();
        return await context.Tracks.FirstOrDefaultAsync(t => t.GlobalId == globalId);
    }

    public async Task SaveTrackAsync(TrackEntity track)
    {
        const int maxRetries = 5;
        
        // Serialize all database writes to prevent SQLite locking
        await _writeSemaphore.WaitAsync();
        try
        {
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    using var context = new AppDbContext();

                    // Find existing entity by GlobalId (primary key)
                    var existingTrack = await context.Tracks
                        .FirstOrDefaultAsync(t => t.GlobalId == track.GlobalId);

                    if (existingTrack == null)
                    {
                        // Case 1: New track - INSERT
                        context.Tracks.Add(track);
                    }
                    else
                    {
                        // Case 2: Existing track - UPDATE
                        // Apply only the properties that may change during download lifecycle
                        existingTrack.State = track.State;
                        existingTrack.Filename = track.Filename;
                        existingTrack.ErrorMessage = track.ErrorMessage;
                        existingTrack.CoverArtUrl = track.CoverArtUrl;
                        existingTrack.Artist = track.Artist;
                        existingTrack.Title = track.Title;
                        existingTrack.Size = track.Size;

                        // Phase 0: Persist Spotify Metadata
                        existingTrack.SpotifyTrackId = track.SpotifyTrackId;
                        existingTrack.SpotifyAlbumId = track.SpotifyAlbumId;
                        existingTrack.SpotifyArtistId = track.SpotifyArtistId;
                        existingTrack.AlbumArtUrl = track.AlbumArtUrl;
                        existingTrack.ArtistImageUrl = track.ArtistImageUrl;
                        existingTrack.Genres = track.Genres;
                        existingTrack.Popularity = track.Popularity;
                        existingTrack.CanonicalDuration = track.CanonicalDuration;
                        existingTrack.ReleaseDate = track.ReleaseDate;
                    
                        // Phase 0.1: Musical Intelligence
                        existingTrack.MusicalKey = track.MusicalKey;
                        existingTrack.BPM = track.BPM;
                        existingTrack.AnalysisOffset = track.AnalysisOffset;
                        existingTrack.BitrateScore = track.BitrateScore;
                        existingTrack.AudioFingerprint = track.AudioFingerprint;
                        existingTrack.CuePointsJson = track.CuePointsJson;
                    
                        // Phase 8: Sonic Integrity
                        existingTrack.SpectralHash = track.SpectralHash;
                        existingTrack.QualityConfidence = track.QualityConfidence;
                        existingTrack.FrequencyCutoff = track.FrequencyCutoff;
                        existingTrack.IsTrustworthy = track.IsTrustworthy;
                        existingTrack.QualityDetails = track.QualityDetails;
                        
                        existingTrack.IsEnriched = track.IsEnriched;
                        existingTrack.CompletedAt = track.CompletedAt;

                        // Don't update AddedAt - preserve original
                        // context.Tracks.Update() is not needed - EF Core tracks changes automatically
                    }

                    await context.SaveChangesAsync();
                    return; // Success - exit method
                }
                catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqliteEx && sqliteEx.SqliteErrorCode == 5)
                {
                    // SQLite Error 5: Database is locked
                    if (attempt < maxRetries - 1)
                    {
                        _logger.LogWarning(
                            "SQLite database locked saving track {GlobalId}, attempt {Attempt}/{Max}. Retrying...", 
                            track.GlobalId, attempt + 1, maxRetries);
                        await Task.Delay(100 * (attempt + 1)); // Exponential backoff: 100ms, 200ms, 300ms, 400ms
                        continue; // Retry
                    }
                    else
                    {
                        _logger.LogError(ex, 
                            "Failed to save track {GlobalId} after {Max} attempts - database remains locked", 
                            track.GlobalId, maxRetries);
                        throw;
                    }
                }
                catch (DbUpdateConcurrencyException ex)
                {
                    if (attempt < maxRetries - 1)
                    {
                        // Retry after a brief delay
                        _logger.LogWarning("Concurrency conflict saving track {GlobalId}, attempt {Attempt}/{Max}. Retrying...", 
                            track.GlobalId, attempt + 1, maxRetries);
                        await Task.Delay(50 * (attempt + 1)); // Exponential backoff: 50ms, 100ms, 150ms, 200ms
                        continue;
                    }
                    else
                    {
                        // Final attempt failed
                        _logger.LogError(ex, "Failed to save track {GlobalId} after {Max} attempts due to concurrency conflicts", 
                            track.GlobalId, maxRetries);
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error saving track {GlobalId}", track.GlobalId);
                    throw;
                }
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }
    
    public async Task UpdateTrackFilePathAsync(string globalId, string filePath)
    {
        using var context = new AppDbContext();
        var track = await context.Tracks.FirstOrDefaultAsync(t => t.GlobalId == globalId);
        if (track != null)
        {
            track.Filename = filePath;
            await context.SaveChangesAsync();
        }
    }

    public async Task RemoveTrackAsync(string globalId)
    {
        using var context = new AppDbContext();
        var track = await context.Tracks.FindAsync(globalId);
        if (track != null)
        {
            context.Tracks.Remove(track);
            await context.SaveChangesAsync();
        }
    }

    // Helper to bulk save if needed
    public async Task SaveAllAsync(IEnumerable<TrackEntity> tracks)
    {
        using var context = new AppDbContext();
        foreach(var t in tracks)
        {
            if (!await context.Tracks.AnyAsync(x => x.GlobalId == t.GlobalId))
            {
                await context.Tracks.AddAsync(t);
            }
        }
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Updates the status of a track across all playlists that contain it,
    /// then recalculates the progress counts for those playlists.
    /// </summary>
    public async Task<List<Guid>> UpdatePlaylistTrackStatusAndRecalculateJobsAsync(string trackUniqueHash, TrackStatus newStatus, string? resolvedPath)
    {
        using var context = new AppDbContext();

        // 1. Find all PlaylistTrack entries for this global track hash
        var playlistTracks = await context.PlaylistTracks
            .Where(pt => pt.TrackUniqueHash == trackUniqueHash)
            .ToListAsync();

        if (!playlistTracks.Any()) return new List<Guid>();

        var distinctJobIds = playlistTracks.Select(pt => pt.PlaylistId).Distinct().ToList();

        // 2. Update their status in memory
        foreach (var pt in playlistTracks)
        {
            pt.Status = newStatus;
            
            if (newStatus == TrackStatus.Downloaded || newStatus == TrackStatus.Failed || newStatus == TrackStatus.Skipped)
            {
                pt.CompletedAt = DateTime.UtcNow;
            }

            if (!string.IsNullOrEmpty(resolvedPath))
            {
                pt.ResolvedFilePath = resolvedPath;
            }
        }
        
        // 3. Fetch all affected jobs and all their related tracks in two efficient queries
        var jobsToUpdate = await context.Projects
            .Where(j => distinctJobIds.Contains(j.Id))
            .ToListAsync();

        var allRelatedTracks = await context.PlaylistTracks
            .Where(t => distinctJobIds.Contains(t.PlaylistId))
            .AsNoTracking() // Use NoTracking for the read-only calculation part
            .ToListAsync();

        // 4. Recalculate counts for each job in memory
        foreach (var job in jobsToUpdate)
        {
            // Combine the already-updated tracks with the other tracks for this job
            var currentJobTracks = allRelatedTracks
                .Where(t => t.PlaylistId == job.Id && t.TrackUniqueHash != trackUniqueHash)
                .ToList();
            currentJobTracks.AddRange(playlistTracks.Where(pt => pt.PlaylistId == job.Id));

            job.SuccessfulCount = currentJobTracks.Count(t => t.Status == TrackStatus.Downloaded);
            job.FailedCount = currentJobTracks.Count(t => t.Status == TrackStatus.Failed || t.Status == TrackStatus.Skipped);
        }

        // 5. Update Library Health stats (Write-Behind Cache)
        await UpdateLibraryHealthAsync(context);

        // 6. Save all changes (track status and job counts) in a single transaction
        await context.SaveChangesAsync();
        _logger.LogInformation("Updated status for track {Hash}, affecting {TrackCount} playlist entries and recalculating {JobCount} jobs.", trackUniqueHash, playlistTracks.Count, jobsToUpdate.Count);

        return distinctJobIds;
    }

    // ===== LibraryEntry Methods =====

    public async Task<LibraryEntryEntity?> FindLibraryEntryAsync(string uniqueHash)
    {
        using var context = new AppDbContext();
        return await context.LibraryEntries.FindAsync(uniqueHash);
    }

    public async Task<List<LibraryEntryEntity>> LoadAllLibraryEntriesAsync()
    {
        using var context = new AppDbContext();
        return await context.LibraryEntries.AsNoTracking().ToListAsync();
    }

    public async Task SaveLibraryEntryAsync(LibraryEntryEntity entry)
    {
        using var context = new AppDbContext();
        
        // Robust Upsert Pattern: Check existence first to avoid DbUpdateConcurrencyException
        var existing = await context.LibraryEntries.FindAsync(entry.UniqueHash);
        
        if (existing == null)
        {
            // It doesn't exist, so we ADD it.
            context.LibraryEntries.Add(entry);
        }
        else
        {
            // It exists, so we UPDATE it.
            // Since 'existing' is tracked and 'entry' is detached, we use SetValues.
            context.Entry(existing).CurrentValues.SetValues(entry);
            
            // Ensure LastUsedAt is updated on the tracked entity
            existing.LastUsedAt = DateTime.UtcNow; 
        }
        
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Finds an enriched track by artist and title.
    /// Used by SpotifyEnrichmentService for cache-first lookups.
    /// </summary>
    public async Task<LibraryEntryEntity?> FindEnrichedTrackAsync(string artist, string title)
    {
        using var context = new AppDbContext();
        return await context.LibraryEntries
            .Where(e => e.Artist.ToLower() == artist.ToLower() && 
                       e.Title.ToLower() == title.ToLower() &&
                       e.IsEnriched)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Gets a library entry by its Guid Id.
    /// </summary>
    public async Task<LibraryEntryEntity?> GetLibraryEntryAsync(Guid id)
    {
        using var context = new AppDbContext();
        return await context.LibraryEntries.FirstOrDefaultAsync(e => e.Id == id);
    }

    /// <summary>
    /// Gets a library entry by its unique hash.
    /// </summary>
    public async Task<LibraryEntryEntity?> GetLibraryEntryAsync(string uniqueHash)
    {
        return await FindLibraryEntryAsync(uniqueHash);
    }

    /// <summary>
    /// Finds an enriched track in the global Tracks table by its GlobalId.
    /// Used by enrichment services for cache lookups.
    /// </summary>
    public async Task<TrackEntity?> FindEnrichedTrackAsync(string globalId)
    {
        using var context = new AppDbContext();
        return await context.Tracks.FirstOrDefaultAsync(t => t.GlobalId == globalId && t.IsEnriched);
    }


    public async Task<List<LibraryEntryEntity>> GetLibraryEntriesNeedingEnrichmentAsync(int limit)
    {
        using var context = new AppDbContext();
        return await context.LibraryEntries
            .Where(e => !e.IsEnriched && e.SpotifyTrackId == null)
            .OrderByDescending(e => e.AddedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task UpdateLibraryEntryEnrichmentAsync(string uniqueHash, TrackEnrichmentResult result)
    {
        using var context = new AppDbContext();
        var entry = await context.LibraryEntries.FindAsync(uniqueHash);
        if (entry != null)
        {
            // If we only identified the track (Stage 1), we set IsEnriched=false still, until features are fetched?
            // User script: "Once TrackEntity.IsMetadataEnriched is true... Download... takes over"
            // So if result has NO features (Identify only), we should set IsEnriched = false (or leave it).
            // But we need to save the SpotifyID.

            if (result.Success)
            {
                entry.SpotifyTrackId = result.SpotifyId;
                entry.SpotifyAlbumId = result.SpotifyAlbumId;
                entry.SpotifyArtistId = result.SpotifyArtistId;
                if (!string.IsNullOrEmpty(result.ISRC)) entry.ISRC = result.ISRC;
                if (!string.IsNullOrEmpty(result.AlbumArtUrl) && string.IsNullOrEmpty(entry.AlbumArtUrl))
                    entry.AlbumArtUrl = result.AlbumArtUrl;
                if (!string.IsNullOrEmpty(result.OfficialArtist)) entry.Artist = result.OfficialArtist;
                if (!string.IsNullOrEmpty(result.OfficialTitle)) entry.Title = result.OfficialTitle;

                // Did we get features? (Legacy/Single call pathway)
                if (result.Bpm > 0 || result.Energy > 0)
                {
                    entry.BPM = result.Bpm;
                    entry.Energy = result.Energy;
                    entry.Valence = result.Valence;
                    entry.Danceability = result.Danceability;
                    entry.IsEnriched = true; // Complete!
                }
                else
                {
                    // Stage 1 only - Identified but no features.
                    // Keep IsEnriched = false so Pass 2 picks it up.
                }
            }
            else
            {
                // Identification failed.
                // Should we mark it as "failed" to stop retrying?
                // For now, maybe just log and leave it. Or could have an "EnrichmentFailed" flag.
            }
            
            context.LibraryEntries.Update(entry);
            
            // Sync to PlaylistTracks too
            var relatedTracks = await context.PlaylistTracks.Where(pt => pt.TrackUniqueHash == uniqueHash).ToListAsync();
            foreach (var pt in relatedTracks)
            {
                pt.SpotifyTrackId = result.SpotifyId;
                pt.SpotifyAlbumId = result.SpotifyAlbumId;
                pt.SpotifyArtistId = result.SpotifyArtistId;
                if (!string.IsNullOrEmpty(result.ISRC)) pt.ISRC = result.ISRC;
                if (!string.IsNullOrEmpty(result.AlbumArtUrl)) pt.AlbumArtUrl = result.AlbumArtUrl;
                if (result.Bpm > 0)
                {
                    pt.BPM = result.Bpm;
                    pt.Energy = result.Energy;
                    pt.Valence = result.Valence;
                    pt.Danceability = result.Danceability;
                    pt.IsEnriched = true;
                }
            }
            
            await context.SaveChangesAsync();
        }
    }

    public async Task<List<PlaylistTrackEntity>> GetPlaylistTracksNeedingEnrichmentAsync(int limit)
    {
        using var context = new AppDbContext();
        return await context.PlaylistTracks
            .Where(e => !e.IsEnriched && e.SpotifyTrackId == null)
            .OrderByDescending(e => e.AddedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task UpdatePlaylistTrackEnrichmentAsync(Guid id, TrackEnrichmentResult result)
    {
        using var context = new AppDbContext();
        var track = await context.PlaylistTracks.FindAsync(id);
        if (track != null)
        {
            if (result.Success)
            {
                track.SpotifyTrackId = result.SpotifyId;
                track.SpotifyAlbumId = result.SpotifyAlbumId;
                track.SpotifyArtistId = result.SpotifyArtistId;
                if (!string.IsNullOrEmpty(result.ISRC)) track.ISRC = result.ISRC;
                if (!string.IsNullOrEmpty(result.AlbumArtUrl)) track.AlbumArtUrl = result.AlbumArtUrl;
                
                if (result.Bpm > 0)
                {
                    track.BPM = result.Bpm;
                    track.Energy = result.Energy;
                    track.Valence = result.Valence;
                    track.Danceability = result.Danceability;
                }
            }
            
            // Mark as enriched regardless of success to prevent infinite loops (Stage 0 stall)
            // If it failed, it means we tried and couldn't find it. Don't try again immediately.
            track.IsEnriched = true;
            
            await context.SaveChangesAsync();
        }
    }

    public async Task<List<LibraryEntryEntity>> GetLibraryEntriesNeedingFeaturesAsync(int limit)
    {
         using var context = new AppDbContext();
         // Pass 2 candidates: Have ID, but NO Energy (or IsEnriched is false)
         return await context.LibraryEntries
             .Where(e => e.SpotifyTrackId != null && !e.IsEnriched)
             .OrderByDescending(e => e.AddedAt)
             .Take(limit)
             .ToListAsync();
    }

    public async Task<List<PlaylistTrackEntity>> GetPlaylistTracksNeedingFeaturesAsync(int limit)
    {
        using var context = new AppDbContext();
        return await context.PlaylistTracks
            .Where(e => e.SpotifyTrackId != null && !e.IsEnriched)
            .OrderByDescending(e => e.AddedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task UpdateLibraryEntriesFeaturesAsync(Dictionary<string, SpotifyAPI.Web.TrackAudioFeatures> featuresMap)
    {
        using var context = new AppDbContext();
        var ids = featuresMap.Keys.ToList();
        
        // 1. Update Library Entries
        var entries = await context.LibraryEntries
            .Where(e => e.SpotifyTrackId != null && ids.Contains(e.SpotifyTrackId))
            .ToListAsync();

        foreach (var entry in entries)
        {
            if (entry.SpotifyTrackId != null && featuresMap.TryGetValue(entry.SpotifyTrackId, out var f))
            {
                entry.BPM = f.Tempo;
                entry.Energy = f.Energy;
                entry.Valence = f.Valence;
                entry.Danceability = f.Danceability;
                entry.IsEnriched = true;
            }
        }

        // 2. Update Playlist Tracks (Intelligence Sync)
        var pTracks = await context.PlaylistTracks
            .Where(e => e.SpotifyTrackId != null && ids.Contains(e.SpotifyTrackId))
            .ToListAsync();

        foreach (var pt in pTracks)
        {
            if (pt.SpotifyTrackId != null && featuresMap.TryGetValue(pt.SpotifyTrackId, out var f))
            {
                pt.BPM = f.Tempo;
                pt.Energy = f.Energy;
                pt.Valence = f.Valence;
                pt.Danceability = f.Danceability;
                pt.IsEnriched = true;
            }
        }
        
        await context.SaveChangesAsync();
    }


    // ===== PlaylistJob Methods =====

    public async Task<List<PlaylistJobEntity>> LoadAllPlaylistJobsAsync()
    {
        using var context = new AppDbContext();
        return await context.Projects
            .AsNoTracking()
            .Where(j => !j.IsDeleted)
            .Include(j => j.Tracks)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync();
    }

    public async Task<PlaylistJobEntity?> LoadPlaylistJobAsync(Guid jobId)
    {
        using var context = new AppDbContext();
        // BUGFIX: Also exclude soft-deleted jobs, otherwise duplicate detection finds deleted jobs
        // that won't show in the library list (LoadAllPlaylistJobsAsync filters by !IsDeleted)
        return await context.Projects.AsNoTracking()
            .Include(j => j.Tracks)
            .FirstOrDefaultAsync(j => j.Id == jobId && !j.IsDeleted);

    }

    public async Task SavePlaylistJobAsync(PlaylistJobEntity job)
    {
        using var context = new AppDbContext();

        // Use the same atomic upsert pattern for PlaylistJobs.
        // EF Core will handle INSERT vs. UPDATE based on the job.Id primary key.
        // We set CreatedAt here if it's a new entity. The DB context tracks the entity state.
        if (context.Entry(job).State == EntityState.Detached)
             job.CreatedAt = DateTime.UtcNow;
        context.Projects.Update(job);
        await context.SaveChangesAsync();
        _logger.LogInformation("Saved PlaylistJob: {Title} ({Id})", job.SourceTitle, job.Id);
    }

    public async Task DeletePlaylistJobAsync(Guid jobId)
    {
        using var context = new AppDbContext();
        var job = await context.Projects.FindAsync(jobId);
        if (job != null)
        {
            context.Projects.Remove(job);
            await context.SaveChangesAsync();
            _logger.LogInformation("Deleted PlaylistJob: {Id}", jobId);
        }
    }

    public async Task SoftDeletePlaylistJobAsync(Guid jobId)
    {
        using var context = new AppDbContext();
        var job = await context.Projects.FindAsync(jobId);
        if (job != null)
        {
            job.IsDeleted = true;
            job.DeletedAt = DateTime.UtcNow;
            await context.SaveChangesAsync();
            _logger.LogInformation("Soft-deleted PlaylistJob: {Id}", jobId);
        }
    }

    public async Task<List<PlaylistJobEntity>> LoadDeletedPlaylistJobsAsync()
    {
        using var context = new AppDbContext();
        return await context.Projects
            .AsNoTracking()
            .IgnoreQueryFilters() // Must ignore filter to see deleted items
            .Where(j => j.IsDeleted)
            .Include(j => j.Tracks)
            .OrderByDescending(j => j.DeletedAt)
            .ToListAsync();
    }

    public async Task RestorePlaylistJobAsync(Guid jobId)
    {
        using var context = new AppDbContext();
        var job = await context.Projects.IgnoreQueryFilters().FirstOrDefaultAsync(j => j.Id == jobId);
        if (job != null)
        {
            job.IsDeleted = false;
            job.DeletedAt = null;
            await context.SaveChangesAsync();
            _logger.LogInformation("Restored PlaylistJob: {Id}", jobId);
        }
    }


    // ===== PlaylistTrack Methods =====

    public async Task<List<PlaylistTrackEntity>> LoadPlaylistTracksAsync(Guid jobId)
    {
        using var context = new AppDbContext();
        return await context.PlaylistTracks
            .Where(t => t.PlaylistId == jobId)
            .OrderBy(t => t.TrackNumber)
            .ToListAsync();
    }

    public async Task SavePlaylistTrackAsync(PlaylistTrackEntity track)
    {
        using var context = new AppDbContext();

        // Check if track already exists in database to decide between Add (INSERT) or Update (UPDATE)
        var exists = await context.PlaylistTracks.AnyAsync(t => t.Id == track.Id);

        if (exists)
        {
            // Update existing track
            context.PlaylistTracks.Update(track);
        }
        else
        {
            // Add new track
            track.AddedAt = DateTime.UtcNow;
            context.PlaylistTracks.Add(track);
        }

        await UpdatePlaylistJobCountersAsync(context, track.PlaylistId);
        await context.SaveChangesAsync();
    }

    private static async Task UpdatePlaylistJobCountersAsync(AppDbContext context, Guid playlistId)
    {
        var job = await context.Projects.FirstOrDefaultAsync(j => j.Id == playlistId);
        if (job == null)
        {
            return;
        } 
        var statuses = await context.PlaylistTracks.AsNoTracking()
            .Where(t => t.PlaylistId == playlistId)
            .Select(t => t.Status)
            .ToListAsync();

        job.TotalTracks = statuses.Count;
        job.SuccessfulCount = statuses.Count(s => s == TrackStatus.Downloaded);
        job.FailedCount = statuses.Count(s => s == TrackStatus.Failed || s == TrackStatus.Skipped);

        var remaining = statuses.Count(s => s == TrackStatus.Missing);
        if (job.TotalTracks > 0 && remaining == 0)
        {
            job.CompletedAt ??= DateTime.UtcNow;
        }
        else
        {
            job.CompletedAt = null;
        }
    }

    public async Task SavePlaylistTracksAsync(IEnumerable<PlaylistTrackEntity> tracks)
    {
        using var context = new AppDbContext();
        
        // Apply the atomic upsert pattern for a collection of entities.
        // Set AddedAt for any new entities.
        foreach (var track in tracks.Where(t => context.Entry(t).State == EntityState.Detached))
        {
            track.AddedAt = DateTime.UtcNow;
        }
        context.PlaylistTracks.UpdateRange(tracks);

        await context.SaveChangesAsync();
        _logger.LogInformation("Saved {Count} playlist tracks", tracks.Count());
    }

    public async Task DeletePlaylistTracksAsync(Guid jobId)
    {
        using var context = new AppDbContext();
        var tracks = await context.PlaylistTracks
            .Where(t => t.PlaylistId == jobId)
            .ToListAsync();
        
        foreach (var track in tracks)
        {
            context.PlaylistTracks.Remove(track);
        }
        
        await context.SaveChangesAsync();
        await context.SaveChangesAsync();
        _logger.LogInformation("Deleted {Count} playlist tracks for job {JobId}", tracks.Count, jobId);
    }

    public async Task DeleteSinglePlaylistTrackAsync(Guid playlistTrackId)
    {
        using var context = new AppDbContext();
        var track = await context.PlaylistTracks.FindAsync(playlistTrackId);
        if (track != null)
        {
            context.PlaylistTracks.Remove(track);
            await UpdatePlaylistJobCountersAsync(context, track.PlaylistId);
            await context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Atomically saves a PlaylistJob and all its associated tracks in a single transaction.
    /// This ensures data integrity: either the entire job+tracks are saved, or none are.
    /// Called by DownloadManager.QueueProject() for imports.
    /// </summary>
    public async Task SavePlaylistJobWithTracksAsync(PlaylistJob job)
    {
        // Prevent race conditions with other DB writes
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            using var transaction = await context.Database.BeginTransactionAsync();
            
            try
            {
            // Convert model to entity
            // 2. Handle Job Header (Add or Update)
            // 2. Robust Upserter Strategy
            // Even with semaphore, we check first. If missing, we TRY ADD.
            // If that fails with Unique Constraint, we CATCH and UPDATE (Upsert).
            
            var existingJob = await context.Projects.FirstOrDefaultAsync(j => j.Id == job.Id);
            bool jobExists = existingJob != null;

            if (existingJob != null)
            {
                 // Update existing job logic
                 existingJob.TotalTracks = Math.Max(existingJob.TotalTracks, job.TotalTracks);
                 existingJob.SourceTitle = job.SourceTitle;
                 existingJob.SourceType = job.SourceType;
                 existingJob.IsDeleted = false;
                 context.Projects.Update(existingJob);
            }
            else
            {
                 // Attempt Insert
                 var jobEntity = new PlaylistJobEntity
                 {
                     Id = job.Id,
                     SourceTitle = job.SourceTitle,
                     SourceType = job.SourceType,
                     DestinationFolder = job.DestinationFolder,
                     CreatedAt = job.CreatedAt,
                     TotalTracks = job.TotalTracks,
                     SuccessfulCount = job.SuccessfulCount,
                     FailedCount = job.FailedCount,
                     MissingCount = job.MissingCount,
                     IsDeleted = false
                 };
                 context.Projects.Add(jobEntity);
                 
                 // Immediate save to catch Unique Constraint violation NOW
                 try
                 {
                     await context.SaveChangesAsync();
                     jobExists = true; // Mark as exists for track handling
                 }
                 catch (DbUpdateException dbEx) when (dbEx.InnerException?.Message.Contains("UNIQUE constraint failed") == true)
                 {
                     _logger.LogWarning("Caught Race Condition in PlaylistJob Insert! Switching to Update strategy for JobId {Id}", job.Id);
                     
                     // Detach the failed entity to clear context state
                     context.Entry(jobEntity).State = EntityState.Detached;

                     // Re-fetch the phantom existing job (might be soft-deleted)
                     existingJob = await context.Projects.IgnoreQueryFilters().FirstOrDefaultAsync(j => j.Id == job.Id);
                     if (existingJob != null)
                     {
                         // Un-delete if it was soft-deleted
                         existingJob.TotalTracks = Math.Max(existingJob.TotalTracks, job.TotalTracks);
                         existingJob.IsDeleted = false;
                         context.Projects.Update(existingJob);
                         await context.SaveChangesAsync(); // Save the UPDATE now
                         jobExists = true;
                         
                         // CRITICAL: Ensure jobEntity is NOT tracked as Added
                         // This prevents line 1525 from trying to INSERT it again
                         var trackedEntity = context.ChangeTracker.Entries<PlaylistJobEntity>()
                             .FirstOrDefault(e => e.Entity.Id == job.Id && e.State == EntityState.Added);
                         if (trackedEntity != null)
                         {
                             trackedEntity.State = EntityState.Detached;
                             _logger.LogDebug("Detached duplicate Added entity for JobId {Id}", job.Id);
                         }
                     }
                     else
                     {
                         throw; // Should be impossible
                     }
                 }
            }
            
            // For tracks, we also need to handle Add vs Update. 
            if (!jobExists) // This branch is only taken if we successfully added a NEW job and it stayed new
            {
                var trackEntities = job.PlaylistTracks.Select(track => new PlaylistTrackEntity
                {
                    Id = track.Id,
                    PlaylistId = job.Id,
                    Artist = track.Artist,
                    Title = track.Title,
                    Album = track.Album,
                    TrackUniqueHash = track.TrackUniqueHash,
                    Status = track.Status,
                    ResolvedFilePath = track.ResolvedFilePath,
                    TrackNumber = track.TrackNumber,
                    AddedAt = track.AddedAt,
                    SortOrder = track.SortOrder,
                    Priority = track.Priority,
                    SourcePlaylistId = track.SourcePlaylistId,
                    SourcePlaylistName = track.SourcePlaylistName,
                    // Phase 0: Spotify Metadata
                    SpotifyTrackId = track.SpotifyTrackId,
                    SpotifyAlbumId = track.SpotifyAlbumId,
                    SpotifyArtistId = track.SpotifyArtistId,
                    AlbumArtUrl = track.AlbumArtUrl,
                    ArtistImageUrl = track.ArtistImageUrl,
                    Genres = track.Genres,
                    Popularity = track.Popularity,
                    CanonicalDuration = track.CanonicalDuration,
                    ReleaseDate = track.ReleaseDate,

                    // Phase 0.1: Musical Intelligence
                    MusicalKey = track.MusicalKey,
                    BPM = track.BPM,
                    CuePointsJson = track.CuePointsJson,
                    AudioFingerprint = track.AudioFingerprint,
                    BitrateScore = track.BitrateScore,
                    AnalysisOffset = track.AnalysisOffset,

                    // Phase 8: Sonic Integrity
                    SpectralHash = track.SpectralHash,
                    QualityConfidence = track.QualityConfidence,
                    FrequencyCutoff = track.FrequencyCutoff,
                    IsTrustworthy = track.IsTrustworthy,
                    QualityDetails = track.QualityDetails,

                    // Phase 13: Search Filter Overrides
                    PreferredFormats = track.PreferredFormats,
                    MinBitrateOverride = track.MinBitrateOverride
                });
                context.PlaylistTracks.AddRange(trackEntities);
            }
            else
            {
                var trackIds = job.PlaylistTracks.Select(t => t.Id).ToList();
                var existingTrackIds = await context.PlaylistTracks
                    .Where(t => trackIds.Contains(t.Id))
                    .Select(t => t.Id)
                    .ToListAsync();
                var existingTrackIdSet = new HashSet<Guid>(existingTrackIds);

                foreach (var track in job.PlaylistTracks)
                {
                    var trackEntity = new PlaylistTrackEntity
                    {
                        Id = track.Id,
                        PlaylistId = job.Id,
                        Artist = track.Artist,
                        Title = track.Title,
                        Album = track.Album,
                        TrackUniqueHash = track.TrackUniqueHash,
                        Status = track.Status,
                        ResolvedFilePath = track.ResolvedFilePath,
                        TrackNumber = track.TrackNumber,
                        AddedAt = track.AddedAt,
                        SortOrder = track.SortOrder,
                        Priority = track.Priority,
                        SourcePlaylistId = track.SourcePlaylistId,
                        SourcePlaylistName = track.SourcePlaylistName,
                        // Phase 0: Spotify Metadata
                        SpotifyTrackId = track.SpotifyTrackId,
                        SpotifyAlbumId = track.SpotifyAlbumId,
                        SpotifyArtistId = track.SpotifyArtistId,
                        AlbumArtUrl = track.AlbumArtUrl,
                        ArtistImageUrl = track.ArtistImageUrl,
                        Genres = track.Genres,
                        Popularity = track.Popularity,
                        CanonicalDuration = track.CanonicalDuration,
                        ReleaseDate = track.ReleaseDate,
                        
                        // Phase 0.1: Musical Intelligence
                        MusicalKey = track.MusicalKey,
                        BPM = track.BPM,
                        CuePointsJson = track.CuePointsJson,
                        AudioFingerprint = track.AudioFingerprint,
                        BitrateScore = track.BitrateScore,
                        AnalysisOffset = track.AnalysisOffset,

                        // Phase 8: Sonic Integrity
                        SpectralHash = track.SpectralHash,
                        QualityConfidence = track.QualityConfidence,
                        FrequencyCutoff = track.FrequencyCutoff,
                        IsTrustworthy = track.IsTrustworthy,
                        QualityDetails = track.QualityDetails,

                        // Phase 13: Search Filter Overrides
                        PreferredFormats = track.PreferredFormats,
                        MinBitrateOverride = track.MinBitrateOverride
                    };

                    if (existingTrackIdSet.Contains(track.Id))
                    {
                        context.PlaylistTracks.Update(trackEntity);
                    }
                    else
                    {
                        context.PlaylistTracks.Add(trackEntity);
                    }
                }
            }
            
            await context.SaveChangesAsync();

            // Phase 2: Recalculate and update header counts to ensure accuracy after merges/updates.
            // This prevents "lost counts" when merging a small batch into a large existing playlist.
            var consolidatedJob = await context.Projects.FirstOrDefaultAsync(j => j.Id == job.Id);
            if (consolidatedJob != null)
            {
                // Source of truth is now the aggregate of all tracks for this PlaylistId in the DB
                consolidatedJob.TotalTracks = await context.PlaylistTracks.CountAsync(t => t.PlaylistId == job.Id);
                consolidatedJob.SuccessfulCount = await context.PlaylistTracks.CountAsync(t => t.PlaylistId == job.Id && t.Status == TrackStatus.Downloaded);
                consolidatedJob.FailedCount = await context.PlaylistTracks.CountAsync(t => t.PlaylistId == job.Id && (t.Status == TrackStatus.Failed || t.Status == TrackStatus.Skipped));
                consolidatedJob.MissingCount = await context.PlaylistTracks.CountAsync(t => t.PlaylistId == job.Id && t.Status == TrackStatus.Missing);
                
                context.Projects.Update(consolidatedJob);
                await context.SaveChangesAsync();
            }

            await transaction.CommitAsync();
            
            _logger.LogInformation(
                "Atomically saved PlaylistJob '{Title}' ({Id}) with {TrackCount} tracks. Thread: {ThreadId}",
                job.SourceTitle,
                job.Id,
                job.PlaylistTracks.Count,
                Thread.CurrentThread.ManagedThreadId);
        }
        catch
        {
            throw;
        }
    }
    finally
    {
        _writeSemaphore.Release();
    }
    }

    public async Task LogPlaylistJobDiagnostic(Guid jobId)
    {
        using var context = new AppDbContext();
        var job = await context.Projects
            .AsNoTracking()
            .Include(j => j.Tracks)
            .FirstOrDefaultAsync(j => j.Id == jobId);

        if (job == null)
        {
            _logger.LogWarning("DIAGNOSTIC: JobId {JobId} not found.", jobId);
            return;
        }

        _logger.LogInformation(
            "DIAGNOSTIC for JobId {JobId}: Title='{SourceTitle}', IsDeleted={IsDeleted}, CreatedAt={CreatedAt}, TotalTracks={TotalTracks}",
            job.Id,
            job.SourceTitle,
            job.IsDeleted,
            job.CreatedAt,
            job.TotalTracks
        );

        foreach (var track in job.Tracks)
        {
            _logger.LogInformation(
                "  DIAGNOSTIC for Track {TrackId} in Job {JobId}: Artist='{Artist}', Title='{Title}', TrackUniqueHash='{TrackUniqueHash}', Status='{Status}'",
                track.Id,
                job.Id,
                track.Artist,
                track.Title,
                track.TrackUniqueHash,
                track.Status
            );
        }
    }

    public async Task<List<PlaylistTrackEntity>> GetAllPlaylistTracksAsync()
    {
        using var context = new AppDbContext();
        
        // Filter out tracks from soft-deleted jobs
        var validJobIds = context.Projects
            .Where(j => !j.IsDeleted)
            .Select(j => j.Id);
            
        return await context.PlaylistTracks
            .AsNoTracking()
            .Where(t => validJobIds.Contains(t.PlaylistId))
            .OrderByDescending(t => t.AddedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Phase 3C.5: Lazy Hydration - Fetch pending tracks for the waiting room.
    /// Orders by Priority (0=High) then Time. LIMITs result to buffer size.
    /// </summary>
    public async Task<List<PlaylistTrackEntity>> GetPendingPriorityTracksAsync(int limit, List<Guid> excludeIds)
    {
        using var context = new AppDbContext();
        
        // 1. Get valid jobs
        var validJobIds = context.Projects
            .Where(j => !j.IsDeleted)
            .Select(j => j.Id);
            
        // 2. Query Pending (Status=0) tracks
        // Rule: Priority ASC (0, 1, 10...), then AddedAt ASC (FIFO)
        var query = context.PlaylistTracks
            .AsNoTracking()
            .Where(t => validJobIds.Contains(t.PlaylistId) && t.Status == TrackStatus.Missing);

        if (excludeIds.Any())
        {
            query = query.Where(t => !excludeIds.Contains(t.Id));
        }

        return await query
            .OrderBy(t => t.Priority)
            .ThenBy(t => t.AddedAt)
            .Take(limit)
            .ToListAsync();
    }

    /// <summary>
    /// Phase 3C.5: Lazy Hydration - Fetch all non-pending tracks (History/Active).
    /// </summary>
    public async Task<List<PlaylistTrackEntity>> GetNonPendingTracksAsync()
    {
        using var context = new AppDbContext();
        var validJobIds = context.Projects.Where(j => !j.IsDeleted).Select(j => j.Id);
        
        return await context.PlaylistTracks
            .AsNoTracking()
            .Where(t => validJobIds.Contains(t.PlaylistId) && t.Status != TrackStatus.Missing)
            .OrderByDescending(t => t.AddedAt)
            .ToListAsync();
    }

    public async Task LogActivityAsync(PlaylistActivityLogEntity log)
    {
        using var context = new AppDbContext();
        context.ActivityLogs.Add(log);
        await context.SaveChangesAsync();
    }

    // ===== Queue Persistence Methods (Phase 0) =====

    /// <summary>
    /// Saves the current playback queue to the database.
    /// Clears existing queue and saves the new state.
    /// </summary>
    public async Task SaveQueueAsync(List<(Guid trackId, int position, bool isCurrent)> queueItems)
    {
        using var context = new AppDbContext();
        
        // Clear existing queue
        var existingQueue = await context.QueueItems.ToListAsync();
        context.QueueItems.RemoveRange(existingQueue);
        
        // Add new queue items
        foreach (var (trackId, position, isCurrent) in queueItems)
        {
            context.QueueItems.Add(new QueueItemEntity
            {
                PlaylistTrackId = trackId,
                QueuePosition = position,
                IsCurrentTrack = isCurrent,
                AddedAt = DateTime.UtcNow
            });
        }
        
        await context.SaveChangesAsync();
        _logger.LogInformation("Saved queue with {Count} items", queueItems.Count);
    }

    /// <summary>
    /// Loads the saved playback queue from the database.
    /// Returns queue items with their associated track data.
    /// </summary>
    public async Task<List<(PlaylistTrack track, bool isCurrent)>> LoadQueueAsync()
    {
        using var context = new AppDbContext();
        
        var queueItems = await context.QueueItems
            .OrderBy(q => q.QueuePosition)
            .ToListAsync();
            
        var trackIds = queueItems.Select(q => q.PlaylistTrackId).ToList();
        
        var trackEntities = await context.PlaylistTracks
            .AsNoTracking()
            .Where(t => trackIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id); // Dictionary for O(1) lookup
            
        var result = new List<(PlaylistTrack, bool)>();
        
        foreach (var queueItem in queueItems)
        {
            if (trackEntities.TryGetValue(queueItem.PlaylistTrackId, out var trackEntity))
            {
                var track = new PlaylistTrack
                {
                    Id = trackEntity.Id,
                    PlaylistId = trackEntity.PlaylistId,
                    Artist = trackEntity.Artist,
                    Title = trackEntity.Title,
                    Album = trackEntity.Album,
                    TrackUniqueHash = trackEntity.TrackUniqueHash,
                    Status = trackEntity.Status,
                    ResolvedFilePath = trackEntity.ResolvedFilePath,
                    TrackNumber = trackEntity.TrackNumber,
                    Priority = trackEntity.Priority,
                    SourcePlaylistId = trackEntity.SourcePlaylistId,
                    SourcePlaylistName = trackEntity.SourcePlaylistName,
                    AddedAt = trackEntity.AddedAt,
                    SortOrder = trackEntity.SortOrder,
                    SpotifyTrackId = trackEntity.SpotifyTrackId,
                    AlbumArtUrl = trackEntity.AlbumArtUrl,
                    ArtistImageUrl = trackEntity.ArtistImageUrl,
                    // Map other fields as needed...
                };
                
                result.Add((track, queueItem.IsCurrentTrack));
            }
        }
        
        return result;

    }

    /// <summary>
    /// Clears the saved playback queue from the database.
    /// </summary>
    public async Task ClearQueueAsync()
    {
        using var context = new AppDbContext();
        var existingQueue = await context.QueueItems.ToListAsync();
        context.QueueItems.RemoveRange(existingQueue);
        await context.SaveChangesAsync();
        _logger.LogInformation("Cleared saved queue");
    }

    /// <summary>
    /// Phase 8: Maintenance - Vacuum database to reclaim space and optimize performance.
    /// Should be called periodically (e.g., during daily maintenance).
    /// </summary>
    public async Task VacuumDatabaseAsync()
    {
        try
        {
            using var context = new AppDbContext();
            var connection = context.Database.GetDbConnection();
            await connection.OpenAsync();
            
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "VACUUM";
            await cmd.ExecuteNonQueryAsync();
            
            _logger.LogInformation("Database VACUUM completed successfully");
            await connection.CloseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database VACUUM failed (this is non-critical)");
        }
    }

    /// <summary>
    /// Updates a specific track's engagement metrics (Like status, Rating, PlayCount).
    /// Used by the Media Player UI.
    /// </summary>
    public async Task UpdatePlaylistTrackAsync(PlaylistTrackEntity track)
    {
        try 
        {
            using var connection = GetConnection();
            await connection.OpenAsync();

            const string sql = @"
                UPDATE PlaylistTracks 
                SET IsLiked = @IsLiked,
                    Rating = @Rating,
                    PlayCount = @PlayCount,
                    LastPlayedAt = @LastPlayedAt,
                    Status = @Status
                WHERE Id = @Id";

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            
            cmd.Parameters.AddWithValue("@IsLiked", track.IsLiked);
            cmd.Parameters.AddWithValue("@Rating", track.Rating);
            cmd.Parameters.AddWithValue("@PlayCount", track.PlayCount);
            // Handle nullable types safely for SQLite
            cmd.Parameters.AddWithValue("@LastPlayedAt", (object?)track.LastPlayedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Status", track.Status.ToString());
            cmd.Parameters.AddWithValue("@Id", track.Id);

            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update playlist track {Id}", track.Id);
            throw; // Re-throw to allow ViewModel to handle rollback
        }
    }

    /// <summary>
    /// Phase 3C: Bulk updates Priority for all tracks in a specific playlist.
    /// Used by DownloadManager.PrioritizeProjectAsync for queue orchestration.
    /// </summary>
    public async Task UpdatePlaylistTracksPriorityAsync(Guid playlistId, int newPriority)
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        using var command = conn.CreateCommand();
        command.CommandText = @"
            UPDATE PlaylistTracks 
            SET Priority = @priority 
            WHERE PlaylistId = @playlistId AND Status = 0"; // 0 = Missing (pending download)

        command.Parameters.AddWithValue("@priority", newPriority);
        command.Parameters.AddWithValue("@playlistId", playlistId.ToString());

        var rowsAffected = await command.ExecuteNonQueryAsync();
        _logger.LogInformation("Updated priority to {Priority} for {Count} tracks in playlist {PlaylistId}",
            newPriority, rowsAffected, playlistId);
    }

    /// <summary>
    /// Phase 3C: Updates priority for a single playlist track.
    /// Used for "Bump to Top" or Auto-Download overrides.
    /// </summary>
    public async Task UpdatePlaylistTrackPriorityAsync(Guid trackId, int newPriority)
    {
        using var conn = GetConnection();
        await conn.OpenAsync();

        using var command = conn.CreateCommand();
        command.CommandText = @"UPDATE PlaylistTracks SET Priority = @priority WHERE Id = @id";
        command.Parameters.AddWithValue("@priority", newPriority);
        command.Parameters.AddWithValue("@id", trackId.ToString());

        await command.ExecuteNonQueryAsync();
        _logger.LogDebug("Updated priority to {Priority} for track {Id}", newPriority, trackId);
    }


    private SqliteConnection GetConnection()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dbPath = Path.Combine(appData, "SLSKDONET", "library.db");
        return new SqliteConnection($"Data Source={dbPath}");
    }

    private async Task UpdateLibraryHealthAsync(AppDbContext context)
    {
        try
        {
            var totalTracks = await context.PlaylistTracks.CountAsync();
            var hqTracks = await context.PlaylistTracks.CountAsync(t => t.Bitrate >= 256 || (t.Format != null && t.Format.ToLower() == "flac"));
            var lowBitrateTracks = await context.PlaylistTracks.CountAsync(t => t.Status == TrackStatus.Downloaded && t.Bitrate > 0 && t.Bitrate < 256);
            
            var health = await context.LibraryHealth.FindAsync(1) ?? new LibraryHealthEntity { Id = 1 };
            
            health.TotalTracks = totalTracks;
            health.HqTracks = hqTracks;
            health.UpgradableCount = lowBitrateTracks;
            health.LastScanDate = DateTime.Now;
            
            if (context.Entry(health).State == EntityState.Detached)
            {
                context.LibraryHealth.Add(health);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update library health cache during track update");
        }
    }

    // ===== Phase 1B: WAL Mode & Index Optimization Methods =====

    /// <summary>
    /// Phase 1B: Manually triggers a WAL checkpoint to merge .wal file into main database.
    /// Useful during low-activity periods or before backups.
    /// </summary>
    public async Task CheckpointWalAsync()
    {
        try
        {
            using var context = new AppDbContext();
            var connection = context.Database.GetDbConnection() as SqliteConnection;
            await connection!.OpenAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            
            var result = await cmd.ExecuteScalarAsync();
            _logger.LogInformation("WAL checkpoint completed: {Result}", result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WAL checkpoint failed (non-fatal)");
        }
    }

    /// <summary>
    /// Phase 1B: Audits database indexes and provides optimization recommendations.
    /// Analyzes which indexes exist, which are missing, and which queries are slow.
    /// </summary>
    public async Task<IndexAuditReport> AuditDatabaseIndexesAsync()
    {
        var report = new IndexAuditReport
        {
            AuditDate = DateTime.Now,
            ExistingIndexes = new List<string>(),
            MissingIndexes = new List<IndexRecommendation>(),
            UnusedIndexes = new List<string>()
        };

        try
        {
            using var context = new AppDbContext();
            var connection = context.Database.GetDbConnection() as SqliteConnection;
            await connection!.OpenAsync();

            // STEP 1: Query existing indexes
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT name, tbl_name, sql 
                    FROM sqlite_master 
                    WHERE type='index' AND sql IS NOT NULL
                    ORDER BY tbl_name, name;";

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var indexName = reader.GetString(0);
                    var tableName = reader.GetString(1);
                    
                    report.ExistingIndexes.Add($"{tableName}.{indexName}");
                    _logger.LogDebug("Found index: {IndexName} on {TableName}", indexName, tableName);
                }
            }

            // STEP 2: Define recommended indexes based on common query patterns
            var recommendations = new List<IndexRecommendation>
            {
                new()
                {
                    TableName = "PlaylistTracks",
                    ColumnNames = new[] { "PlaylistId", "Status" },
                    Reason = "Composite index for filtered playlist queries (WHERE PlaylistId = X AND Status = Y)",
                    EstimatedImpact = "High - Used in library sidebar and download manager",
                    CreateIndexSql = "CREATE INDEX IF NOT EXISTS IX_PlaylistTrack_PlaylistId_Status ON PlaylistTracks(PlaylistId, Status);"
                },
                new()
                {
                    TableName = "LibraryEntries",
                    ColumnNames = new[] { "UniqueHash" },
                    Reason = "Global library lookups for cross-project deduplication",
                    EstimatedImpact = "High - Used on every download to check existing files",
                    CreateIndexSql = "CREATE INDEX IF NOT EXISTS IX_LibraryEntry_UniqueHash ON LibraryEntries(UniqueHash);"
                },
                new()
                {
                    TableName = "LibraryEntries",
                    ColumnNames = new[] { "Artist", "Title" },
                    Reason = "Search and filtering in All Tracks view",
                    EstimatedImpact = "Medium - Used in library search",
                    CreateIndexSql = "CREATE INDEX IF NOT EXISTS IX_LibraryEntry_Artist_Title ON LibraryEntries(Artist, Title);"
                },
                new()
                {
                    TableName = "Projects",
                    ColumnNames = new[] { "IsDeleted", "CreatedAt" },
                    Reason = "Filtered project listing (excludes deleted, sorted by date)",
                    EstimatedImpact = "Medium - Used in sidebar project list",
                    CreateIndexSql = "CREATE INDEX IF NOT EXISTS IX_Project_IsDeleted_CreatedAt ON Projects(IsDeleted, CreatedAt);"
                },
            };

            // STEP 3: Check which recommended indexes are missing
            foreach (var rec in recommendations)
            {
                var indexKey = $"{rec.TableName}.{string.Join("_", rec.ColumnNames)}";
                
                // Check if index exists (approximate match)
                var exists = report.ExistingIndexes.Any(idx => 
                    idx.Contains(rec.TableName, StringComparison.OrdinalIgnoreCase) && 
                    rec.ColumnNames.All(col => idx.Contains(col, StringComparison.OrdinalIgnoreCase)));

                if (!exists)
                {
                    report.MissingIndexes.Add(rec);
                    _logger.LogWarning("Missing recommended index: {Index} - {Reason}", indexKey, rec.Reason);
                }
                else
                {
                    _logger.LogInformation("✅ Index exists: {Index}", indexKey);
                }
            }

            _logger.LogInformation(
                "Index Audit Complete: {Existing} existing, {Missing} missing, {Recommendations} total recommendations",
                report.ExistingIndexes.Count,
                report.MissingIndexes.Count,
                recommendations.Count);

            return report;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Index audit failed");
            throw;
        }
    }

    /// <summary>
    /// Applies all recommended indexes from the audit report.
    /// </summary>
    public async Task ApplyIndexRecommendationsAsync(IndexAuditReport report)
    {
        using var context = new AppDbContext();
        var connection = context.Database.GetDbConnection() as SqliteConnection;
        await connection!.OpenAsync();

        foreach (var rec in report.MissingIndexes)
        {
            try
            {
                _logger.LogInformation("Creating index: {Sql}", rec.CreateIndexSql);
                
                using var cmd = connection.CreateCommand();
                cmd.CommandText = rec.CreateIndexSql;
                await cmd.ExecuteNonQueryAsync();
                
                _logger.LogInformation("✅ Created index for {Table}.{Columns}", 
                    rec.TableName, string.Join(", ", rec.ColumnNames));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create index: {Sql}", rec.CreateIndexSql);
            }
        }
    }

    /// <summary>
    /// Phase 1B: Benchmarks database performance before/after WAL mode.
    /// </summary>
    public async Task<PerformanceBenchmark> BenchmarkDatabaseAsync()
    {
        var benchmark = new PerformanceBenchmark();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            using var context = new AppDbContext();
            
            // Test 1: Read 1000 tracks
            stopwatch.Restart();
            var tracks = await context.PlaylistTracks.Take(1000).ToListAsync();
            benchmark.Read1000TracksMs = stopwatch.ElapsedMilliseconds;
            
            // Test 2: Filtered query (common pattern)
            stopwatch.Restart();
            var filtered = await context.PlaylistTracks
                .Where(t => t.Status == TrackStatus.Downloaded)
                .Take(100)
                .ToListAsync();
            benchmark.FilteredQueryMs = stopwatch.ElapsedMilliseconds;
            
            // Test 3: Join query (library entries)
            stopwatch.Restart();
            var joined = await context.LibraryEntries
                .Take(100)
                .ToListAsync();
            benchmark.JoinQueryMs = stopwatch.ElapsedMilliseconds;
            
            _logger.LogInformation(
                "Benchmark: Read={Read}ms, Filter={Filter}ms, Join={Join}ms",
                benchmark.Read1000TracksMs,
                benchmark.FilteredQueryMs,
                benchmark.JoinQueryMs);
            
            return benchmark;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Benchmark failed");
            throw;
        }
    }

    /// <summary>
    /// Closes all database connections and disposes resources during application shutdown.
    /// Prevents orphaned connections and ensures clean process termination.
    /// Note: DatabaseService uses 'using var context' pattern, so no persistent context to dispose.
    /// </summary>
    public async Task CloseConnectionsAsync()
    {
        try
        {
            _logger.LogInformation("Database service shutdown - Running WAL Checkpoint...");
            
            // Phase 5C Hardening: Checkpoint WAL on shutdown to merge -wal file
            using var context = new AppDbContext();
            await context.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(FULL);");
            
            _logger.LogInformation("WAL Checkpoint complete.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during database service shutdown");
        }
    }
    /// <summary>
    /// Retrieves all library entries. Used for bulk operations like Export.
    /// WARN: This can be memory intensive for large libraries.
    /// </summary>
    public async Task<List<LibraryEntryEntity>> GetAllLibraryEntriesAsync()
    {
        using var context = new AppDbContext();
        return await context.LibraryEntries.AsNoTracking().ToListAsync();
    }
    // ===== Genre Enrichment Methods (Stage 3) =====

    public async Task<List<LibraryEntryEntity>> GetLibraryEntriesNeedingGenresAsync(int limit)
    {
        using var context = new AppDbContext();
        return await context.LibraryEntries
            .AsNoTracking()
            .Where(e => !string.IsNullOrEmpty(e.SpotifyArtistId) && e.Genres == null)
            .OrderByDescending(e => e.AddedAt) // Prioritize recent adds
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<PlaylistTrackEntity>> GetPlaylistTracksNeedingGenresAsync(int limit)
    {
        using var context = new AppDbContext();
        return await context.PlaylistTracks
            .AsNoTracking()
            .Where(t => !string.IsNullOrEmpty(t.SpotifyArtistId) && t.Genres == null)
            .OrderByDescending(t => t.AddedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task UpdateLibraryEntriesGenresAsync(Dictionary<string, List<string>> artistGenreMap)
    {
        if (!artistGenreMap.Any()) return;

        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            
            // 1. Update LibraryEntries
            var artistIds = artistGenreMap.Keys.ToList();
            var entries = await context.LibraryEntries
                .Where(e => !string.IsNullOrEmpty(e.SpotifyArtistId) && artistIds.Contains(e.SpotifyArtistId))
                .ToListAsync();

            foreach (var entry in entries)
            {
                if (!string.IsNullOrEmpty(entry.SpotifyArtistId) && artistGenreMap.TryGetValue(entry.SpotifyArtistId, out var genres))
                {
                    // Serialize list to comma-separated string or just take first few?
                    // The property is defined as string? Genres in the entity usually, but let's check
                    // Entity definition said "Genres TEXT NULL", so likely a string.
                    // We'll join with commas.
                    entry.Genres = string.Join(", ", genres);
                }
            }

            // 2. Update PlaylistTracks
            var tracks = await context.PlaylistTracks
                .Where(t => !string.IsNullOrEmpty(t.SpotifyArtistId) && artistIds.Contains(t.SpotifyArtistId))
                .ToListAsync();

            foreach (var track in tracks)
            {
                if (!string.IsNullOrEmpty(track.SpotifyArtistId) && artistGenreMap.TryGetValue(track.SpotifyArtistId, out var genres))
                {
                    track.Genres = string.Join(", ", genres);
                }
            }

            await context.SaveChangesAsync();
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }
    // Phase 15: Style Lab
    public async Task<List<StyleDefinitionEntity>> LoadAllStyleDefinitionsAsync()
    {
        using var context = new AppDbContext();
        return await context.StyleDefinitions.AsNoTracking().ToListAsync();
    }
}


