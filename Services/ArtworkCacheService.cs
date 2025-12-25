using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services;

/// <summary>
/// Service for downloading and caching album artwork locally.
/// Downloads images from Spotify URLs and stores them in %AppData%/SLSKDONET/artwork/
/// </summary>
public class ArtworkCacheService
{
    private readonly ILogger<ArtworkCacheService> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _cacheDirectory;
    private readonly string _placeholderPath;

    private readonly SLSKDONET.Services.IO.IFileWriteService _fileWriteService;

    public ArtworkCacheService(
        ILogger<ArtworkCacheService> logger,
        SLSKDONET.Services.IO.IFileWriteService fileWriteService)
    {
        _logger = logger;
        _fileWriteService = fileWriteService;
        _httpClient = new HttpClient();
        
        // Set cache directory to %AppData%/SLSKDONET/artwork/
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _cacheDirectory = Path.Combine(appData, "SLSKDONET", "artwork");
        Directory.CreateDirectory(_cacheDirectory);
        
        // Placeholder path for missing artwork
        _placeholderPath = Path.Combine(_cacheDirectory, "placeholder.png");
        
        _logger.LogInformation("Artwork cache initialized at: {CacheDirectory}", _cacheDirectory);
    }

    /// <summary>
    /// Gets the local file path for album artwork, downloading it if necessary.
    /// Supports an optional requestedSize for micro-thumbnails (e.g., 64).
    /// </summary>
    public async Task<string> GetArtworkPathAsync(string? albumArtUrl, string? spotifyAlbumId, int? requestedSize = null)
    {
        // If no URL or ID provided, return placeholder
        if (string.IsNullOrWhiteSpace(albumArtUrl) || string.IsNullOrWhiteSpace(spotifyAlbumId))
        {
            return await GetPlaceholderPathAsync();
        }

        try
        {
            // Generate cache file path
            var suffix = requestedSize.HasValue ? $"_{requestedSize.Value}" : "";
            var cacheFileName = $"{spotifyAlbumId}{suffix}.jpg";
            var cachePath = Path.Combine(_cacheDirectory, cacheFileName);

            // If already cached, return immediately
            if (File.Exists(cachePath))
            {
                return cachePath;
            }

            // For resized versions, check if the original exists first
            var originalFileName = $"{spotifyAlbumId}.jpg";
            var originalPath = Path.Combine(_cacheDirectory, originalFileName);

            if (!File.Exists(originalPath))
            {
                // Download original artwork atomically
                _logger.LogInformation("Downloading original artwork for album {AlbumId} from {Url}", spotifyAlbumId, albumArtUrl);
                var imageBytes = await _httpClient.GetByteArrayAsync(albumArtUrl);
                
                if (imageBytes != null && imageBytes.Length > 0)
                {
                    await _fileWriteService.WriteAtomicAsync(
                        originalPath,
                        async (tempPath) => await File.WriteAllBytesAsync(tempPath, imageBytes),
                        async (tempPath) => 
                        {
                             // Verify image is not empty
                             return new FileInfo(tempPath).Length > 0;
                        }
                    );
                }
            }

            if (requestedSize.HasValue)
            {
                // Create resized version from original
                _logger.LogInformation("Creating resized artwork ({Size}px) for album {AlbumId}", requestedSize, spotifyAlbumId);
                var originalBytes = await File.ReadAllBytesAsync(originalPath);
                using var stream = new MemoryStream(originalBytes);
                
                // Use Avalonia's Bitmap to decode and scale
                // Note: DecodeToHeight/DecodeToWidth is efficient as it doesn't load the full image into memory if possible
                using var bitmap = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, requestedSize.Value);
                bitmap.Save(cachePath);
            }
            
            return cachePath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get artwork for album {AlbumId} (Size: {Size})", spotifyAlbumId, requestedSize);
            return await GetPlaceholderPathAsync();
        }
    }

    /// <summary>
    /// Gets the path to the placeholder image, creating it if necessary.
    /// </summary>
    private async Task<string> GetPlaceholderPathAsync()
    {
        if (File.Exists(_placeholderPath))
            return _placeholderPath;

        try
        {
            // Create a simple 1x1 transparent PNG as placeholder
            // PNG header + IHDR + IDAT (empty) + IEND
            var placeholderBytes = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
            
            await _fileWriteService.WriteAllBytesAtomicAsync(_placeholderPath, placeholderBytes);
            _logger.LogInformation("Created placeholder image at {Path}", _placeholderPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create placeholder image");
        }

        return _placeholderPath;
    }

    /// <summary>
    /// Clears all cached artwork files.
    /// </summary>
    public async Task ClearCacheAsync()
    {
        try
        {
            var files = Directory.GetFiles(_cacheDirectory, "*.jpg");
            foreach (var file in files)
            {
                File.Delete(file);
            }
            
            _logger.LogInformation("Cleared {Count} cached artwork files", files.Length);
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear artwork cache");
        }
    }

    /// <summary>
    /// Gets the total size of the artwork cache in bytes.
    /// </summary>
    public async Task<long> GetCacheSizeAsync()
    {
        try
        {
            var files = Directory.GetFiles(_cacheDirectory, "*.jpg");
            long totalSize = 0;
            
            foreach (var file in files)
            {
                var fileInfo = new FileInfo(file);
                totalSize += fileInfo.Length;
            }
            
            _logger.LogDebug("Artwork cache size: {Size} bytes ({Count} files)", totalSize, files.Length);
            return await Task.FromResult(totalSize);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to calculate cache size");
            return 0;
        }
    }

    /// <summary>
    /// Removes orphaned artwork files that are no longer referenced in the database.
    /// </summary>
    public async Task CleanupOrphanedArtworkAsync(HashSet<string> activeAlbumIds)
    {
        try
        {
            var files = Directory.GetFiles(_cacheDirectory, "*.jpg");
            int removedCount = 0;
            
            foreach (var file in files)
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (!activeAlbumIds.Contains(fileName))
                {
                    File.Delete(file);
                    removedCount++;
                }
            }
            
            _logger.LogInformation("Removed {Count} orphaned artwork files", removedCount);
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup orphaned artwork");
        }
    }
}
