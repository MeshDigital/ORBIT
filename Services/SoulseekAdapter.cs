using System.IO;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using Soulseek;
using System.Linq;
using System.Collections.Concurrent;

namespace SLSKDONET.Services;

/// <summary>
/// Real Soulseek.NET adapter for network interactions.
/// </summary>
public class SoulseekAdapter : ISoulseekAdapter, IDisposable
{
    private readonly ILogger<SoulseekAdapter> _logger;
    private readonly AppConfig _config;
    private readonly IEventBus _eventBus;
    private SoulseekClient? _client;

    public SoulseekAdapter(ILogger<SoulseekAdapter> logger, AppConfig config, IEventBus eventBus)
    {
        _logger = logger;
        _config = config;
        _eventBus = eventBus;
    }

    public async Task ConnectAsync(string? password = null, CancellationToken ct = default)
    {
        try
        {
            _client = new SoulseekClient();
            _logger.LogInformation("Connecting to Soulseek as {Username} on {Server}:{Port}...", 
                _config.Username, _config.SoulseekServer, _config.SoulseekPort);
            
            if (string.IsNullOrEmpty(_config.SoulseekServer))
            {
               _logger.LogWarning("SoulseekServer is null or empty! Fallback might occur.");
            }

            await _client.ConnectAsync(
                _config.SoulseekServer ?? "server.slsknet.org", 
                _config.SoulseekPort == 0 ? 2242 : _config.SoulseekPort, 
                _config.Username, 
                password, 
                ct);
            
            // Subscribe to state changes
            _client.StateChanged += (sender, args) =>
            {
                _logger.LogInformation("Soulseek state changed: {State} (was {PreviousState})", 
                    args.State, args.PreviousState);
                _eventBus.Publish(new SoulseekStateChangedEvent(
                    args.State.ToString(), 
                    args.State.HasFlag(SoulseekClientStates.Connected)));
            };
            
            _logger.LogInformation("Successfully connected to Soulseek as {Username}", _config.Username);
            _eventBus.Publish(new SoulseekConnectionStatusEvent("connected", _config.Username ?? "Unknown"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Soulseek: {Message}", ex.Message);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_client != null)
        {
            _client.Disconnect();
            await Task.CompletedTask;
            _logger.LogInformation("Disconnected from Soulseek");
        }
    }

    public void Disconnect()
    {
        if (_client != null)
        {
            _client.Disconnect();
            _logger.LogInformation("Disconnected from Soulseek");
        }
    }

    public async Task<int> SearchAsync(
        string query,
        IEnumerable<string>? formatFilter,
        (int? Min, int? Max) bitrateFilter,
        DownloadMode mode, // Add DownloadMode parameter
        Action<IEnumerable<Track>> onTracksFound,
        CancellationToken ct = default)
    {
        if (_client == null)
        {
            throw new InvalidOperationException("Not connected to Soulseek");
        }

        try
        {
            var minBitrateStr = bitrateFilter.Min?.ToString() ?? "0";
            var maxBitrateStr = bitrateFilter.Max?.ToString() ?? "unlimited";
            _logger.LogInformation("Search started for query {SearchQuery} with mode {SearchMode}, format filter {FormatFilter}, bitrate range {MinBitrate}-{MaxBitrate}",
                query, mode, formatFilter == null ? "NONE" : string.Join(", ", formatFilter), minBitrateStr, maxBitrateStr);
            
            var searchQuery = Soulseek.SearchQuery.FromText(query);
            var options = new SearchOptions(
                searchTimeout: 30000, // 30 seconds
                responseLimit: 1000,
                fileLimit: 10000
            );

            var resultCount = 0;
            var totalFilesReceived = 0;
            var filteredByFormat = 0;
            var filteredByBitrate = 0;
            var formatSet = formatFilter?.Select(f => f.ToLowerInvariant()).ToHashSet();
            
            // For Album mode, we'll collect directories to evaluate later.
            var directories = new ConcurrentDictionary<string, List<Soulseek.File>>();

            var searchResult = await _client!.SearchAsync(
                searchQuery,
                (response) =>
                {
                    _logger.LogDebug("Received response from {User} with {Count} files", response.Username, response.Files.Count());
                    
                    var foundTracksInResponse = new List<Track>();

                    // Process each search response
                    foreach (var file in response.Files)
                    {
                        if (mode == DownloadMode.Album)
                        {
                            var directoryName = Path.GetDirectoryName(file.Filename);
                            if (!string.IsNullOrEmpty(directoryName))
                            {
                                var key = $"{response.Username}@{directoryName}";
                                directories.AddOrUpdate(key, 
                                    _ => new List<Soulseek.File> { file }, 
                                    (_, list) => { list.Add(file); return list; });
                            }
                        }
                        else // Normal mode
                        {
                            totalFilesReceived++;
                            
                            // Apply format filter
                            var extension = Path.GetExtension(file.Filename)?.TrimStart('.').ToLowerInvariant();
                            if (formatSet != null && formatSet.Any() && !formatSet.Contains(extension ?? ""))
                            {
                                filteredByFormat++;
                                if (filteredByFormat <= 3) // Log first 3 filtered items
                                {
                                    _logger.LogInformation("[FILTER] Rejected by format: {File} (extension: {Ext}, allowed: {Formats})", file.Filename, extension, string.Join(", ", formatSet));
                                }
                                continue;
                            }

                            // Parse filename to extract artist, title, album
                            var track = ParseTrackFromFile(file, response);

                            // Apply bitrate filter
                            if (bitrateFilter.Min.HasValue && track.Bitrate < bitrateFilter.Min.Value)
                            {
                                filteredByBitrate++;
                                _logger.LogTrace("Filtered by bitrate (too low): {File} ({Bitrate} < {Min})", file.Filename, track.Bitrate, bitrateFilter.Min.Value);
                                continue;
                            }
                            if (bitrateFilter.Max.HasValue && track.Bitrate > bitrateFilter.Max.Value)
                            {
                                filteredByBitrate++;
                                _logger.LogTrace("Filtered by bitrate (too high): {File} ({Bitrate} > {Max})", file.Filename, track.Bitrate, bitrateFilter.Max.Value);
                                continue;
                            }

                            if (resultCount < 3) // Log first 3 accepted tracks
                            {
                                _logger.LogInformation("[ACCEPT] Track passed filters: {Artist} - {Title} ({Bitrate} kbps, {Ext})", track.Artist, track.Title, track.Bitrate, extension);
                            }
                            foundTracksInResponse.Add(track);
                            resultCount++;
                        }
                    }

                    if (foundTracksInResponse.Any())
                        onTracksFound(foundTracksInResponse);
                },
                options: options,
                cancellationToken: ct
            );

            if (mode == DownloadMode.Album)
            {
                _logger.LogInformation("Found {Count} potential album directories.", directories.Count);
                // TODO: In a future step, we would rank these directories and create album download jobs.
                // For now, we will just log them.
                resultCount = directories.Count;
            }

            _logger.LogInformation("Search completed: {ResultCount} results from {TotalFiles} files (filtered: {FormatFiltered} by format, {BitrateFiltered} by bitrate)",
                resultCount, totalFilesReceived, filteredByFormat, filteredByBitrate);
            
            return resultCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Search failed for query {SearchQuery} with mode {SearchMode}", query, mode);
            throw;
        }
    }

    /// <summary>
    /// Progressive search strategy: Tries multiple search queries with increasing leniency.
    /// 1. Strict: "Artist - Title" (exact match expected)
    /// 2. Relaxed: "Artist Title" (keyword-based)
    /// 3. Album: Album-based search (fallback)
    /// Returns results from the first successful strategy.
    /// </summary>
    public async Task<int> ProgressiveSearchAsync(
        string artist,
        string title,
        string? album,
        IEnumerable<string>? formatFilter,
        (int? Min, int? Max) bitrateFilter,
        Action<IEnumerable<Track>> onTracksFound,
        CancellationToken ct = default)
    {
        if (_client == null)
        {
            throw new InvalidOperationException("Not connected to Soulseek");
        }

        var maxAttempts = _config.MaxSearchAttempts;
        _logger.LogInformation("Starting progressive search: {Artist} - {Title} (album: {Album})", artist, title, album ?? "unknown");

        // Strategy 1: Strict search "Artist - Title"
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested)
                return 0;

            try
            {
                var strictQuery = $"{artist} - {title}";
                _logger.LogInformation("Attempt {Attempt}/{Max}: Strict search: {Query}", attempt, maxAttempts, strictQuery);
                
                var resultCount = await SearchAsync(
                    strictQuery,
                    formatFilter,
                    bitrateFilter,
                    DownloadMode.Normal,
                    onTracksFound,
                    ct);
                
                if (resultCount > 0)
                {
                    _logger.LogInformation("Progressive search succeeded with strict query after {Attempt} attempt(s)", attempt);
                    return resultCount;
                }

                if (attempt < maxAttempts)
                    await Task.Delay(500, ct); // Brief delay before retry
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Strict search attempt {Attempt} failed", attempt);
            }
        }

        // Strategy 2: Relaxed search "Artist Title"
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested)
                return 0;

            try
            {
                var relaxedQuery = $"{artist} {title}";
                _logger.LogInformation("Attempt {Attempt}/{Max}: Relaxed search: {Query}", attempt, maxAttempts, relaxedQuery);
                
                var resultCount = await SearchAsync(
                    relaxedQuery,
                    formatFilter,
                    bitrateFilter,
                    DownloadMode.Normal,
                    onTracksFound,
                    ct);
                
                if (resultCount > 0)
                {
                    _logger.LogInformation("Progressive search succeeded with relaxed query after {Attempt} attempt(s)", attempt);
                    return resultCount;
                }

                if (attempt < maxAttempts)
                    await Task.Delay(500, ct); // Brief delay before retry
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Relaxed search attempt {Attempt} failed", attempt);
            }
        }

