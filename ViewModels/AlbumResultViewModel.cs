using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Views; // Assuming RelayCommand is here or accessible via project usage

namespace SLSKDONET.ViewModels;

public class AlbumResultViewModel
{
    private readonly DownloadManager _downloadManager;
    private readonly AnalysisQueueService _analysisQueue;

    public string AlbumTitle { get; private set; } = string.Empty;
    public string Artist { get; private set; } = string.Empty;
    public string Username { get; private set; } = string.Empty;
    public string Directory { get; private set; } = string.Empty;
    public int TrackCount { get; private set; }
    public string QualitySummary { get; private set; } = string.Empty;
    public bool HasFreeSlot { get; private set; }
    public int UploadSpeed { get; private set; }
    public int QueueLength { get; private set; }
    public double TotalSizeMb { get; private set; }

    public ICommand DownloadAlbumCommand { get; }
    public ICommand ForceScanAlbumCommand { get; }

    public List<Track> Tracks { get; }

    public AlbumResultViewModel(List<Track> tracks, DownloadManager downloadManager, AnalysisQueueService analysisQueue)
    {
        Tracks = tracks;
        _downloadManager = downloadManager;
        _analysisQueue = analysisQueue;

        if (tracks.Any())
        {
            var first = tracks.First();
            // Use directory name as Album Title if Album metadata is missing? 
            // Better to prefer metadata if common, else directory name.
            // For now, let's try to find the most common non-empty Album tag
            var commonAlbum = tracks
                .GroupBy(t => t.Album)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault(g => !string.IsNullOrEmpty(g.Key))?.Key;

            Directory = first.Directory ?? string.Empty;
            AlbumTitle = !string.IsNullOrEmpty(commonAlbum) ? commonAlbum : System.IO.Path.GetFileName(Directory);
            Artist = tracks.GroupBy(t => t.Artist).OrderByDescending(g => g.Count()).First().Key ?? "Unknown";
            Username = first.Username ?? "Unknown";
            
            // Metrics
            HasFreeSlot = tracks.Any(t => t.HasFreeUploadSlot);
            UploadSpeed = tracks.Max(t => t.UploadSpeed); // Optimistic
            QueueLength = tracks.Min(t => t.QueueLength); // Optimistic
            
            TrackCount = tracks.Count;
            TotalSizeMb = tracks.Sum(t => t.Size ?? 0) / 1024d / 1024d;
            
            // Quality Summary (e.g. "320kbps MP3")
            var avgBitrate = (int)tracks.Average(t => t.Bitrate);
            var formats = tracks.Select(t => t.GetExtension()).Distinct();
            QualitySummary = $"{avgBitrate}kbps {string.Join("/", formats)}";
        }
        
        DownloadAlbumCommand = new RelayCommand(DownloadAlbum_Execute);
        ForceScanAlbumCommand = new RelayCommand(ForceScanAlbum_Execute);
    }

    private async void DownloadAlbum_Execute()
    {
        if (Tracks == null || !Tracks.Any()) return;
        
        // Create PlaylistJob for Library integration
        var job = new PlaylistJob
        {
            Id = System.Guid.NewGuid(),
            SourceTitle = $"{Artist} - {AlbumTitle}",
            SourceType = "Album",
            CreatedAt = System.DateTime.Now,
            DestinationFolder = ""
        };
        
        // Convert tracks to PlaylistTrack and add to job
        foreach(var t in Tracks)
        {
            var playlistTrack = new PlaylistTrack
            {
                Id = System.Guid.NewGuid(),
                PlaylistId = job.Id,
                Artist = t.Artist ?? "",
                Title = t.Title ?? "",
                Album = AlbumTitle ?? "",
                Status = TrackStatus.Missing,
                TrackUniqueHash = t.UniqueHash ?? "",
                ResolvedFilePath = string.Empty,
                AddedAt = System.DateTime.Now
            };
            
            job.PlaylistTracks.Add(playlistTrack);
            job.OriginalTracks.Add(t);
        }
        
        // Use new QueueProject overload - creates job in Library and queues tracks
        await _downloadManager.QueueProject(job);
    }

    private void ForceScanAlbum_Execute()
    {
        // Queue all tracks for audio analysis (waveform, BPM, key detection)
        // This will scan both downloaded and library tracks
        foreach (var track in Tracks)
        {
            if (!string.IsNullOrEmpty(track.Filename))
            {
                // Construct file path from directory + filename
                var filePath = System.IO.Path.Combine(Directory, track.Filename);
                
                // Only queue if file exists (downloaded or in library)
                if (System.IO.File.Exists(filePath))
                {
                    _analysisQueue.QueueAnalysis(filePath, track.UniqueHash ?? string.Empty);
                }
            }
        }
    }
}
