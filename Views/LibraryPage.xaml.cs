using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Views
{
    /// <summary>
    /// Interaction logic for LibraryPage.xaml
    /// </summary>
    public partial class LibraryPage : Page
    {
        public LibraryPage(LibraryViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            
            // Lazy-load projects when page is accessed
            Loaded += (s, e) =>
            {
                if (viewModel.AllProjects.Count == 0)
                {
                    _ = viewModel.LoadProjectsAsync();
                }
            };
        }



        private System.Windows.Point _dragStartPoint;
        private DragAdorner? _adorner;
        private AdornerLayer? _layer;

        private void DataGridRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(null);
            System.Diagnostics.Debug.WriteLine($"[DRAG] MouseDown at {_dragStartPoint}");
            Console.WriteLine($"[DRAG] MouseDown at {_dragStartPoint}"); // Also output to console
        }

        // Removed Redundant DataGridRow_PreviewMouseDown
        
        private void DataGridRow_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                System.Windows.Point mousePos = e.GetPosition(null);
                Vector diff = _dragStartPoint - mousePos;

                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    if (sender is DataGridRow row && row.DataContext is PlaylistTrackViewModel trackVm)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DRAG] Starting drag for: {trackVm.Artist} - {trackVm.Title}");
                        Console.WriteLine($"[DRAG] Starting drag for: {trackVm.Artist} - {trackVm.Title}");
                        
                        // Start Drag
                        _layer = AdornerLayer.GetAdornerLayer(row);
                        _adorner = new DragAdorner(row, row, 0.7);
                        if (_layer != null)
                        {
                            _layer.Add(_adorner);
                            System.Diagnostics.Debug.WriteLine("[DRAG] Adorner added to layer");
                            Console.WriteLine("[DRAG] Adorner added to layer");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("[DRAG] WARNING: Could not get AdornerLayer!");
                            Console.WriteLine("[DRAG] WARNING: Could not get AdornerLayer!");
                        }

                        try
                        {
                            var dragData = new System.Windows.DataObject(typeof(PlaylistTrackViewModel), trackVm);
                            System.Diagnostics.Debug.WriteLine("[DRAG] Calling DoDragDrop...");
                            Console.WriteLine("[DRAG] Calling DoDragDrop...");
                            var result = DragDrop.DoDragDrop(row, dragData, System.Windows.DragDropEffects.Move);
                            System.Diagnostics.Debug.WriteLine($"[DRAG] DoDragDrop completed with result: {result}");
                            Console.WriteLine($"[DRAG] DoDragDrop completed with result: {result}");
                        }
                        finally
                        {
                            // Cleanup Adorner
                            if (_layer != null && _adorner != null)
                            {
                                _layer.Remove(_adorner);
                                _adorner = null!;
                                _layer = null!;
                                System.Diagnostics.Debug.WriteLine("[DRAG] Adorner cleaned up");
                            }
                        }
                    }
                }
            }
        }

        private void DataGridRow_Drop(object sender, System.Windows.DragEventArgs e)
        {
             if (e.Data.GetDataPresent(typeof(PlaylistTrackViewModel)))
             {
                 var sourceVm = e.Data.GetData(typeof(PlaylistTrackViewModel)) as PlaylistTrackViewModel;
                 var targetRow = sender as DataGridRow;
                 var targetVm = targetRow?.DataContext as PlaylistTrackViewModel;
                 
                 if (sourceVm != null && targetVm != null && ReferenceEquals(sourceVm, targetVm) == false)
                 {
                     var vm = DataContext as LibraryViewModel;
                     vm?.ReorderTrack(sourceVm, targetVm); 
                 }
             }
        }

        private void OnPreviewDragOver(object sender, System.Windows.DragEventArgs e)
        {
            // e.Effects = DragDropEffects.Move; 
            e.Effects = System.Windows.DragDropEffects.Move;
            e.Handled = true;
        }
        
        private void Playlist_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(PlaylistTrackViewModel)))
            {
                 var sourceVm = e.Data.GetData(typeof(PlaylistTrackViewModel)) as PlaylistTrackViewModel;
                 var targetItem = sender as ListBoxItem; 
                 var targetPlaylist = targetItem?.DataContext as PlaylistJob;
                 
                 if (sourceVm != null && targetPlaylist != null)
                 {
                     var vm = DataContext as LibraryViewModel;
                     vm?.AddToPlaylist(targetPlaylist, sourceVm);
                 }
            }
        }

        private void DataGridRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[PLAYBACK] DataGridRow_MouseDoubleClick fired");
            Console.WriteLine("[PLAYBACK] DataGridRow_MouseDoubleClick fired");
            if (sender is DataGridRow row && row.DataContext is PlaylistTrackViewModel track)
            {
                System.Diagnostics.Debug.WriteLine($"[PLAYBACK] Playing: {track.Artist} - {track.Title}");
                Console.WriteLine($"[PLAYBACK] Playing: {track.Artist} - {track.Title}");
                var vm = DataContext as LibraryViewModel;
                vm?.PlayTrackCommand.Execute(track);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[PLAYBACK] WARNING: Sender is not DataGridRow or DataContext is not PlaylistTrackViewModel");
                Console.WriteLine("[PLAYBACK] WARNING: Sender is not DataGridRow or DataContext is not PlaylistTrackViewModel");
            }
        }
    }
}
