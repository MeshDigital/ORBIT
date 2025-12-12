using System.Windows;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Services.InputParsers;
using SLSKDONET.Services;
using SLSKDONET.Views;

namespace SLSKDONET;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    public IServiceProvider Services { get; }

    public App()
    {
        Services = ConfigureServices();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Register global exception handlers to surface runtime errors during startup.
        AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
        {
            try
            {
                var ex = ev.ExceptionObject as Exception;
                System.Windows.MessageBox.Show($"Unhandled exception: {ex?.Message}\n\n{ex?.StackTrace}", "Unhandled Exception", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            catch { }
        };

        TaskScheduler.UnobservedTaskException += (s, ev) =>
        {
            try
            {
                // Check if it's a network-related error that we can safely ignore
                var isNetworkError = ev.Exception.InnerExceptions.Any(e => 
                    e.Message.Contains("refused") || 
                    e.Message.Contains("aborted") || 
                    e.Message.Contains("Unable to read") ||
                    e.Message.Contains("transport connection"));
                
                if (isNetworkError)
                {
                    // Log network errors but don't show dialog
                    System.Diagnostics.Debug.WriteLine($"Network error (handled): {ev.Exception.Message}");
                }
                else
                {
                    // Log instead of showing dialog to avoid crash loops
                    System.Diagnostics.Debug.WriteLine($"Unobserved task exception: {ev.Exception}");
                }
                ev.SetObserved();
            }
            catch { }
        };

        this.DispatcherUnhandledException += (s, ev) =>
        {
            try
            {
                System.Windows.MessageBox.Show($"Dispatcher exception: {ev.Exception.Message}\n\n{ev.Exception.StackTrace}", "Dispatcher Exception", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                ev.Handled = true;
            }
            catch { }
        };

        base.OnStartup(e);

        try
        {
            // Initialize database before anything else
            var databaseService = Services.GetRequiredService<DatabaseService>();
            databaseService.InitAsync().GetAwaiter().GetResult();
            
            var mainWindow = Services.GetRequiredService<MainWindow>();
            
            // Eagerly instantiate LibraryViewModel so it subscribes to ProjectAdded events
            // during orchestration, before user navigates to Library page
            _ = Services.GetRequiredService<SLSKDONET.ViewModels.LibraryViewModel>();
            
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Startup failed: {ex.Message}\n\n{ex.StackTrace}", "Startup Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            throw;
        }
    }

    /// <summary>
    /// Gets the current <see cref="App"/> instance in use
    /// </summary>
    public new static App Current => (App)System.Windows.Application.Current;
    
    private static IServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        return services.BuildServiceProvider();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Logging
        services.AddLogging(config =>
        {
            config.ClearProviders();
            config.AddConsole();
            config.SetMinimumLevel(LogLevel.Information);
        });

        // Configuration
        // Register ConfigManager as a singleton service.
        services.AddSingleton<ConfigManager>();
        // Register AppConfig using a factory that resolves ConfigManager from the container.
        // This ensures the configuration is loaded and processed correctly within the DI ecosystem.
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

        // Register Spotify services (API + public scraping fallback)
        services.AddSingleton<SpotifyInputSource>();
        services.AddSingleton<SpotifyScraperInputSource>();

        // Input parsers
        services.AddSingleton<CsvInputSource>();
        
        // Download logging and library management
        services.AddSingleton<DownloadLogService>();
        services.AddSingleton<LibraryService>();
        services.AddSingleton<ILibraryService>(provider => provider.GetRequiredService<LibraryService>());
        
        // Metadata and tagging service
        services.AddSingleton<ITaggerService, MetadataTaggerService>();
        
        // Rekordbox export service
        services.AddSingleton<RekordboxXmlExporter>();

        // Download manager (depends on ILibraryService, SpotifyInputSource, CsvInputSource)
        services.AddSingleton<DownloadManager>();

        // Register navigation and UI services used by the app.
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<SLSKDONET.Views.INotificationService, NotificationServiceAdapter>();
        services.AddSingleton<IUserInputService, UserInputService>();

        // Views (add logger for MainWindow)
        services.AddSingleton<MainWindow>();
        services.AddSingleton<DatabaseService>();
        services.AddSingleton<IMetadataService, MetadataService>();
        services.AddSingleton<MainViewModel>(); // MainViewModel can be a singleton as it represents the app's main state.

        // Pages for navigation
        services.AddSingleton<SLSKDONET.ViewModels.LibraryViewModel>();
        services.AddSingleton<ImportPreviewViewModel>(); // Make the preview VM a singleton to hold state during navigation
        services.AddTransient<SearchPage>();
        services.AddTransient<DownloadsPage>();
        services.AddTransient<LibraryPage>();
        services.AddTransient<SettingsPage>();
        services.AddTransient<ImportPreviewPage>();


        // Utilities can be registered as singletons as they are stateless.
        services.AddSingleton<SearchQueryNormalizer>();
    }
}
