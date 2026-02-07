using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SLSKDONET;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia
{
    public partial class SettingsPage : UserControl
    {
        private DiagnosticsViewModel? _diagnosticsViewModel;
        private Window? _diagnosticsWindow;

        public SettingsPage()
        {
            InitializeComponent();
        }

        public SettingsPage(SettingsViewModel viewModel) : this()
        {
            DataContext = viewModel;

            // Phase 8: Wire up FFmpeg download button
            var downloadButton = this.FindControl<Button>("DownloadFfmpegButton");
            if (downloadButton != null)
            {
                downloadButton.Click += OnDownloadFfmpegClick;
            }

            // Wire up Diagnostics Panel button
            var diagnosticsButton = this.FindControl<Button>("OpenDiagnosticsPanelButton");
            if (diagnosticsButton != null)
            {
                diagnosticsButton.Click += OnOpenDiagnosticsClick;
            }
        }

        private void OnDownloadFfmpegClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                // Open browser to official FFmpeg download page
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://ffmpeg.org/download.html",
                    UseShellExecute = true
                });
            }
            catch (System.Exception ex)
            {
                // Log error (logger not available in code-behind, but graceful fallback)
                System.Diagnostics.Debug.WriteLine($"Failed to open FFmpeg download page: {ex.Message}");
            }
        }

        private async void OnOpenDiagnosticsClick(object? sender, RoutedEventArgs e)
        {
            try
            {
                // Get or create the DiagnosticsViewModel from DI
                var app = (App)global::Avalonia.Application.Current!;
                _diagnosticsViewModel ??= app.Services!.GetRequiredService<DiagnosticsViewModel>();

                // Create and show the Diagnostics Panel as a modal window
                if (_diagnosticsWindow == null || !_diagnosticsWindow.IsVisible)
                {
                    var panel = new DiagnosticsPanel
                    {
                        DataContext = _diagnosticsViewModel
                    };

                    _diagnosticsWindow = new Window
                    {
                        Title = "🔬 Diagnostics & Telemetry",
                        Content = panel,
                        Width = 600,
                        Height = 550,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        CanResize = false,
                        SystemDecorations = SystemDecorations.BorderOnly
                    };

                    // Get the parent window
                    var parentWindow = TopLevel.GetTopLevel(this) as Window;
                    if (parentWindow != null)
                    {
                        await _diagnosticsWindow.ShowDialog(parentWindow);
                    }
                    else
                    {
                        _diagnosticsWindow.Show();
                    }
                }
                else
                {
                    _diagnosticsWindow.Activate();
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to open Diagnostics panel: {ex.Message}");
            }
        }
    }
}
