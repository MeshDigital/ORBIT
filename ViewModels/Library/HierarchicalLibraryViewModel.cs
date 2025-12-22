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
    public ITreeDataGridRowSelectionModel<ILibraryNode>? Selection => Source.RowSelection;

    public HierarchicalLibraryViewModel()
    {
        Source = new HierarchicalTreeDataGridSource<ILibraryNode>(_albums);
        Source.RowSelection!.SingleSelect = false;

        Source.Columns.AddRange(new IColumn<ILibraryNode>[]
        {
                // Column #1: Album Art
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
                
                // Column #2: Status (PRIORITY - moved from position 10)
                new TemplateColumn<ILibraryNode>(
                    "Status",
                    new FuncDataTemplate<object>((item, _) => 
                    {
                         if (item is not PlaylistTrackViewModel track) return new Panel();

                        var border = new Border {
                            CornerRadius = new CornerRadius(4),
                            Padding = new Thickness(6, 3)
                        };

                        var textBlock = new TextBlock { 
                            FontSize = 10,
                            Foreground = Brushes.White
                        };

                        // Bind Background Color
                        border.Bind(Border.BackgroundProperty, new Binding(nameof(PlaylistTrackViewModel.StatusColor)) { Converter = new FuncValueConverter<string, IBrush>(c => Brush.Parse(c)) });
                        
                        // Bind Status Text (includes progress)
                        textBlock.Bind(TextBlock.TextProperty, new Binding(nameof(PlaylistTrackViewModel.StatusText)));

                        border.Child = textBlock;
                        return border;

                    }, false),
                    width: new GridLength(100)),
                
                // Column #3: Metadata Status (PRIORITY - moved from position 9)
                new TemplateColumn<ILibraryNode>(
                    " ✨",
                    new FuncDataTemplate<object>((item, _) => 
                    {
                         if (item is not PlaylistTrackViewModel track) return new Panel();

                        // Use Bindings for Dynamic Updates
                        var textBlock = new TextBlock { 
                            VerticalAlignment = VerticalAlignment.Center,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            FontSize = 14
                        };

                        textBlock.Bind(TextBlock.TextProperty, new Binding(nameof(PlaylistTrackViewModel.MetadataStatusSymbol)));
                        textBlock.Bind(TextBlock.ForegroundProperty, new Binding(nameof(PlaylistTrackViewModel.MetadataStatusColor)) { Converter = new FuncValueConverter<string, IBrush>(c => Brush.Parse(c)) });
                        textBlock.Bind(ToolTip.TipProperty, new Binding(nameof(PlaylistTrackViewModel.MetadataStatus)));

                        return textBlock;
                    }, false),
                    width: new GridLength(40)),
                    
                // Column #4: Title (Hierarchical Expander)
                new HierarchicalExpanderColumn<ILibraryNode>(
                    new TextColumn<ILibraryNode, string>("Title", x => x.Title ?? "Unknown"),
                    x => x is AlbumNode album ? album.Tracks : null),
                    
                // Column #5-11: Supporting Info
                new TextColumn<ILibraryNode, int>("#", x => x.SortOrder),
                new TextColumn<ILibraryNode, string>("Artist", x => x.Artist ?? string.Empty),
                new TextColumn<ILibraryNode, string>("Album", x => x.Album ?? string.Empty),
                new TextColumn<ILibraryNode, string>("Duration", x => x.Duration ?? string.Empty),
                new TextColumn<ILibraryNode, int>("🔥", x => x.Popularity),
                new TextColumn<ILibraryNode, string>("Bitrate", x => x.Bitrate ?? string.Empty),
                new TextColumn<ILibraryNode, string>("Genres", x => x.Genres ?? string.Empty),
                
                // Column #12: Actions
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

                        // 1. Search button (Missing/Failed) - Bind IsVisible to CanHardRetry (or logic)
                        var searchBtn = new Button {
                            Content = "🔍",
                            Command = track.FindNewVersionCommand,
                            Padding = new Thickness(6, 2),
                            FontSize = 11
                        };
                        ToolTip.SetTip(searchBtn, "Search for this track");
                        // Logic: Show if Pending or Failed. CanHardRetry covers Failed/Cancelled. Pending is separate.
                        // We will bind to a converter or add a specific property. For now, let's just make sure it updates.
                        // Actually, let's simplify: Bind IsVisible to a new property or existing one?
                        // Let's use MultiBinding or just basic binding if property exists. 
                        // To keep it simple, we assume the VM has exact properties. 
                        // CanHardRetry = Failed/Cancelled. 
                        // We also want it for Pending? Pending means "Missing" usually.
                        // Let's check VM: State == PlaylistTrackState.Pending is initial.
                        
                        // We'll bind directly to State with a Converter for maximum flexibility without adding 20 booleans.
                        var searchVisBinding = new Binding(nameof(PlaylistTrackViewModel.State))
                        {
                            Converter = new FuncValueConverter<PlaylistTrackState, bool>(s => 
                                s == PlaylistTrackState.Pending || s == PlaylistTrackState.Failed)
                        };
                        searchBtn.Bind(Button.IsVisibleProperty, searchVisBinding);
                        panel.Children.Add(searchBtn);

                        // 2. Pause button
                        var pauseBtn = new Button {
                            Content = "⏸",
                            Command = track.PauseCommand,
                            Padding = new Thickness(6, 2),
                            FontSize = 11
                        };
                        ToolTip.SetTip(pauseBtn, "Pause download");
                        pauseBtn.Bind(Button.IsVisibleProperty, new Binding(nameof(PlaylistTrackViewModel.CanPause)));
                        panel.Children.Add(pauseBtn);

                        // 3. Resume button
                        var resumeBtn = new Button {
                            Content = "▶",
                            Command = track.ResumeCommand,
                            Padding = new Thickness(6, 2),
                            FontSize = 11
                        };
                        ToolTip.SetTip(resumeBtn, "Resume download");
                        resumeBtn.Bind(Button.IsVisibleProperty, new Binding(nameof(PlaylistTrackViewModel.CanResume)));
                        panel.Children.Add(resumeBtn);

                        // 4. Cancel button
                        var cancelBtn = new Button {
                            Content = "✕",
                            Command = track.CancelCommand,
                            Padding = new Thickness(6, 2),
                            FontSize = 11,
                            Foreground = Brush.Parse("#F44336")
                        };
                        ToolTip.SetTip(cancelBtn, "Cancel");
                        cancelBtn.Bind(Button.IsVisibleProperty, new Binding(nameof(PlaylistTrackViewModel.CanCancel)));
                        panel.Children.Add(cancelBtn);

                        return panel;

                    }, false),
                    width: new GridLength(120)),
        });
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
