using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Services;
using SLSKDONET.Services.InputParsers;
using SLSKDONET.Services.Ranking;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Library;
using SLSKDONET.Services.Musical;
using SLSKDONET.ViewModels;
using SLSKDONET.Views;
using System;
using System.IO;

namespace SLSKDONET;

/// <summary>
/// Avalonia application class for cross-platform UI
/// </summary>
public partial class App : Application
{
    public IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Configure services
            Services = ConfigureServices();

            // Register shutdown handler to prevent orphaned processes
            desktop.Exit += async (_, __) =>
            {
                Serilog.Log.Information("Application shutdown initiated - cleaning up services...");
                
                try
                {
                    // Disconnect Soulseek client
                    try
                    {
                        var soulseekAdapter = Services?.GetService<ISoulseekAdapter>();
                        if (soulseekAdapter != null)
                        {
                            Serilog.Log.Information("Disconnecting Soulseek client...");
                            await soulseekAdapter.DisconnectAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(ex, "Failed to disconnect Soulseek client");
                    }

                    // Clear Spotify credentials if configured
                    try
                    {
                        var config = Services?.GetService<ConfigManager>()?.GetCurrent();
                        if (config?.ClearSpotifyOnExit ?? false)
                        {
                            var spotifyAuthService = Services?.GetService<SpotifyAuthService>();
                            if (spotifyAuthService != null)
                            {
                                Serilog.Log.Information("Clearing Spotify credentials...");
                                await spotifyAuthService.ClearCachedCredentialsAsync();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(ex, "Failed to clear Spotify credentials");
                    }

                    // Close database connections
                    try
                    {
                        var databaseService = Services?.GetService<DatabaseService>();
                        if (databaseService != null)
                        {
                            Serilog.Log.Information("Closing database connections...");
                            await databaseService.CloseConnectionsAsync();
                        }

                        // Phase 2A: Close Crash Recovery Journal (prevents locked WAL files)
                        var crashJournal = Services?.GetService<CrashRecoveryJournal>();
                        if (crashJournal != null)
                        {
                            Serilog.Log.Information("Closing crash recovery journal...");
                            await crashJournal.DisposeAsync();
                        }
                        
                        // Phase 3B: Stop Health Monitor
                        var healthMonitor = Services?.GetService<DownloadHealthMonitor>();
                        if (healthMonitor != null)
                        {
                            healthMonitor.Dispose();
                        }
                        
                        // Phase 4.7: Stop Forensic Logger consumer  
                        var forensicLogger = Services?.GetService<TrackForensicLogger>();
                        if (forensicLogger != null && forensicLogger is IDisposable disposable)
                        {
                            Serilog.Log.Information("Stopping forensic logger...");
                            disposable.Dispose();
                        }

                        // Phase 4.1: Ensure Essentia processes are killed
                        var essentiaService = Services?.GetService<SLSKDONET.Services.IAudioIntelligenceService>();
                        if (essentiaService is IDisposable disposableEssentia)
                        {
                             Serilog.Log.Information("Cleaning up Essentia processes...");
                             disposableEssentia.Dispose();
                        }

                        // Stop Download Manager (cancels downloads and saves state)
                        var downloadManager = Services?.GetService<DownloadManager>();
                        if (downloadManager != null)
                        {
                             Serilog.Log.Information("Stopping Download Manager...");
                             downloadManager.Dispose();
                        }
                        
                        // Phase 1: Stop Library Enrichment Worker
                        var enrichmentWorker = Services?.GetService<LibraryEnrichmentWorker>();
                        if (enrichmentWorker != null)
                        {
                            Serilog.Log.Information("Stopping library enrichment worker...");
                            await enrichmentWorker.StopAsync(); // It has StopAsync, not just Dispose
                            enrichmentWorker.Dispose();
                        }
                        
                        // Phase 9: Stop Metadata Orchestrator
                        var orchestrator = Services?.GetService<MetadataEnrichmentOrchestrator>();
                        if (orchestrator is IDisposable disposableOrchestrator)
                        {
                            Serilog.Log.Information("Stopping metadata orchestrator...");
                            disposableOrchestrator.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(ex, "Failed to close database connections or stop services");
                    }

                    Serilog.Log.Information("Application shutdown completed");
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "Error during application shutdown");
                }
                finally
                {
                    // Ensure Serilog is flushed before process terminates
                    Serilog.Log.CloseAndFlush();
                }
            };

            try
            {
                // Phase 2.4: Load ranking strategy from config
                // TEMPORARILY DISABLED: Causing NullReferenceException on startup
                // TODO: Fix this after app launches
                /*
                var config = Services.GetRequiredService<ConfigManager>().GetCurrent();
                ISortingStrategy strategy = (config.RankingPreset ?? "Balanced") switch
                {
                    "Quality First" => new QualityFirstStrategy(),
                    "DJ Mode" => new DJModeStrategy(),
                    _ => new BalancedStrategy()
                };
                ResultSorter.SetStrategy(strategy);
                Serilog.Log.Information("Loaded ranking strategy: {Strategy}", config.RankingPreset ?? "Balanced");
                */
                
                // Phase 10: Biggers App Refactoring - Config Migration
                // Detect legacy weights and migrate to SearchPolicy
                try {
                     var configManager = Services.GetRequiredService<ConfigManager>();
                     var migrationConfig = configManager.Load(); // Reload to be sure
                     var migrationService = Services.GetRequiredService<ConfigMigrationService>();
                     
                     if (migrationService.Migrate(migrationConfig))
                     {
                         configManager.Save(migrationConfig);
                         Serilog.Log.Information("✅ Configuration migrated to 'Biggers App' Search Policy");
                     }
                }
                catch (Exception profEx)
                {
                    Serilog.Log.Warning(profEx, "Config migration failed (non-critical)");
                }

                // Phase 7: Load ranking strategy and weights from config
                var configDispatcher = Services.GetRequiredService<ConfigManager>();
                var config = configDispatcher.GetCurrent() ?? new AppConfig();
                
                string profile = config.RankingProfile ?? "Balanced";
                ISortingStrategy strategy = profile switch
                {
                    "Quality First" => new QualityFirstStrategy(),
                    "DJ Mode" => new DJModeStrategy(),
                    _ => new BalancedStrategy()
                };
                
                ResultSorter.SetStrategy(strategy);
                // ResultSorter.SetWeights(config.CustomWeights ?? ScoringWeights.Balanced); // Removed: Obsolete API
                ResultSorter.SetConfig(config);
                
                Serilog.Log.Information("Loaded ranking strategy: {Profile}", profile);

                // Phase 8: Validate FFmpeg availability - Moved to background task

                // Show Splash Screen first
                var splashScreen = new SLSKDONET.Views.Avalonia.SplashScreen();
                
                // Set as main window temporarily so it shows up as the app window
                desktop.MainWindow = splashScreen;
                splashScreen.Show();
                splashScreen.UpdateStatus("Initializing Database...");
                
                // Yield to let the UI thread render the splash screen
                await Task.Delay(50);
                
                // CRITICAL FIX: Initialize Database BEFORE creating the UI to prevent SQLite locks
                _ = Task.Run(async () =>
                {
                    MainViewModel? mainVm = null;
                    var initCts = new CancellationTokenSource(TimeSpan.FromMinutes(2)); 
                    
                    try
                    {
                        var databaseService = Services.GetRequiredService<DatabaseService>();
                        
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => splashScreen.UpdateStatus("Optimizing Database..."));
                        await databaseService.InitAsync().WaitAsync(initCts.Token);
                        
                        Serilog.Log.Information("✅ Database initialization completed successfully");
                        
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => splashScreen.UpdateStatus("Starting UI..."));
                        await Task.Delay(50);

                        // Create main window and show it immediately on the UI thread
                        // We resolve MainViewModel on the UI thread because it creates UI-bound components (like TreeDataGridSource)
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            mainVm = Services.GetRequiredService<MainViewModel>();
                            mainVm.StatusText = "Finalizing UI...";
                            mainVm.IsInitializing = true;
                            
                            var mainWindow = new Views.Avalonia.MainWindow
                            {
                                DataContext = mainVm
                            };

                            desktop.MainWindow = mainWindow;
                            mainWindow.Show(); 
                            splashScreen.Close();
                        });

                        // --- THE BARRIER: WE ARE NOW DATA-SAFE ---
                        // All subsequent background services that hit the DB can now start.
                        
                        // Initialize and Start DownloadManager Orchestrator
                        var downloadManager = Services.GetRequiredService<DownloadManager>();
                        _ = downloadManager.StartAsync(); // Auto-start engine on launch

                        // Start Library Enrichment Worker (Phase 1)
                        var enrichmentWorker = Services.GetRequiredService<LibraryEnrichmentWorker>();
                        enrichmentWorker.Start();

                        // Phase 9: Start Metadata Orchestrator
                        var orchestrator = Services.GetRequiredService<MetadataEnrichmentOrchestrator>();
                        orchestrator.Start();

                        // Start Mission Control (Phase 0A)
                        var missionControl = Services.GetRequiredService<MissionControlService>();
                        missionControl.Start();

                        // Start Forensic Lockdown Watchdog (Phase 12.7)
                        var lockdownService = Services.GetRequiredService<IForensicLockdownService>();
                        _ = Task.Run(() => lockdownService.MonitorSystemHealthAsync());

                        // Phase 2A: Initialize Crash Recovery Journal
                        try
                        {
                            var crashJournal = Services.GetRequiredService<CrashRecoveryJournal>();
                            await crashJournal.InitAsync();
                            Serilog.Log.Information("✅ Crash Recovery Journal initialized");
                            
                            var crashRecovery = Services.GetRequiredService<CrashRecoveryService>();
                            await crashRecovery.RecoverAsync();
                        }
                        catch (Exception journalEx)
                        {
                            Serilog.Log.Warning(journalEx, "Crash recovery failed (non-critical)");
                        }

                        // Start Library Sync
                        try
                        {
                            var libraryService = Services.GetRequiredService<ILibraryService>();
                            await libraryService.SyncLibraryEntriesFromTracksAsync();
                            Serilog.Log.Information("✅ Start-up Library synchronization completed");
                        }
                        catch (Exception syncEx)
                        {
                            Serilog.Log.Error(syncEx, "Start-up Library sync failed");
                        }
                        
                        // Load projects into the LibraryViewModel
                        await mainVm.LibraryViewModel.LoadProjectsAsync();
                        
                        // Update UI on completion
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            mainVm.IsInitializing = false;
                            mainVm.StatusText = "Ready";
                            Serilog.Log.Information("Background initialization completed");

                            // Start maintenance tasks AFTER initialization is confirmed complete
                            _ = RunMaintenanceTasksAsync();
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        Serilog.Log.Fatal("CRITICAL: Application initialization TIMED OUT after 2 minutes.");
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
                           splashScreen.UpdateStatus("Initialization Timeout. Please restart.");
                        });
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Error(ex, "Background initialization failed");
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => {
                            splashScreen.UpdateStatus($"Error: {ex.Message}");
                        });
                    }
                });
                
            }
            catch (Exception ex)
            {
                // Log startup error
                Serilog.Log.Fatal(ex, "Startup failed during framework initialization");
                throw;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Tray Icon Event Handlers
    private void ShowWindow_Click(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && 
            desktop.MainWindow != null)
        {
            desktop.MainWindow.Show();
            desktop.MainWindow.WindowState = WindowState.Normal;
            desktop.MainWindow.Activate();
        }
    }

    private void HideWindow_Click(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && 
            desktop.MainWindow != null)
        {
            desktop.MainWindow.Hide();
        }
    }

    private void Exit_Click(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        ConfigureSharedServices(services);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Shared service configuration used by both WPF and Avalonia
    /// </summary>
    public static void ConfigureSharedServices(IServiceCollection services)
    {
        // Logging - Use Serilog
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.Services.AddSingleton<ILoggerProvider>(new SerilogLoggerProvider(Serilog.Log.Logger, dispose: true));
        });

        // Configuration
        services.AddSingleton<ConfigMigrationService>(); // [NEW] Biggers App Migration
        services.AddSingleton<ConfigManager>();
        services.AddSingleton(provider =>
        {
            var configManager = provider.GetRequiredService<ConfigManager>();
            var appConfig = configManager.Load();
            if (string.IsNullOrEmpty(appConfig.DownloadDirectory))
                appConfig.DownloadDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "SLSKDONET");
            return appConfig;
        });

        // EventBus - Unified event communication
        services.AddSingleton<IEventBus, EventBusService>();
        
        // Phase 1A: SafeWrite Service - Atomic file operations (ORBIT v1.0)
        services.AddSingleton<SLSKDONET.Services.IO.IFileWriteService, SLSKDONET.Services.IO.SafeWriteService>();
        
        // Phase 2A: Crash Recovery - Journal & Recovery Services (ORBIT v1.0)
        services.AddSingleton<CrashRecoveryJournal>();
        services.AddSingleton<CrashRecoveryService>();
        
        //Session 1: Performance Optimization - Smart caching layer
        services.AddSingleton<LibraryCacheService>();
        
        // Session 2: Performance Optimization - Extracted services
        services.AddSingleton<LibraryOrganizationService>();
        services.AddSingleton<ArtworkPipeline>();
        services.AddSingleton<DragAdornerService>();
        
        // Session 3: Performance Optimization - Polymorphic taggers
        services.AddSingleton<Services.Tagging.Id3Tagger>();
        services.AddSingleton<Services.Tagging.VorbisTagger>();
        services.AddSingleton<Services.Tagging.M4ATagger>();
        services.AddSingleton<Services.Tagging.TaggerFactory>();

        // Services
        services.AddSingleton<SoulseekAdapter>();
        services.AddSingleton<ISoulseekAdapter>(sp => sp.GetRequiredService<SoulseekAdapter>());
        services.AddSingleton<FileNameFormatter>();
        services.AddSingleton<ProtectedDataService>();
        services.AddSingleton<ISoulseekCredentialService, SoulseekCredentialService>();

        // Spotify services
        services.AddHttpClient<SpotifyBatchClient>(); // Phase 7: Batch Client for Throttling Fix
        services.AddSingleton<SpotifyInputSource>();
        services.AddSingleton<SpotifyScraperInputSource>();
        
        // Spotify OAuth services

        services.AddSingleton<ISecureTokenStorage>(sp => SecureTokenStorageFactory.Create(sp));
        services.AddSingleton<SpotifyAuthService>();
        services.AddSingleton<ISpotifyMetadataService, SpotifyMetadataService>();
        services.AddSingleton<SpotifyMetadataService>(); // Keep concrete registration just in case
        services.AddSingleton<ArtworkCacheService>(); // Phase 0: Artwork caching
        services.AddSingleton<SpotifyBulkFetcher>(); // Phase 8: Robust Bulk Fetcher
        
        // Phase 1: Library Enrichment
        services.AddSingleton<SpotifyEnrichmentService>();
        services.AddSingleton<DiscoveryBridgeService>();
        services.AddSingleton<LibraryEnrichmentWorker>();

        // Input parsers
        services.AddSingleton<CsvInputSource>();

        // Import Plugin System
        services.AddSingleton<ImportOrchestrator>();
        // Register concrete types for direct injection
        services.AddSingleton<Services.ImportProviders.SpotifyImportProvider>();
        services.AddSingleton<Services.ImportProviders.CsvImportProvider>();
        services.AddSingleton<Services.ImportProviders.SpotifyLikedSongsImportProvider>();
        services.AddSingleton<Services.ImportProviders.TracklistImportProvider>();
        
        // Phase 1: Persistent Enrichment Queue
        services.AddSingleton<Services.Repositories.IEnrichmentTaskRepository, Services.Repositories.EnrichmentTaskRepository>();
        
        // Register as interface for Orchestrator
        services.AddSingleton<IImportProvider>(sp => sp.GetRequiredService<Services.ImportProviders.SpotifyImportProvider>());
        services.AddSingleton<IImportProvider>(sp => sp.GetRequiredService<Services.ImportProviders.CsvImportProvider>());
        services.AddSingleton<IImportProvider>(sp => sp.GetRequiredService<Services.ImportProviders.SpotifyLikedSongsImportProvider>());
        services.AddSingleton<IImportProvider>(sp => sp.GetRequiredService<Services.ImportProviders.TracklistImportProvider>());

        // Library Action System
        services.AddSingleton<Services.LibraryActions.LibraryActionProvider>();
        services.AddSingleton<Services.LibraryActions.ILibraryAction, Services.LibraryActions.OpenFolderAction>();
        services.AddSingleton<Services.LibraryActions.ILibraryAction, Services.LibraryActions.RemoveFromPlaylistAction>();
        services.AddSingleton<Services.LibraryActions.ILibraryAction, Services.LibraryActions.DeletePlaylistAction>();

        // Download logging and library management
        services.AddSingleton<DownloadLogService>();
        services.AddSingleton<LibraryService>();
        services.AddSingleton<ILibraryService>(provider => provider.GetRequiredService<LibraryService>());
        services.AddSingleton<ColumnConfigurationService>();

        // Audio Player
        services.AddSingleton<IAudioPlayerService, AudioPlayerService>();
        services.AddSingleton<PlayerViewModel>();

        // Metadata and tagging service
        services.AddSingleton<ITaggerService, MetadataTaggerService>();
        services.AddSingleton<IFilePathResolverService, FilePathResolverService>();

        // Rekordbox export service
        services.AddSingleton<Services.Rekordbox.XorService>();
        services.AddSingleton<Services.Rekordbox.AnlzFileParser>();
        services.AddSingleton<RekordboxXmlExporter>();
        services.AddSingleton<Services.Export.RekordboxService>();
        services.AddSingleton<Services.Export.IRekordboxExportService, Services.Export.RekordboxExportService>();
        services.AddSingleton<Services.Export.ExportValidator>();
        services.AddSingleton<Services.Export.ExportPackOrganizer>();
        services.AddSingleton<Services.Export.IGigBagService, Services.Export.GigBagService>();
        
        // Harmonic matching service (DJ feature)
        services.AddSingleton<HarmonicMatchService>();
        services.AddSingleton<ISetIntelligenceService, SetIntelligenceService>(); // Phase 4: Set Curator Intelligence
        
        // Phase 18: Sonic Match Engine - AI-powered vibe similarity
        services.AddSingleton<Services.AI.ISonicMatchService, Services.AI.SonicMatchService>();
        services.AddSingleton<Services.Musical.SonicMatchService>();

        // Phase 25: Universal Music Engine (MusicBrainz Integration)
        services.AddSingleton<IMusicBrainzService, MusicBrainzService>();

        // Phase 2.5: Path provider for safe folder structure
        services.AddSingleton<PathProviderService>();
        
        // Library Folder Scanner
        services.AddSingleton<LibraryFolderScannerService>();

        // Download manager
        
        // Phase 4.6 Hotfix: Search String Normalization
        services.AddSingleton<SearchNormalizationService>();
        
        // Phase 4.7: Forensic Logging (Track-scoped correlation)
        services.AddSingleton<TrackForensicLogger>();
        services.AddSingleton<IForensicLogger>(sp => sp.GetRequiredService<TrackForensicLogger>());
        
        // Phase 7: Forensic Duplication (The Immune System)
        services.AddSingleton<IForensicLockdownService, ForensicLockdownService>();
        services.AddSingleton<ForensicLockdownService>(sp => (ForensicLockdownService)sp.GetRequiredService<IForensicLockdownService>());
        
        // Phase 3: Audio Analysis Services
        services.AddSingleton<WaveformAnalysisService>();
        services.AddSingleton<Services.Analysis.SetlistStressTestService>();
        
        services.AddSingleton<DownloadManager>();
        services.AddSingleton<DownloadHealthMonitor>(); // Phase 3B: Active Health Monitor
        
        // Phase 2.5: Download Center ViewModel (singleton observer)
        services.AddSingleton<ViewModels.Downloads.DownloadCenterViewModel>();

        // Database
        services.AddDbContextFactory<AppDbContext>();
        services.AddSingleton<SchemaMigratorService>();
        services.AddSingleton<SLSKDONET.Services.Repositories.ITrackRepository, SLSKDONET.Services.Repositories.TrackRepository>();
        services.AddSingleton<DatabaseService>();
        services.AddSingleton<DashboardService>(); // [NEW] HomePage Intelligence
        services.AddSingleton<IMetadataService, MetadataService>();

        // Navigation and UI services
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IUserInputService, UserInputService>();
        services.AddSingleton<IFileInteractionService, FileInteractionService>();
        services.AddSingleton<INotificationService, NotificationServiceAdapter>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IDialogService, DialogService>();

        // Mission Control
        services.AddSingleton<MissionControlService>();
        services.AddTransient<SLSKDONET.Services.Missions.SonicAuditMission>();

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<SearchFilterViewModel>(); // [FIX] Added missing registration
        services.AddSingleton<ConnectionViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<HomeViewModel>(); // [NEW] Command Center ViewModel
        services.AddSingleton<BulkOperationViewModel>();
        services.AddSingleton<ExportManagerViewModel>();

        // [NEW] Library Scanning
        services.AddSingleton<LibraryFolderScannerService>();
        
        // Orchestration Services
        services.AddSingleton<SearchOrchestrationService>();
        services.AddSingleton<DownloadOrchestrationService>();
        services.AddSingleton<IBulkOperationCoordinator, BulkOperationCoordinator>(); // Phase 10.5: Refined Workflow
        services.AddSingleton<DownloadDiscoveryService>();
        services.AddSingleton<SearchResultMatcher>(); // Phase 3.1
        services.AddSingleton<MetadataEnrichmentOrchestrator>(); // Phase 3.1
        services.AddSingleton<SonicIntegrityService>(); // Phase 8: Sonic Integrity

        services.AddTransient<IAudioAnalysisService, AudioAnalysisService>(); // Phase 3: Local Audio Analysis
        services.AddTransient<WaveformAnalysisService>(); // Phase 8.1: High-Fidelity Waveforms

        services.AddSingleton<LibraryUpgradeScout>(); // Phase 8: Self-Healing Library
        services.AddSingleton<UpgradeScoutViewModel>();
        services.AddSingleton<Services.Export.RekordboxService>(); // Phase 4: DJ Export
        services.AddSingleton<Services.Export.IHardwareExportService, Services.Export.HardwareExportService>(); // Phase 9: Hardware Export
        
        // Phase 4: Musical Intelligence (The Brain)
        services.AddTransient<IAudioIntelligenceService, EssentiaAnalyzerService>();

        services.AddSingleton<AnalysisQueueService>();
        services.AddSingleton<MusicalBrainTestService>();
        services.AddSingleton<Services.Audio.PhraseDetectionService>(); // Phase 1: Structural Intelligence
        services.AddHostedService<AnalysisWorker>();
        services.AddSingleton<AnalysisQueueViewModel>();
        
        // Phase 4.2: Drop Detection & Cue Generation Engines
        services.AddSingleton<Services.Musical.DropDetectionEngine>();
        services.AddSingleton<Services.Musical.CueGenerationEngine>();
        services.AddSingleton<Services.Musical.ManualCueGenerationService>(); // User-triggered batch cue processing
        
        // Phase 9: Forensic & Style Services
        services.AddSingleton<SonicIntegrityService>();
        services.AddSingleton<SLSKDONET.Services.AI.PersonalClassifierService>();
        services.AddSingleton<SLSKDONET.Services.AI.TensorFlowModelPool>();
        services.AddSingleton<ForensicLibrarianService>(); // Phase 6: Integrity Enforcement

        // Phase 10: Tagging & Mobility
        services.AddSingleton<SLSKDONET.Services.IO.SafeWriteService>();
        services.AddSingleton<SLSKDONET.Services.IO.IFileWriteService>(sp => sp.GetRequiredService<SLSKDONET.Services.IO.SafeWriteService>());

        services.AddSingleton<SLSKDONET.Services.Tagging.ISeratoMarkerService, SLSKDONET.Services.Tagging.SeratoMarkerService>();
        services.AddSingleton<SLSKDONET.Services.Tagging.IUniversalCueService, SLSKDONET.Services.Tagging.UniversalCueService>();

        // Phase 15: Style Lab (Sonic Taxonomy)
        services.AddSingleton<Services.AI.IStyleClassifierService, Services.AI.StyleClassifierService>();
        services.AddTransient<ViewModels.StyleLabViewModel>();

        // Phase 16: Applied Intelligence (Autonomy)
        services.AddSingleton<Services.Library.SmartSorterService>();
        services.AddTransient<ViewModels.Tools.SortPreviewViewModel>();

        // Phase 20: Smart Playlists 2.0
        services.AddSingleton<ISmartPlaylistService, SmartPlaylistService>();
        
        // Phase 23: Smart Crates (Dynamic AI Playlists)
        services.AddSingleton<SmartCrateService>();
        
        // Phase 24: Stem Workspace (Stem Separation & Remixing)
        services.AddSingleton<StemSeparationService>();
        services.AddSingleton<StemProjectService>();
        services.AddTransient<Services.Audio.RealTimeStemEngine>();
        services.AddSingleton<ViewModels.Stem.StemWorkspaceViewModel>(); // Root VM for workspace
        
        // Phase 4: Set Designer (DAW)
        services.AddSingleton<Services.Audio.MultiTrackEngine>();
        services.AddSingleton<Services.Audio.TransitionEngine>();
        services.AddSingleton<ViewModels.Timeline.SetDesignerViewModel>();
        
        // Sprint 5: Transition Preview Engine
        services.AddSingleton<Services.Audio.TransitionPreviewService>();
        services.AddTransient<ViewModels.MixPreviewViewModel>();
        services.AddSingleton<Func<ViewModels.MixPreviewViewModel>>(sp => () => sp.GetRequiredService<ViewModels.MixPreviewViewModel>());
        
        // Phase 0: ViewModel Refactoring - Library child ViewModels
        services.AddTransient<ViewModels.Library.ProjectListViewModel>();
        services.AddTransient<ViewModels.Library.TrackListViewModel>(sp => 
        {
            return new ViewModels.Library.TrackListViewModel(
                sp.GetRequiredService<ILogger<ViewModels.Library.TrackListViewModel>>(),
                sp.GetRequiredService<ILibraryService>(),
                sp.GetRequiredService<DownloadManager>(),
                sp.GetRequiredService<ArtworkCacheService>(),
                sp.GetRequiredService<IEventBus>(),
                sp.GetRequiredService<AppConfig>(),
                sp.GetRequiredService<MetadataEnrichmentOrchestrator>(),
                sp.GetRequiredService<AnalysisQueueService>(),
                sp.GetRequiredService<IBulkOperationCoordinator>()
            );
        });
        services.AddTransient<ViewModels.Library.TrackOperationsViewModel>();
        services.AddTransient<ViewModels.Library.SmartPlaylistViewModel>();
        
        // Contextual Sidebar ViewModels (refactored module)
        services.AddSingleton<Features.LibrarySidebar.ViewModels.MetadataSidebarViewModel>();
        services.AddSingleton<Features.LibrarySidebar.ViewModels.BulkActionSidebarViewModel>();
        services.AddSingleton<Features.LibrarySidebar.ViewModels.SimilaritySidebarViewModel>();
        services.AddSingleton<Features.LibrarySidebar.ViewModels.ForensicSidebarViewModel>();
        services.AddSingleton<Features.LibrarySidebar.ViewModels.CueSidebarViewModel>();
        services.AddSingleton<Features.LibrarySidebar.ViewModels.StemSidebarViewModel>();
        services.AddSingleton<Features.LibrarySidebar.ViewModels.VibeSidebarViewModel>();
        services.AddSingleton<Features.LibrarySidebar.ViewModels.TransitionProberViewModel>();
        services.AddSingleton<Features.LibrarySidebar.ViewModels.ContextualSidebarViewModel>();

        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<ImportPreviewViewModel>();
        services.AddSingleton<ImportHistoryViewModel>();
        services.AddSingleton<SpotifyImportViewModel>();
        services.AddSingleton<ViewModels.LibrarySourcesViewModel>();
        services.AddSingleton<TheaterModeViewModel>();
        services.AddSingleton<ViewModels.Discovery.DiscoveryHubViewModel>(); // Discovery Hub
        services.AddSingleton<Services.Import.AutoCleanerService>();

        // Utilities
        services.AddSingleton<SearchQueryNormalizer>();
        
        // Gatekeeper Service
        services.AddSingleton<ISafetyFilterService, SafetyFilterService>();
        services.AddSingleton<StartupHealthCheckService>();
        services.AddSingleton<StyleLabPersistenceVerifier>();

        // Phase 10.5: Native Dependency Health (Reliability)
        services.AddSingleton<NativeDependencyHealthService>();
        
        // Views - Register all page controls for NavigationService
        services.AddTransient<Views.Avalonia.HomePage>();
        services.AddTransient<Views.Avalonia.SearchPage>();
        services.AddTransient<Views.Avalonia.LibraryPage>();
        services.AddTransient<Views.Avalonia.DownloadsPage>();
        services.AddTransient<Views.Avalonia.SettingsPage>();
        services.AddTransient<Views.Avalonia.ImportPage>();
        services.AddTransient<Views.Avalonia.ImportPreviewPage>();
        services.AddTransient<Views.Avalonia.UpgradeScoutView>();
        services.AddTransient<Views.Avalonia.InspectorPage>();
        services.AddTransient<Views.Avalonia.AnalysisQueuePage>();
        services.AddTransient<Views.Avalonia.StyleLabPage>();
        services.AddTransient<Views.Avalonia.TheaterModePage>();
        services.AddTransient<Views.Avalonia.Timeline.SetDesignerView>();
        services.AddTransient<Views.Avalonia.FlowBuilderView>();
        services.AddTransient<Views.Avalonia.DiscoveryHubView>(); // [FIX] Added missing view registration
        services.AddTransient<Views.Avalonia.DJCompanionView>(); // [FIX] Added missing view registration
        
        services.AddSingleton<ViewModels.TrackInspectorViewModel>();
        services.AddSingleton<ViewModels.ForensicLabViewModel>();
        services.AddSingleton<ViewModels.ForensicUnifiedViewModel>();
        services.AddSingleton<ViewModels.DJCompanionViewModel>(); // [FIX] Added missing ViewModel registration
        services.AddSingleton<ViewModels.IntelligenceCenterViewModel>();
        services.AddSingleton<ViewModels.FlowBuilderViewModel>();

        // Phase 2: Surgical Editing Engine
        services.AddSingleton<ISurgicalProcessingService, SurgicalProcessingService>();
        services.AddSingleton<StemCacheService>();

        // Phase 3: Set-Prep Intelligence
        services.AddSingleton<IPhraseAlignmentService, PhraseAlignmentService>();
        services.AddSingleton<VocalIntelligenceService>();
        services.AddSingleton<ITransitionAdvisorService, TransitionAdvisorService>();
        services.AddSingleton<SetListService>();
        services.AddSingleton<ITransitionPreviewPlayer, TransitionPreviewPlayer>();
        
        // Stress Test Service (Performance Validation)
        services.AddSingleton<Services.Testing.CockpitStressTestService>();
        services.AddSingleton<Services.Testing.SessionAutopsyService>();
        services.AddSingleton<Services.Testing.GenreBridgeTestService>();
        services.AddSingleton<Services.Testing.MockLibraryGenerator>();
        services.AddSingleton<Services.Testing.ScalabilityStressTestService>();
        services.AddSingleton<DiagnosticsViewModel>();
    }

    /// <summary>
    /// Phase 8: Maintenance Task - Runs daily cleanup operations.
    /// - Deletes backup files older than 7 days
    /// - Vacuums database for performance
    /// </summary>
    private async Task RunMaintenanceTasksAsync()
    {
        await PerformMaintenanceAsync();
    }

    private async Task PerformMaintenanceAsync()
    {
        var config = Services?.GetService<AppConfig>();
        if (config == null) return;
        
        Serilog.Log.Information("[Maintenance] Starting daily maintenance tasks...");
        
        // Task 1: Clean old backup files (7-day retention)
        if (!string.IsNullOrEmpty(config.DownloadDirectory) && Directory.Exists(config.DownloadDirectory))
        {
            try
            {
                var backupFiles = Directory.GetFiles(config.DownloadDirectory, "*.backup", SearchOption.AllDirectories)
                    .Where(f => File.GetCreationTime(f) < DateTime.Now.AddDays(-7))
                    .ToList();
                
                if (backupFiles.Any())
                {
                    foreach (var backupFile in backupFiles)
                    {
                        try
                        {
                            File.Delete(backupFile);
                            Serilog.Log.Debug("[Maintenance] Deleted old backup: {File}", Path.GetFileName(backupFile));
                        }
                        catch (Exception ex)
                        {
                            Serilog.Log.Warning(ex, "[Maintenance] Failed to delete backup: {File}", backupFile);
                        }
                    }
                    
                    Serilog.Log.Information("[Maintenance] Cleaned {Count} old backup files (>7 days)", backupFiles.Count);
                }
                else
                {
                    Serilog.Log.Debug("[Maintenance] No old backups to clean");
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[Maintenance] Backup cleanup failed");
            }
        }
        
        // Task 2: Vacuum database for performance
        try
        {
            var dbService = Services?.GetService<DatabaseService>();
            if (dbService != null)
            {
                await dbService.VacuumDatabaseAsync();
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[Maintenance] Database vacuum failed");
        }
        
        Serilog.Log.Information("[Maintenance] Daily maintenance completed");
    }

}
