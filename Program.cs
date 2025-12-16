using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.ReactiveUI;
using Microsoft.Extensions.Configuration;
using Serilog;
using System;
using System.IO;

namespace SLSKDONET
{
    class Program
    {
        // Initialization code. Don't use any Avalonia or WPF types until Get
        // Instance() is called.
        [STAThread]
        public static void Main(string[] args)
        {
            // Build configuration for Serilog
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true)
                .Build();

            // Initialize Serilog
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .ReadFrom.Configuration(configuration)
                .CreateLogger();

            try
            {
                Log.Information("Starting QMUSICSLSK application");
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Application terminated unexpectedly");
                throw;
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace()
                .UseReactiveUI();
    }
}
