using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SLSKDONET.Configuration;
using SLSKDONET.Views;
using System;

namespace SLSKDONET.Views.Avalonia
{
    public partial class MainWindow : Window
    {
        private DateTime _lastTypeTime = DateTime.MinValue;
        private string _typeToSelectBuffer = string.Empty;

        public MainWindow()
        {
            InitializeComponent();
#if DEBUG
            this.AttachDevTools();
#endif
            
            // Get config from DataContext (MainViewModel will set it)
            this.Opened += OnWindowOpened;
            this.Closing += OnWindowClosing;
            
            // Responsive layout: auto-collapse navigation on small screens
            this.PropertyChanged += OnWindowPropertyChanged;

            // Global Keyboard Shortcuts
            this.KeyDown += OnKeyDown;
            this.KeyUp += OnKeyUp;
        }

        private void OnKeyUp(object? sender, global::Avalonia.Input.KeyEventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                if (e.Key == global::Avalonia.Input.Key.LeftAlt || e.Key == global::Avalonia.Input.Key.RightAlt)
                {
                    vm.IsAltPressed = false;
                }
            }
        }

        private void OnKeyDown(object? sender, global::Avalonia.Input.KeyEventArgs e)
        {
            // Ignore if a TextBox is focused/active source to prevention typing interference
            if (e.Source is TextBox || e.Source is AutoCompleteBox) return;

            if (DataContext is MainViewModel vm)
            {
                switch (e.Key)
                {
                    case global::Avalonia.Input.Key.LeftAlt:
                    case global::Avalonia.Input.Key.RightAlt:
                        vm.IsAltPressed = true;
                        break;

                    case global::Avalonia.Input.Key.LeftShift:
                    case global::Avalonia.Input.Key.RightShift:
                        // ... existing Shift logic ...
                        break;

                    case global::Avalonia.Input.Key.Enter:
                        if (e.KeyModifiers == global::Avalonia.Input.KeyModifiers.Shift)
                        {
                            vm.QueueToTopCommand?.Execute(null);
                            e.Handled = true;
                        }
                        break;
                        
                    case global::Avalonia.Input.Key.Space:
                        // ... existing Space logic ...
                        break;
                        
                    case global::Avalonia.Input.Key.Left:
                    case global::Avalonia.Input.Key.Right:
                        // ... existing Arrow logic ...
                        break;

                    default:
                        // Alphanumeric search-to-select in grid
                        if (e.KeyModifiers == global::Avalonia.Input.KeyModifiers.None && 
                            ((e.Key >= global::Avalonia.Input.Key.A && e.Key <= global::Avalonia.Input.Key.Z) || 
                             (e.Key >= global::Avalonia.Input.Key.D0 && e.Key <= global::Avalonia.Input.Key.D9)))
                        {
                            var nowType = DateTime.UtcNow;
                            if ((nowType - _lastTypeTime).TotalMilliseconds > 1000) _typeToSelectBuffer = "";
                            _lastTypeTime = nowType;
                            
                            var keyStr = e.Key.ToString().Replace("D", "");
                            if (keyStr.Length > 1) keyStr = keyStr.Last().ToString(); // Handle named keys if any
                            _typeToSelectBuffer += keyStr;
                            
                            vm.LibraryViewModel?.JumpToTrack(_typeToSelectBuffer);
                            e.Handled = true;
                        }
                        break;
                }
            }
        }

        private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            // Listen for Bounds changes to detect window resize
            if (e.Property == BoundsProperty && DataContext is MainViewModel vm)
            {
                var width = Bounds.Width;
                
                // Auto-collapse navigation below 800px
                if (width < 800 && !vm.IsNavigationCollapsed)
                {
                    vm.IsNavigationCollapsed = true;
                }
                // Auto-expand above 1200px
                else if (width >= 1200 && vm.IsNavigationCollapsed)
                {
                    vm.IsNavigationCollapsed = false;
                }
            }
        }

        // Tray Icon Event Handlers
        private void ShowWindow_Click(object? sender, EventArgs e)
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void HideWindow_Click(object? sender, EventArgs e)
        {
            Hide();
        }

        private void Exit_Click(object? sender, EventArgs e)
        {
            Close();
        }

        private void OnWindowOpened(object? sender, EventArgs e)
        {
            // Try to get config from app services
            if (App.Current is App app && app.Services != null)
            {
                var config = app.Services.GetService(typeof(AppConfig)) as AppConfig;
                var configManager = app.Services.GetService(typeof(ConfigManager)) as ConfigManager;
                
                if (config != null)
                {
                    // Restore window state
                    if (!double.IsNaN(config.WindowWidth) && config.WindowWidth > 0)
                        Width = config.WindowWidth;
                    
                    if (!double.IsNaN(config.WindowHeight) && config.WindowHeight > 0)
                        Height = config.WindowHeight;
                    
                    if (!double.IsNaN(config.WindowX) && !double.IsNaN(config.WindowY))
                    {
                        Position = new PixelPoint((int)config.WindowX, (int)config.WindowY);
                    }
                    
                    if (config.WindowMaximized)
                    {
                        WindowState = WindowState.Maximized;
                    }
                }
            }
        }

        private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // Save window state
            if (App.Current is App app && app.Services != null)
            {
                var config = app.Services.GetService(typeof(AppConfig)) as AppConfig;
                var configManager = app.Services.GetService(typeof(ConfigManager)) as ConfigManager;
                
                if (config != null && configManager != null)
                {
                    config.WindowWidth = Width;
                    config.WindowHeight = Height;
                    config.WindowX = Position.X;
                    config.WindowY = Position.Y;
                    config.WindowMaximized = WindowState == WindowState.Maximized;
                    
                    configManager.Save(config);
                }
            }
        }
    }
}
