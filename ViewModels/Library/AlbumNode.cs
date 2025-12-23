using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Views; // For RelayCommand

namespace SLSKDONET.ViewModels.Library;

public class AlbumNode : ILibraryNode, INotifyPropertyChanged
{
    private readonly DownloadManager? _downloadManager;
    
    public string? AlbumTitle { get; set; }
    public string? Artist { get; set; }
    public string? Title => AlbumTitle;
    public string? Album => AlbumTitle;
    public string? Duration => string.Empty;
    public string? Bitrate => string.Empty;
    public string? Status => string.Empty;
    public int SortOrder => 0;
    public int Popularity => 0;
    public string? Genres => string.Empty;
    private string? _albumArtPath;
    public string? AlbumArtPath
    {
        get => _albumArtPath;
        set
        {
            if (_albumArtPath != value)
            {
                _albumArtPath = value;
                OnPropertyChanged();
            }
        }
    }

    public double Progress
    {
        get
        {
            if (Tracks == null || !Tracks.Any()) return 0;
            // Only count tracks that have started or are downloading
            var tracksWithProgress = Tracks.Where(t => t.Progress > 0).ToList();
            if (!tracksWithProgress.Any()) return 0;
            
            return tracksWithProgress.Average(t => t.Progress);
        }
    }

    public ObservableCollection<PlaylistTrackViewModel> Tracks { get; } = new();
    
    public ICommand DownloadAlbumCommand { get; }

    public AlbumNode(string? albumTitle, string? artist, DownloadManager? downloadManager = null)
    {
        AlbumTitle = albumTitle;
        Artist = artist;
        _downloadManager = downloadManager;
        
        DownloadAlbumCommand = new RelayCommand<object>(_ => DownloadAlbum());
        
        Tracks.CollectionChanged += (s, e) => {
            if (e.NewItems != null)
            {
                foreach (PlaylistTrackViewModel item in e.NewItems)
                    item.PropertyChanged += OnTrackPropertyChanged;
            }
            if (e.OldItems != null)
            {
                foreach (PlaylistTrackViewModel item in e.OldItems)
                    item.PropertyChanged -= OnTrackPropertyChanged;
            }
            OnPropertyChanged(nameof(Progress));
            UpdateAlbumArt();
        };
    }

    private void DownloadAlbum()
    {
        if (_downloadManager == null || !Tracks.Any()) return;
        
        var tracksToDownload = Tracks.Select(t => t.Model).ToList();
        _downloadManager.QueueTracks(tracksToDownload);
    }

    private void UpdateAlbumArt()
    {
        // Use the art from the first track that has it
        var art = Tracks.FirstOrDefault(t => !string.IsNullOrEmpty(t.AlbumArtPath))?.AlbumArtPath;
        if (art != AlbumArtPath)
        {
            AlbumArtPath = art;
        }
    }

    private void OnTrackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaylistTrackViewModel.Progress))
        {
            OnPropertyChanged(nameof(Progress));
        }
        else if (e.PropertyName == nameof(PlaylistTrackViewModel.AlbumArtPath))
        {
            UpdateAlbumArt();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
