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

            try
            {
                // Initialize database before anything else (required for app to function)
                var databaseService = Services.GetRequiredService<DatabaseService>();
                databaseService.InitAsync().GetAwaiter().GetResult();

                // Diagnostic: Check for LibVLC files
                var binDir = AppContext.BaseDirectory;
                var libVlcDir = Path.Combine(binDir, "libvlc", "win-x64");
                if (!Directory.Exists(libVlcDir)) libVlcDir = Path.Combine(binDir, "libvlc", "win-x86"); // Try x86
                
                if (Directory.Exists(libVlcDir))
                {
                     Serilog.Log.Information("LibVLC directory found at: {Path}", libVlcDir);
                     var dllCount = Directory.GetFiles(libVlcDir, "*.dll").Length;
                     Serilog.Log.Information("LibVLC directory contains {Count} DLLs", dllCount);
                }
                else
                {
                     Serilog.Log.Error("CRITICAL: LibVLC directory NOT found at {Path}. Playback will fail.", libVlcDir);
                }
                
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
                
                // Phase 7: Load ranking strategy and weights from config
                var configDispatcher = Services.GetRequiredService<ConfigManager>();
                var config = configDispatcher.GetCurrent() ?? new AppConfig();
                ISortingStrategy strategy = (config.RankingPreset ?? "Balanced") switch
                {
                    "Quality First" => new QualityFirstStrategy(),
                    "DJ Mode" => new DJModeStrategy(),
                    _ => new BalancedStrategy()
                };
                ResultSorter.SetStrategy(strategy);
                ResultSorter.SetWeights(config.CustomWeights ?? ScoringWeights.Balanced);
                
                Serilog.Log.Information("Loaded ranking strategy: {Strategy} with custom weights", config.RankingPreset ?? "Balanced");

                // Phase 8: Validate FFmpeg availability - Moved to background task

                // Create main window and show it immediately
                var mainVm = Services.GetRequiredService<MainViewModel>();
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

                        // Initialize DownloadManager
                        var downloadManager = Services.GetRequiredService<DownloadManager>();
                        await downloadManager.InitAsync();
                        _ = downloadManager.StartAsync();
                        
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
        
        // Session 1: Performance Optimization - Smart caching layer
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
        services.AddSingleton<LocalHttpServer>();
        services.AddSingleton<ISecureTokenStorage>(sp => SecureTokenStorageFactory.Create(sp));
        services.AddSingleton<SpotifyAuthService>();
        services.AddSingleton<ISpotifyMetadataService, SpotifyMetadataService>();
        services.AddSingleton<SpotifyMetadataService>(); // Keep concrete registration just in case
        services.AddSingleton<ArtworkCacheService>(); // Phase 0: Artwork caching

        // Input parsers
        services.AddSingleton<CsvInputSource>();

        // Import Plugin System
        services.AddSingleton<ImportOrchestrator>();
        // Register concrete types for direct injection
        services.AddSingleton<Services.ImportProviders.SpotifyImportProvider>();
        services.AddSingleton<Services.ImportProviders.CsvImportProvider>();
        services.AddSingleton<Services.ImportProviders.SpotifyLikedSongsImportProvider>();
        services.AddSingleton<Services.ImportProviders.TracklistImportProvider>();
        
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
        services.AddSingleton<RekordboxXmlExporter>();

        // Download manager
        services.AddSingleton<DownloadManager>();

        // Database
        services.AddSingleton<DatabaseService>();
        services.AddSingleton<IMetadataService, MetadataService>();

        // Navigation and UI services
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IUserInputService, UserInputService>();
        services.AddSingleton<IFileInteractionService, FileInteractionService>();
        services.AddSingleton<INotificationService, NotificationServiceAdapter>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IDialogService, DialogService>();

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<ConnectionViewModel>();
        services.AddSingleton<SettingsViewModel>();
        
        // Orchestration Services
        services.AddSingleton<SearchOrchestrationService>();
        services.AddSingleton<DownloadOrchestrationService>();
        services.AddSingleton<DownloadDiscoveryService>(); // Phase 3.1
        services.AddSingleton<MetadataEnrichmentOrchestrator>(); // Phase 3.1
        services.AddSingleton<SonicIntegrityService>(); // Phase 8: Sonic Integrity
        services.AddSingleton<LibraryUpgradeScout>(); // Phase 8: Self-Healing Library
        services.AddSingleton<UpgradeScoutViewModel>();
        
        // Phase 0: ViewModel Refactoring - Library child ViewModels
        services.AddTransient<ViewModels.Library.ProjectListViewModel>();
        services.AddTransient<ViewModels.Library.TrackListViewModel>();
        services.AddTransient<ViewModels.Library.TrackOperationsViewModel>();
        services.AddTransient<ViewModels.Library.SmartPlaylistViewModel>();
        
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<ImportPreviewViewModel>();
        services.AddSingleton<ImportHistoryViewModel>();
        services.AddSingleton<SpotifyImportViewModel>();

        // Utilities
        services.AddSingleton<SearchQueryNormalizer>();
        
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
