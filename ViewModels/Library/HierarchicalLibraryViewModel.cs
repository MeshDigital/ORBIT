using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Selection;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels.Library;

public class HierarchicalLibraryViewModel
{
    private readonly ObservableCollection<AlbumNode> _albums = new();
    public HierarchicalTreeDataGridSource<ILibraryNode> Source { get; }

    public HierarchicalLibraryViewModel()
    {
        Source = new HierarchicalTreeDataGridSource<ILibraryNode>(_albums)
        {
            Columns =
            {
                new TemplateColumn<ILibraryNode>(
                    "🎨",
                    new FuncDataTemplate<object>((item, _) => 
                    {
                        if (item is not ILibraryNode node) return new Panel();

                        return new Border { 
                            Width = 32, Height = 32, CornerRadius = new CornerRadius(4), ClipToBounds = true,
                            Background = Brush.Parse("#2D2D2D"),
                            Margin = new Thickness(4),
                            Child = new Image { 
                                [!Image.SourceProperty] = new Binding(nameof(ILibraryNode.AlbumArtPath))
                                {
                                    Converter = new FuncValueConverter<string?, IImage?>(path => 
                                    {
                                        if (string.IsNullOrEmpty(path)) return null;
                                        try {
                                            if (System.IO.File.Exists(path)) return new Avalonia.Media.Imaging.Bitmap(path);
                                        } catch {} // Ignore errors
                                        return null;
                                    })
                                },
                                Stretch = Stretch.UniformToFill
                            }
                        };
                    }, false), // Disable recycling
                    width: new GridLength(40)),
                new HierarchicalExpanderColumn<ILibraryNode>(
                    new TextColumn<ILibraryNode, string>("Title", x => x.Title ?? "Unknown"),
                    x => x is AlbumNode album ? album.Tracks : null),
                new TextColumn<ILibraryNode, int>("#", x => x.SortOrder),
                new TextColumn<ILibraryNode, string>("Artist", x => x.Artist ?? string.Empty),
                new TextColumn<ILibraryNode, string>("Album", x => x.Album ?? string.Empty),
                new TextColumn<ILibraryNode, string>("Duration", x => x.Duration ?? string.Empty),
                new TextColumn<ILibraryNode, int>("🔥", x => x.Popularity),
                new TextColumn<ILibraryNode, string>("Bitrate", x => x.Bitrate ?? string.Empty),
                new TextColumn<ILibraryNode, string>("Genres", x => x.Genres ?? string.Empty),
                // Phase 1: Enhanced Status Column with Colored Badges
                new TemplateColumn<ILibraryNode>(
                    "Status",
                    new FuncDataTemplate<object>((item, _) => 
                    {
                         if (item is not PlaylistTrackViewModel track) return new Panel();

                        var stateColor = track.State switch
                        {
                            PlaylistTrackState.Completed => "#1DB954",  // Spotify green
                            PlaylistTrackState.Downloading => "#00A3FF", // Orbit blue
                            PlaylistTrackState.Searching => "#B388FF",   // Purple
                            PlaylistTrackState.Queued => "#FFA726",      // Orange
                            PlaylistTrackState.Failed => "#F44336",      // Red
                            PlaylistTrackState.Pending => "#757575",     // Gray
                            _ => "#666666"
                        };

                        var stateText = track.State switch
                        {
                            PlaylistTrackState.Completed => "✓ Ready",
                            PlaylistTrackState.Downloading => $"↓ {track.Progress:P0}",
                            PlaylistTrackState.Searching => "🔍 Search",
                            PlaylistTrackState.Queued => "⏳ Queued",
                            PlaylistTrackState.Failed => "✗ Failed",
                            PlaylistTrackState.Pending => "⊙ Missing",
                            _ => "?"
                        };

                        return new Border {
                            Background = Brush.Parse(stateColor),
                            CornerRadius = new CornerRadius(4),
                            Padding = new Thickness(6, 3),
                            Child = new TextBlock { 
                                Text = stateText,
                                FontSize = 10,
                                Foreground = Brushes.White
                            }
                        };

                    }, false),
                    width: new GridLength(100)),
                    
                // Phase 2: Actions Column with Inline Controls
                new TemplateColumn<ILibraryNode>(
                    "Actions",
                    new FuncDataTemplate<object>((item, _) => 
                    {
                         if (item is not PlaylistTrackViewModel track) return new Panel();

                        var panel = new StackPanel { 
                            Orientation = Orientation.Horizontal, 
                            Spacing = 4,
                            HorizontalAlignment = HorizontalAlignment.Center
                        };

                        // Search button (Missing/Failed)
                        if (track.State == PlaylistTrackState.Pending || track.State == PlaylistTrackState.Failed)
                        {
                            var searchBtn = new Button {
                                Content = "🔍",
                                Command = track.FindNewVersionCommand,
                                Padding = new Thickness(6, 2),
                                FontSize = 11
                            };
                            ToolTip.SetTip(searchBtn, "Search for this track");
                            panel.Children.Add(searchBtn);
                        }

                        // Pause button (Downloading/Queued/Searching)
                        if (track.State == PlaylistTrackState.Downloading || 
                            track.State == PlaylistTrackState.Queued ||
                            track.State == PlaylistTrackState.Searching)
                        {
                            var pauseBtn = new Button {
                                Content = "⏸",
                                Command = track.PauseCommand,
                                Padding = new Thickness(6, 2),
                                FontSize = 11
                            };
                            ToolTip.SetTip(pauseBtn, "Pause download");
                            panel.Children.Add(pauseBtn);
                        }

                        // Resume button (Paused)
                        if (track.State == PlaylistTrackState.Paused)
                        {
                            var resumeBtn = new Button {
                                Content = "▶",
                                Command = track.ResumeCommand,
                                Padding = new Thickness(6, 2),
                                FontSize = 11
                            };
                            ToolTip.SetTip(resumeBtn, "Resume download");
                            panel.Children.Add(resumeBtn);
                        }

                        // Cancel button (Active states)
                        if (track.State != PlaylistTrackState.Completed && 
                            track.State != PlaylistTrackState.Cancelled)
                        {
                            var cancelBtn = new Button {
                                Content = "✕",
                                Command = track.CancelCommand,
                                Padding = new Thickness(6, 2),
                                FontSize = 11,
                                Foreground = Brush.Parse("#F44336")
                            };
                            ToolTip.SetTip(cancelBtn, "Cancel");
                            panel.Children.Add(cancelBtn);
                        }

                        return panel;

                    }, false),
                    width: new GridLength(120)),
            }
        };
    }

    public void UpdateTracks(IEnumerable<PlaylistTrackViewModel> tracks)
    {
        _albums.Clear();
        var grouped = tracks.GroupBy(t => t.Model.Album ?? "Unknown Album");
        
        foreach (var group in grouped)
        {
            var firstTrack = group.First();
            var albumNode = new AlbumNode(group.Key, firstTrack.Artist)
            {
                AlbumArtPath = firstTrack.AlbumArtPath
            };
            foreach (var track in group)
            {
                albumNode.Tracks.Add(track);
            }
            _albums.Add(albumNode);
        }

        // Auto-expand all albums by default
        for (int i = 0; i < _albums.Count; i++)
        {
            Source.Expand(new IndexPath(i));
        }
    }
}
