using System.IO;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services.InputParsers;
using SLSKDONET.Utils;

namespace SLSKDONET.Services;

/// <summary>
/// Manages download jobs and orchestrates the download process.
/// </summary>
public class DownloadManager : INotifyPropertyChanged, IDisposable
{
    private readonly ILogger<DownloadManager> _logger;
    private readonly AppConfig _config;
    private readonly SoulseekAdapter _soulseek;
    private readonly FileNameFormatter _fileNameFormatter;
    private readonly ILibraryService _libraryService;
    private readonly ITaggerService _taggerService;
    private readonly SpotifyScraperInputSource _spotifyScraperInputSource;
    private readonly CsvInputSource _csvInputSource;
    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
    private readonly SemaphoreSlim _concurrencySemaphore;
    private readonly Channel<DownloadJob> _jobChannel;
    private CancellationTokenSource _cts = new();
    private readonly List<Task> _runningTasks = new();
    private PlaylistJob? _currentPlaylistJob;

    public event EventHandler<DownloadJob>? JobUpdated;
    public event EventHandler<DownloadJob>? JobCompleted;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Collection of imported playlists and their download status.
    /// </summary>
    public ObservableCollection<PlaylistJob> ImportedPlaylists { get; } = new();

    public DownloadManager(
        ILogger<DownloadManager> logger,
        AppConfig config,
        SoulseekAdapter soulseek,
        FileNameFormatter fileNameFormatter,
        ILibraryService libraryService,
        ITaggerService taggerService,
        SpotifyScraperInputSource spotifyScraperInputSource,
        CsvInputSource csvInputSource)
    {
        _logger = logger;
        _config = config;
        _soulseek = soulseek;
        _fileNameFormatter = fileNameFormatter;
        _libraryService = libraryService;
        _taggerService = taggerService;
        _spotifyScraperInputSource = spotifyScraperInputSource;
        _csvInputSource = csvInputSource;
        _concurrencySemaphore = new SemaphoreSlim(_config.MaxConcurrentDownloads);
        _jobChannel = Channel.CreateUnbounded<DownloadJob>();
    }

