using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Diagnostics;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views.Avalonia
{
    public partial class SettingsPage : UserControl
    {
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
    }
}
