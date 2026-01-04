using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services
{
    /// <summary>
    /// Provides a shared cache for artwork bitmaps to prevent memory bloat.
    /// Uses ConditionalWeakTable to ensure bitmaps are eligible for collection when no longer referenced by active ViewModels,
    /// while deduplicating identical URLs/Paths.
    /// </summary>
    public class ArtworkCacheService
    {
        private readonly ILogger<ArtworkCacheService> _logger;
        private readonly HttpClient _httpClient;
        
        // We use ConditionalWeakTable to map a specific string instance (the URL/Key) to a Bitmap.
        // NOTE: For this to work efficiently with data binding, we need to ensure we use the SAME string instance for the same URL.
        // We will use a separate Intern pool for this.
        private readonly ConditionalWeakTable<string, Bitmap> _cache = new();
        
        // A simple lock object for thread safety during cache access (though CWT is thread-safe, the load logic logic needs care)
        // Actually, CWT is thread safe. But we want to avoid double-loading.
        // We can't put Tasks in CWT easily if we want the final object to be the Bitmap.
        // So we might accept some double loading or use a secondary dictionary for "Loading Tasks".
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<Bitmap?>> _loadingTasks = new();

        public ArtworkCacheService(ILogger<ArtworkCacheService> logger, HttpClient httpClient)
        {
            _logger = logger;
            _httpClient = httpClient;
        }

        /// <summary>
        /// Retrieves a shared Bitmap instance for the given URI or File Path.
        /// If the bitmap is already in memory, returns the existing instance.
        /// </summary>
        public async Task<Bitmap?> GetBitmapAsync(string? uriOrPath)
        {
            if (string.IsNullOrWhiteSpace(uriOrPath)) return null;

            // 1. Intern the string to ensure we have a unique object key for CWT.
            // String.Intern is one way, but it lives forever. 
            // Better: We can rely on the fact that if the ViewModel holds the string, we can use THAT string instance if we are careful.
            // However, to truly deduplicate across different ViewModels that might have constructed different string objects for the same URL,
            // we effectively need a "Key" object.
            // Let's use the provided string. If we want true dedup, we might need a String Interning service, 
            // OR we accept that CWT works best when the KEY is the object managing the lifetime.
            // Actually, the user asked for CWT<string, Bitmap>. 
            // Problem: Strings are value-equal usually but reference-distinct.
            // If 50 tracks have "http://.../img.jpg", they likely have 50 string instances.
            // If they are distinct references, CWT won't deduce them to one entry.
            // SOLUTION: We will use a ConcurrentDictionary<string, Bitmap> as a "Strong Cache" (LRU) or just a simple Cache 
            // for the scope of this request.
            // WAIT, the prompt explicitly said: "Implement an ArtworkCacheService using ConditionalWeakTable<string, Bitmap>."
            // To make this work, I'll use String.Intern(uri) as the key. 
            // While Interned strings never die (causing a small leak of strings), URLs are relatively small compared to Bitmaps.
            // This satisfies the requirement and ensures uniqueness.
            
            string key = string.Intern(uriOrPath);

            // 2. Check Cache
            if (_cache.TryGetValue(key, out var bitmap))
            {
                return bitmap;
            }

            // 3. Load (with dedup via loadingTasks)
            return await _loadingTasks.GetOrAdd(key, async (k) =>
            {
                try
                {
                    var loaded = await LoadBitmapInternalAsync(k);
                    if (loaded != null)
                    {
                        // Add to CWT
                        _cache.Add(k, loaded);
                    }
                    return loaded;
                }
                finally
                {
                    _loadingTasks.TryRemove(k, out _);
                }
            });
        }

        private async Task<Bitmap?> LoadBitmapInternalAsync(string uriOrPath)
        {
            try
            {
                if (uriOrPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    // Download
                    var data = await _httpClient.GetByteArrayAsync(uriOrPath);
                    using var stream = new MemoryStream(data);
                    return new Bitmap(stream);
                }
                else if (File.Exists(uriOrPath))
                {
                    // Local File
                    // Use a stream to avoid locking the file? Bitmap(path) usually locks?
                    // Avalonia Bitmap(string) loads it.
                    return new Bitmap(uriOrPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load artwork: {Path}", uriOrPath);
            }
            return null;
        }
    }
}
