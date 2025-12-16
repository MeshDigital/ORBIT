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

    public override void OnFrameworkInitializationCompleted()
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
                        // Initialize DownloadManager
                        var downloadManager = Services.GetRequiredService<DownloadManager>();
                        await downloadManager.InitAsync();
                        _ = downloadManager.StartAsync();
                        
                        // Initialize LibraryViewModel
                        var libraryViewModel = Services.GetRequiredService<LibraryViewModel>();
                        libraryViewModel.SetMainViewModel(mainVm);
                        await libraryViewModel.LoadProjectsAsync();
                        
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

        // Services
        services.AddSingleton<SoulseekAdapter>();
        services.AddSingleton<ISoulseekAdapter>(sp => sp.GetRequiredService<SoulseekAdapter>());
        services.AddSingleton<FileNameFormatter>();
        services.AddSingleton<ProtectedDataService>();
        services.AddSingleton<ISoulseekCredentialService, SoulseekCredentialService>();

        // Spotify services
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

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<ConnectionViewModel>();
        services.AddSingleton<SettingsViewModel>();
        
        // Orchestration Services
        services.AddSingleton<SearchOrchestrationService>();
        services.AddSingleton<DownloadOrchestrationService>();
        
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
        // Views
        services.AddTransient<Views.Avalonia.ImportPreviewPage>();
    }
}