    /// <summary>
    /// Starts a playlist/source import job with deduplication.
    /// Fetches tracks from the source, deduplicates against library, and creates SearchQueries for missing tracks.
    /// </summary>
    public async Task StartPlaylistDownloadAsync(string inputUrl, string destinationFolder, InputSourceType sourceType)
    {
        try
        {
            _logger.LogInformation("Starting playlist download: {URL} to {Folder}", inputUrl, destinationFolder);

            // Fetch tracks from the source
            List<Track> sourceTracks = new();
            string playlistName = "Import";

            if (sourceType == InputSourceType.Spotify)
            {
                try
                {
                    _logger.LogInformation("Fetching tracks from Spotify: {Url}", inputUrl);

                    _logger.LogDebug("Attempting to scrape Spotify content (public access)");
                    var queries = await _spotifyScraperInputSource.ParseAsync(inputUrl);
                    
                    if (!queries.Any())
                    {
                        _logger.LogWarning("Spotify scraping returned no tracks for URL: {Url}", inputUrl);
                        throw new InvalidOperationException("No tracks found in the Spotify source. The playlist might be private, empty, or the page structure may have changed.");
                    }
                    
                    sourceTracks = queries.Select(q => new Track
                    {
                        Artist = q.Artist,
                        Title = q.Title,
                        Album = q.Album,
                        SourceTitle = q.SourceTitle
                    }).ToList();
                    playlistName = queries.FirstOrDefault()?.SourceTitle ?? "Spotify Playlist";
                    
                    _logger.LogInformation("Successfully imported {Count} tracks from Spotify", sourceTracks.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to fetch Spotify tracks via public scraping");
                    throw new InvalidOperationException($"Spotify scraping error: {ex.Message}");
                }
            }
            else if (sourceType == InputSourceType.CSV)
            {
                var queries = await _csvInputSource.ParseAsync(inputUrl);
                sourceTracks = queries.Select(q => new Track
                {
                    Artist = q.Artist,
                    Title = q.Title,
                    Album = q.Album,
                    SourceTitle = q.SourceTitle
                }).ToList();
                playlistName = Path.GetFileNameWithoutExtension(inputUrl);
            }

            if (!sourceTracks.Any())
                throw new InvalidOperationException("No tracks found in source");

            // Create the playlist job
            var playlistJob = new PlaylistJob
            {
                SourceTitle = playlistName,
                SourceType = sourceType.ToString(),
                DestinationFolder = destinationFolder,
                OriginalTracks = new ObservableCollection<Track>(sourceTracks)
            };

            _currentPlaylistJob = playlistJob;

            // Load existing library for deduplication
            var libraryEntries = _libraryService.LoadDownloadedTracks();
            var libraryHashToPath = libraryEntries.ToDictionary(e => e.UniqueHash, e => e.FilePath);

            // Create PlaylistTracks with deduplication logic
            var playlistTracks = new List<PlaylistTrack>();
            int trackNumber = 1;
            
            foreach (var track in sourceTracks)
            {
                var hash = track.UniqueHash;
                PlaylistTrack playlistTrack;
                
                if (libraryHashToPath.TryGetValue(hash, out var libFilePath))
                {
                    // Track already in library - status = Downloaded
                    track.FilePath = libFilePath;
                    track.LocalPath = libFilePath;
                    playlistTrack = new PlaylistTrack
                    {
                        Id = Guid.NewGuid(),
                        PlaylistId = playlistJob.Id,
                        TrackUniqueHash = hash,
                        Artist = track.Artist ?? "Unknown",
                        Title = track.Title ?? "Unknown",
                        Album = track.Album ?? "Unknown",
                        Status = TrackStatus.Downloaded,
                        ResolvedFilePath = libFilePath,
                        TrackNumber = trackNumber
                    };
                    _logger.LogDebug("Track already in library: {Hash} at {Path}", hash, track.FilePath);
                }
                else
                {
                    // Track is missing - calculate expected path, status = Missing
                    var expectedFilename = FormatFilename(track);
                    var expectedPath = Path.Combine(destinationFolder, expectedFilename);
                    track.FilePath = expectedPath;
                    playlistTrack = new PlaylistTrack
                    {
                        Id = Guid.NewGuid(),
                        PlaylistId = playlistJob.Id,
                        TrackUniqueHash = hash,
                        Artist = track.Artist ?? "Unknown",
                        Title = track.Title ?? "Unknown",
                        Album = track.Album ?? "Unknown",
                        Status = TrackStatus.Missing,
                        ResolvedFilePath = expectedPath,
                        TrackNumber = trackNumber
                    };
                    _logger.LogDebug("Track missing, will be downloaded to: {Path}", expectedPath);
                }
                
                playlistTracks.Add(playlistTrack);
                trackNumber++;
            }

            // Save all playlist tracks to persistent storage
            await _libraryService.SavePlaylistTracksAsync(playlistTracks);
            playlistJob.PlaylistTracks = playlistTracks;
            playlistJob.RefreshStatusCounts();

            // Save the job for persistence
            await _libraryService.SavePlaylistJobAsync(playlistJob);

            // Add to imported playlists collection
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                ImportedPlaylists.Add(playlistJob);
            });

            _logger.LogInformation("Playlist job created: {SourceTitle}, {Total} tracks, {Missing} missing",
                playlistJob.SourceTitle, playlistJob.TotalTracks, playlistJob.MissingCount);

            OnPropertyChanged(nameof(ImportedPlaylists));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start playlist download");
            throw;
        }
    }

    /// <summary>
    /// Enqueues a track for download.
    /// </summary>
    public DownloadJob EnqueueDownload(Track track, string? outputPath = null, string? sourceTitle = null)
    {
        var job = new DownloadJob
        {
            Track = track,
            OutputPath = outputPath ?? Path.Combine(
                _config.DownloadDirectory!, // App.xaml.cs ensures this is not null
                FormatFilename(track)
            ),
            DestinationPath = outputPath ?? Path.Combine(
                _config.DownloadDirectory!,
                FormatFilename(track)
            ),
            SourceTitle = sourceTitle
        };

        _jobs.TryAdd(job.Id, job);
        job.PropertyChanged += Job_PropertyChanged;
        _logger.LogInformation("Enqueued download: {TrackId}", job.Id);
        OnPropertyChanged(nameof(SuccessfulCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(TodoCount));
        
        // Post the job to the channel for processing
        _jobChannel.Writer.TryWrite(job);

        return job;
    }

    /// <summary>
    /// Requeues an existing job (used for resume after cancel/fail).
    /// </summary>
    public void RequeueJob(DownloadJob job)
    {
        if (job.State != DownloadState.Cancelled && job.State != DownloadState.Failed)
        {
            _logger.LogWarning("Job {JobId} is not cancelled/failed; skipping requeue", job.Id);
            return;
        }

        job.ResetCancellationToken();
        job.RetryCount = 0;
        job.ErrorMessage = null;
        job.Progress = 0;
        job.BytesDownloaded = 0;
        job.StartedAt = null;
        job.CompletedAt = null;
        job.State = DownloadState.Pending;
        JobUpdated?.Invoke(this, job);

        _logger.LogInformation("Requeued job: {JobId}", job.Id);
        _jobChannel.Writer.TryWrite(job);
    }

    /// <summary>
    /// Starts the long-running task that processes jobs from the channel.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _logger.LogInformation("Download manager started. Waiting for jobs...");

        try
        {
            // Continuously read from the channel until it's completed.
            await foreach (var job in _jobChannel.Reader.ReadAllAsync(_cts.Token))
            {
                // Start the job and track it to prevent unobserved exceptions
                var task = ProcessJobAsync(job, _cts.Token);
                lock (_runningTasks)
                {
                    _runningTasks.Add(task);
                    // Clean up completed tasks
                    _runningTasks.RemoveAll(t => t.IsCompleted);
                }
                // Observe the task to prevent unobserved exceptions and mark observed
                _ = task.ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                    {
                        _logger.LogError(t.Exception, "Unhandled exception in ProcessJobAsync");
                        var _ = t.Exception.Flatten(); // mark observed
                    }
                }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Download manager processing loop cancelled.");
        }
        _logger.LogInformation("Download manager stopped.");
    }

    /// <summary>
    /// Processes a single download job.
    /// </summary>
    private async Task ProcessJobAsync(DownloadJob job, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, job.CancellationTokenSource.Token);
        var effectiveCt = linkedCts.Token;

        await _concurrencySemaphore.WaitAsync(effectiveCt);
        try
        {
            // Attempt download with retry logic
            var downloaded = false;
            while (!downloaded && job.RetryCount <= _config.MaxDownloadRetries)
            {
                var progress = new Progress<double>(p =>
                {
                    job.Progress = p;
                    job.BytesDownloaded = (long?)((job.Track.Size ?? 0) * p);
                    JobUpdated?.Invoke(this, job);
                });

                job.State = job.RetryCount > 0 ? DownloadState.Retrying : DownloadState.Downloading;
                job.StartedAt ??= DateTime.UtcNow;
                job.LastAttemptTime = DateTime.UtcNow;
                JobUpdated?.Invoke(this, job);

                var success = await _soulseek.DownloadAsync(
                    job.Track.Username!,
                    job.Track.Filename!,
                    job.OutputPath!,
                    job.Track.Size,
                    progress,
                    effectiveCt
                );

                if (success)
                {
                    downloaded = true;
                    job.Progress = 1.0;
                    job.Track.LocalPath = job.OutputPath; // persist local path for history
                    job.State = DownloadState.Completed;
                    job.CompletedAt = DateTime.UtcNow;
                    _logger.LogInformation("Download completed successfully: {Artist} - {Title}",
                        job.Track.Artist, job.Track.Title);
                }
                else if (job.RetryCount < _config.MaxDownloadRetries && _config.AutoRetryFailedDownloads)
                {
                    job.RetryCount++;
                    job.ErrorMessage = $"Attempt {job.RetryCount} failed, retrying...";
                    _logger.LogWarning("Download failed, will retry: {Artist} - {Title} (retry {Attempt}/{Max})",
                        job.Track.Artist, job.Track.Title, job.RetryCount, _config.MaxDownloadRetries);
                    await Task.Delay(1000, effectiveCt); // Brief delay before retry
                }
                else
                {
                    job.State = DownloadState.Failed;
                    job.ErrorMessage = $"Download failed after {job.RetryCount + 1} attempt(s)";
                    job.CompletedAt = DateTime.UtcNow;
                    _logger.LogError("Download failed and no more retries available: {Artist} - {Title}",
                        job.Track.Artist, job.Track.Title);
                    break;
                }
            }

            // Post-processing on success (tagging)
            if (downloaded)
            {
                try
                {
                    if (!string.IsNullOrEmpty(job.OutputPath))
                    {
                        _logger.LogDebug("Tagging downloaded file: {Path}", job.OutputPath);
                        var taggingSuccess = await _taggerService.TagFileAsync(job.Track, job.OutputPath);
                        if (taggingSuccess)
                        {
                            _logger.LogInformation("File tagged successfully: {Artist} - {Title}",
                                job.Track.Artist ?? "Unknown", job.Track.Title ?? "Unknown");
                        }
                        else
                        {
                            _logger.LogWarning("Failed to tag file: {Path}", job.OutputPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error while tagging file: {Path}", job.OutputPath);
                    // Don't fail the entire job if tagging fails - it's a post-processing step
                }
            }

            _logger.LogInformation("Job completed: {JobId} - {State}", job.Id, job.State);
        }
        catch (OperationCanceledException)
        {
            job.State = DownloadState.Cancelled;
            job.CompletedAt = DateTime.UtcNow;
            _logger.LogWarning("Job cancelled: {JobId}", job.Id);
        }
        catch (Exception ex)
        {
            job.State = DownloadState.Failed;
            job.ErrorMessage = ex.Message;
            job.CompletedAt = DateTime.UtcNow;
            _logger.LogError(ex, "Job error: {JobId}", job.Id);
        }
        finally
        {
            JobCompleted?.Invoke(this, job);
            _concurrencySemaphore.Release();
            OnPropertyChanged(nameof(SuccessfulCount));
            OnPropertyChanged(nameof(FailedCount));
            OnPropertyChanged(nameof(TodoCount));
        }
    }

    /// <summary>
    /// Cancels all pending downloads.
    /// </summary>
    public void CancelAll()
    {
        _logger.LogInformation("Cancelling all downloads");
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }
        // Complete the channel to stop the processing loop.
        _jobChannel.Writer.TryComplete();
    }

    /// <summary>
    /// Gets all download jobs.
    /// </summary>
    public IEnumerable<DownloadJob> GetJobs() => _jobs.Values;

    /// <summary>
    /// Gets a specific download job.
    /// </summary>
    public DownloadJob? GetJob(string jobId)
    {
        _jobs.TryGetValue(jobId, out var job);
        return job;
    }

    /// <summary>
    /// Formats a track filename using the configured name format.
    /// </summary>
    private string FormatFilename(Track track)
    {
        var template = _config.NameFormat ?? "{artist} - {title}";
        var filename = _fileNameFormatter.Format(template, track);

        var ext = track.GetExtension();
        return string.IsNullOrEmpty(ext) ? filename : $"{filename}.{ext}";
    }

    private void Job_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadJob.State) || e.PropertyName == nameof(DownloadJob.Status))
        {
            OnPropertyChanged(nameof(SuccessfulCount));
            OnPropertyChanged(nameof(FailedCount));
            OnPropertyChanged(nameof(TodoCount));
        }
    }

    public int SuccessfulCount => _jobs.Values.Count(j => j.State == DownloadState.Completed);
    public int FailedCount => _jobs.Values.Count(j => j.State == DownloadState.Failed || j.State == DownloadState.Cancelled);
    public int TodoCount => _jobs.Values.Count(j => j.State == DownloadState.Pending || j.State == DownloadState.Downloading || j.State == DownloadState.Searching);

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _jobChannel.Writer.TryComplete();
        _concurrencySemaphore?.Dispose();
    }
}
