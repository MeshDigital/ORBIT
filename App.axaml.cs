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
                // Initialize database before anything else
                var databaseService = Services.GetRequiredService<DatabaseService>();
                databaseService.InitAsync().GetAwaiter().GetResult();

                // Create main window
                var mainWindow = new Views.Avalonia.MainWindow
                {
                    DataContext = Services.GetRequiredService<MainViewModel>()
                };

                // Eagerly instantiate LibraryViewModel so it subscribes to ProjectAdded events
                var libraryViewModel = Services.GetRequiredService<LibraryViewModel>();
                var mainViewModel = Services.GetRequiredService<MainViewModel>();
                libraryViewModel.SetMainViewModel(mainViewModel);
                
                // Load playlists from database
                libraryViewModel.LoadProjectsAsync().GetAwaiter().GetResult();

                // Start the Download Manager loop
                var downloadManager = Services.GetRequiredService<DownloadManager>();
                downloadManager.InitAsync().GetAwaiter().GetResult();
                _ = downloadManager.StartAsync();

                desktop.MainWindow = mainWindow;
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

        // Services
        services.AddSingleton<SoulseekAdapter>();
        services.AddSingleton<FileNameFormatter>();
        services.AddSingleton<ProtectedDataService>();

        // Spotify services
        services.AddSingleton<SpotifyInputSource>();
        services.AddSingleton<SpotifyScraperInputSource>();

        // Input parsers
        services.AddSingleton<CsvInputSource>();

        // Import Plugin System
        services.AddSingleton<ImportOrchestrator>();
        services.AddSingleton<IImportProvider, Services.ImportProviders.SpotifyImportProvider>();
        services.AddSingleton<IImportProvider, Services.ImportProviders.CsvImportProvider>();
        services.AddSingleton<IImportProvider, Services.ImportProviders.TracklistImportProvider>();

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

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<ImportPreviewViewModel>();
        services.AddSingleton<ImportHistoryViewModel>();
        services.AddSingleton<SpotifyImportViewModel>();

        // Utilities
        services.AddSingleton<SearchQueryNormalizer>();
    }
}
