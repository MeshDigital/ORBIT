using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Services;
using SLSKDONET.Services.InputParsers;
using SLSKDONET.Services.Ranking;
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
                // Initialize database before anything else (required for app to function)
                var databaseService = Services.GetRequiredService<DatabaseService>();
                databaseService.InitAsync().GetAwaiter().GetResult();

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
                ResultSorter.SetWeights(config.CustomWeights ?? ScoringWeights.Balanced);
                ResultSorter.SetConfig(config);
                
                Serilog.Log.Information("Loaded ranking strategy: {Profile}", profile);

                // Phase 8: Validate FFmpeg availability - Moved to background task

                // Create main window and show it immediately
                MainViewModel mainVm;
                try 
                {
                    mainVm = Services.GetRequiredService<MainViewModel>();
                }
                catch (Exception diEx)
                {
                    Serilog.Log.Fatal(diEx, "DI RESOLUTION ERROR: {Message}", diEx.GetBaseException().Message);
                    throw;
                }
                mainVm.StatusText = "Initializing application...";
                
                var mainWindow = new Views.Avalonia.MainWindow
                {
                    DataContext = mainVm
                };

                desktop.MainWindow = mainWindow;
                
                // Start background initialization (non-blocking)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // CRITICAL FIX: Proactively verify Spotify connection on startup
                        // This prevents the "zombie token" bug where tokens are invalid but UI shows "Connected"
                        try
                        {
                            var spotifyAuthService = Services.GetRequiredService<SpotifyAuthService>();
                            await spotifyAuthService.VerifyConnectionAsync();
                        }
                        catch (Exception spotifyEx)
                        {
                            Serilog.Log.Warning(spotifyEx, "Spotify connection verification failed (non-critical)");
                        }

                        // Phase 8: Validate FFmpeg availability (Moved from startup)
                        try
                        {
                            var sonicService = Services.GetRequiredService<SonicIntegrityService>();
                            var ffmpegAvailable = await sonicService.ValidateFfmpegAsync();
                            if (!ffmpegAvailable)
                                Serilog.Log.Warning("FFmpeg not found in PATH. Sonic Integrity features will be disabled.");
                            else
                                Serilog.Log.Information("FFmpeg validation successful - Phase 8 features enabled");
                        }
                        catch (Exception ffmpegEx)
                        {
                            Serilog.Log.Warning(ffmpegEx, "FFmpeg validation failed (non-critical)");
                        }

                        // Phase 2A: Initialize Crash Recovery Journal
                        try
                        {
                            var crashJournal = Services.GetRequiredService<CrashRecoveryJournal>();
                            await crashJournal.InitAsync();
                            Serilog.Log.Information("✅ Crash Recovery Journal initialized");

                            // Phase 2A: Run crash recovery with delay for UI breathing room
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(3000); // Wait 3s ensures UI & EventBus are fully engaged
                                try
                                {
                                    var crashRecovery = Services.GetRequiredService<CrashRecoveryService>();
                                    await crashRecovery.RecoverAsync();
                                }
                                catch (Exception recoveryEx)
                                {
                                    Serilog.Log.Error(recoveryEx, "Crash recovery failed (non-critical)");
                                }
                            });
                        }
                        catch (Exception journalEx)
                        {
                            Serilog.Log.Warning(journalEx, "Crash recovery journal initialization failed (non-critical)");
                        }

                        // Initialize DownloadManager
                        var downloadManager = Services.GetRequiredService<DownloadManager>();
                        await downloadManager.InitAsync();
                        _ = downloadManager.StartAsync();

                        // Phase 3B: Start Health Monitor
                        var healthMonitor = Services.GetRequiredService<DownloadHealthMonitor>();
                        healthMonitor.StartMonitoring();

                        // Start Library Enrichment Worker (Phase 1)
                        var enrichmentWorker = Services.GetRequiredService<LibraryEnrichmentWorker>();
                        enrichmentWorker.Start();

                        // Phase 9: Start Metadata Orchestrator (with 15s delay)
                        // Must run to catch "Pending Orchestrations" from previous session
                        var orchestrator = Services.GetRequiredService<MetadataEnrichmentOrchestrator>();
                        orchestrator.Start();

                        // Start Library Sync (Phase 13: "All Tracks" persistence)
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
                        
                        // Start Mission Control (Phase 0A)
                        var missionControl = Services.GetRequiredService<MissionControlService>();
                        missionControl.Start();
                        
                        // AnalysisWorker auto-starts as a hosted service (registered in DI)
                        //  No manual startup required - it begins processing automatically
                        
                        // Load projects into the LibraryViewModel that's bound to UI
                        // CRITICAL: Use mainVm.LibraryViewModel (the one shown in UI)
                        // not a new instance from DI
                        await mainVm.LibraryViewModel.LoadProjectsAsync();
                        
                        // Update UI on completion
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            mainVm.IsInitializing = false;
                            mainVm.StatusText = "Ready";
                            Serilog.Log.Information("Background initialization completed");
                        });
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Error(ex, "Background initialization failed");
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            mainVm.StatusText = "Initialization failed - check logs";
                            mainVm.IsInitializing = false;
                        });
                    }
                });
                
                // Phase 8: Start maintenance tasks (backup cleanup, database vacuum)
                _ = RunMaintenanceTasksAsync();
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
        
        // Harmonic matching service (DJ feature)
        services.AddSingleton<HarmonicMatchService>();

        // Phase 2.5: Path provider for safe folder structure
        services.AddSingleton<PathProviderService>();

        // Download manager
        
        // Phase 4.6 Hotfix: Search String Normalization
        services.AddSingleton<SearchNormalizationService>();
        
        // Phase 4.7: Forensic Logging (Track-scoped correlation)
        services.AddSingleton<TrackForensicLogger>();
        services.AddSingleton<IForensicLogger>(sp => sp.GetRequiredService<TrackForensicLogger>());
        
        // Phase 3: Audio Analysis Services
        services.AddSingleton<WaveformAnalysisService>();
        
        services.AddSingleton<DownloadManager>();
        services.AddSingleton<DownloadHealthMonitor>(); // Phase 3B: Active Health Monitor
        
        // Phase 2.5: Download Center ViewModel (singleton observer)
        services.AddSingleton<ViewModels.Downloads.DownloadCenterViewModel>();

        // Database
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

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<ConnectionViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<HomeViewModel>(); // [NEW] Command Center ViewModel
        
        // Orchestration Services
        services.AddSingleton<SearchOrchestrationService>();
        services.AddSingleton<DownloadOrchestrationService>();
        services.AddSingleton<DownloadDiscoveryService>();
        services.AddSingleton<SearchResultMatcher>(); // Phase 3.1
        services.AddSingleton<MetadataEnrichmentOrchestrator>(); // Phase 3.1
        services.AddSingleton<SearchResultMatcher>(); // Phase 3.1
        services.AddSingleton<MetadataEnrichmentOrchestrator>(); // Phase 3.1
        services.AddSingleton<SonicIntegrityService>(); // Phase 8: Sonic Integrity

        services.AddSingleton<IAudioAnalysisService, AudioAnalysisService>(); // Phase 3: Local Audio Analysis
        services.AddSingleton<WaveformAnalysisService>(); // Phase 8.1: High-Fidelity Waveforms
        services.AddSingleton<LibraryUpgradeScout>(); // Phase 8: Self-Healing Library
        services.AddSingleton<UpgradeScoutViewModel>();
        services.AddSingleton<Services.Export.RekordboxService>(); // Phase 4: DJ Export
        
        // Phase 4: Musical Intelligence (The Brain)
        services.AddSingleton<IAudioIntelligenceService, EssentiaAnalyzerService>();
        services.AddSingleton<AnalysisQueueService>();
        services.AddSingleton<MusicalBrainTestService>();
        services.AddHostedService<AnalysisWorker>();
        services.AddSingleton<AnalysisQueueViewModel>();
        
        // Phase 4.2: Drop Detection & Cue Generation Engines
        services.AddSingleton<Services.Musical.DropDetectionEngine>();
        services.AddSingleton<Services.Musical.CueGenerationEngine>();
        services.AddSingleton<Services.Musical.ManualCueGenerationService>(); // User-triggered batch cue processing
        
        // Phase 15: Style Lab (Sonic Taxonomy)
        services.AddSingleton<Services.AI.PersonalClassifierService>();
        services.AddSingleton<Services.AI.IStyleClassifierService, Services.AI.StyleClassifierService>();
        services.AddTransient<ViewModels.StyleLabViewModel>();

        // Phase 16: Applied Intelligence (Autonomy)
        services.AddSingleton<Services.Library.SmartSorterService>();
        services.AddTransient<ViewModels.Tools.SortPreviewViewModel>();
        
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
                sp.GetRequiredService<AnalysisQueueService>()
            );
        });
        services.AddTransient<ViewModels.Library.TrackOperationsViewModel>();
        services.AddTransient<ViewModels.Library.SmartPlaylistViewModel>();
        
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<ImportPreviewViewModel>();
        services.AddSingleton<ImportHistoryViewModel>();
        services.AddSingleton<SpotifyImportViewModel>();

        // Utilities
        services.AddSingleton<SearchQueryNormalizer>();
        
        // Gatekeeper Service
        services.AddSingleton<ISafetyFilterService, SafetyFilterService>();
        
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
        
        // Singleton ViewModels
        services.AddSingleton<ViewModels.TrackInspectorViewModel>();
    }

    /// <summary>
    /// Phase 8: Maintenance Task - Runs daily cleanup operations.
    /// - Deletes backup files older than 7 days
    /// - Vacuums database for performance
    /// </summary>
    private async Task RunMaintenanceTasksAsync()
    {
        try
        {
            // Wait 5 minutes after app startup before running first maintenance
            await Task.Delay(TimeSpan.FromMinutes(5));
            
            while (true)
            {
                try
                {
                    await PerformMaintenanceAsync();
                }
                catch (Exception ex)
                {
                    // Don't crash app on maintenance errors
                    Serilog.Log.Warning(ex, "Maintenance task failed (non-critical)");
                }
                
                // Run maintenance daily
                await Task.Delay(TimeSpan.FromHours(24));
            }
        }
        catch (TaskCanceledException)
        {
            // App is shutting down
            Serilog.Log.Debug("Maintenance task canceled (app shutdown)");
        }
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