        // Strategy 3: Album search (fallback)
        if (!string.IsNullOrEmpty(album))
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                if (ct.IsCancellationRequested)
                    return 0;

                try
                {
                    _logger.LogInformation("Attempt {Attempt}/{Max}: Album search: {Query}", attempt, maxAttempts, album);
                    
                    var resultCount = await SearchAsync(
                        album,
                        formatFilter,
                        bitrateFilter,
                        DownloadMode.Album,
                        onTracksFound,
                        ct);
                    
                    if (resultCount > 0)
                    {
                        _logger.LogInformation("Progressive search succeeded with album search after {Attempt} attempt(s)", attempt);
                        return resultCount;
                    }

                    if (attempt < maxAttempts)
                        await Task.Delay(500, ct); // Brief delay before retry
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Album search attempt {Attempt} failed", attempt);
                }
            }
        }

        _logger.LogWarning("Progressive search exhausted all strategies for {Artist} - {Title}", artist, title);
        return 0;
    }

    private Track ParseTrackFromFile(Soulseek.File file, Soulseek.SearchResponse response)
    {
        // Extract bitrate and length from file attributes
        var bitrateAttr = file.Attributes?.FirstOrDefault(a => a.Type == FileAttributeType.BitRate);
        var bitrate = bitrateAttr?.Value ?? 0;
        var lengthAttr = file.Attributes?.FirstOrDefault(a => a.Type == FileAttributeType.Length);
        var length = lengthAttr?.Value ?? 0;

        // Parse filename for artist/title (basic implementation)
        var filename = Path.GetFileNameWithoutExtension(file.Filename);
        var parts = filename.Split(new[] { '-', '_' }, 2);
        
        string artist = "Unknown Artist";
        string title = filename;
        string album = "";

        if (parts.Length >= 2)
        {
            artist = parts[0].Trim();
            title = parts[1].Trim();
        }

        // Try to extract album from path
        var pathParts = file.Filename.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (pathParts.Length > 1)
        {
            album = pathParts[^2]; // Second to last part is often the album
        }

        return new Track
        {
            Artist = artist,
            Title = title,
            Album = album,
            Filename = file.Filename,
            Directory = Path.GetDirectoryName(file.Filename), // Added for Album Grouping
            Username = response.Username,
            Bitrate = bitrate,
            Size = file.Size,
            Length = length,
            SoulseekFile = file, // CRITICAL: Store the original file object for downloads.
            
            // Intelligence Metrics
            HasFreeUploadSlot = response.HasFreeUploadSlot,
            QueueLength = response.QueueLength,
            UploadSpeed = response.UploadSpeed // Bytes/s
        };
    }

    public async Task<bool> DownloadAsync(
        string username,
        string filename,
        string outputPath,
        long? size = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (this._client == null)
        {
            throw new InvalidOperationException("Not connected to Soulseek");
        }

        try
        {
            this._logger.LogInformation("Downloading {Filename} from {Username} to {OutputPath}", filename, username, outputPath);
            
            // Check if already cancelled
            ct.ThrowIfCancellationRequested();

            var directory = Path.GetDirectoryName(outputPath);
            if (directory != null)
                System.IO.Directory.CreateDirectory(directory);

            // Track state for timeout logic
            DateTime lastActivity = DateTime.UtcNow;
            long lastBytes = 0;
            bool isQueued = false;

            var downloadOptions = new TransferOptions(
                stateChanged: (args) =>
                {
                    // Update queued status
                    if (args.Transfer.State.HasFlag(TransferStates.Queued))
                    {
                        isQueued = true;
                    }
                    else if (args.Transfer.State.HasFlag(TransferStates.InProgress))
                    {
                        isQueued = false;
                        
                        // Check for progress activity
                        if (args.Transfer.BytesTransferred > lastBytes)
                        {
                            lastBytes = args.Transfer.BytesTransferred;
                            lastActivity = DateTime.UtcNow;
                        }

                        if (size.HasValue && size.Value > 0)
                        {
                            double percentage = (double)args.Transfer.BytesTransferred / size.Value;
                            progress?.Report(percentage);
                        }
                    }
                });

            using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);
            
            // We wrap the Soulseek DownloadAsync in our own task to enforce our custom timeout logic
            // The underlying client has some timeout logic, but we want granular control over "Stalled vs Queued"
            var downloadTask = this._client.DownloadAsync(
                username,
                filename,
                () => Task.FromResult((Stream)fileStream),
                size,
                startOffset: 0,
                options: downloadOptions,
                cancellationToken: ct);

            // Monitoring Loop
            while (!downloadTask.IsCompleted)
            {
                // Check if we should time out
                if (!isQueued && (DateTime.UtcNow - lastActivity).TotalSeconds > 60)
                {
                    // STALLED: Not queued, but no bytes moved for 60s
                    throw new TimeoutException("Transfer stalled for 60 seconds (0 bytes received).");
                }
                
                // If we are queued, we WAIT INDEFINITELY (or until user cancels)
                // This is the key fix: Don't timeout if we are just waiting in line.

                await Task.WhenAny(downloadTask, Task.Delay(1000, ct));
            }

            await downloadTask; // Propagate exceptions/completion

            this._logger.LogInformation("Download completed: {Filename}", filename);
            progress?.Report(1.0);
            _eventBus.Publish(new TransferFinishedEvent(filename, username));
            return true;
        }
        catch (OperationCanceledException)
        {
            this._logger.LogWarning("Download cancelled: {Filename}", filename);
            _eventBus.Publish(new TransferCancelledEvent(filename, username));
            throw; 
        }
        catch (TimeoutException ex)
        {
            this._logger.LogWarning("Download timeout: {Filename} from {Username} - {Message}", filename, username, ex.Message);
            _eventBus.Publish(new TransferFailedEvent(filename, username, "Connection timeout"));
            return false;
        }
        catch (IOException ex)
        {
            this._logger.LogError(ex, "I/O error during download: {Filename} from {Username}", filename, username);
            _eventBus.Publish(new TransferFailedEvent(filename, username, "I/O error: " + ex.Message));
            return false;
        }
        catch (Exception ex) when (ex.Message.Contains("refused") || ex.Message.Contains("aborted") || ex.Message.Contains("Unable to read"))
        {
            this._logger.LogWarning("Network error during download: {Filename} from {Username} - {Message}", filename, username, ex.Message);
            _eventBus.Publish(new TransferFailedEvent(filename, username, "Connection failed"));
            return false;
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Download failed: {Message}", ex.Message);
            _eventBus.Publish(new TransferFailedEvent(filename, username, ex.Message));
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            _client?.Disconnect();
            _client?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing SoulseekClient");
        }
    }

    public bool IsConnected => _client?.State.HasFlag(SoulseekClientStates.Connected) == true;
}
