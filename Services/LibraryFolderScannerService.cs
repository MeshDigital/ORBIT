using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SLSKDONET.Models;

namespace SLSKDONET.Services
{
    public class ScanProgress
    {
        public int FilesDiscovered { get; set; }
        public int FilesImported { get; set; }
        public int FilesSkipped { get; set; }
    }

    public class ScanResult
    {
        public int FilesImported { get; set; }
        public int FilesSkipped { get; set; }
    }

    public interface ILibraryFolderScannerService
    {
        Task InitializeIndexAsync(IEnumerable<string> folders);
        Task<string?> AttemptSmartRelinkAsync(string fileName, long fileSize);
        double CalculateConfidence(string foundPath, string originalFileName, long originalFileSize, TimeSpan? duration = null);
        Task EnsureDefaultFolderAsync(string? defaultPath = null);
        Task<Dictionary<string, ScanResult>> ScanAllFoldersAsync(IProgress<ScanProgress>? progress = null);
    }

    public class LibraryFolderScannerService : ILibraryFolderScannerService
    {
        // Key: "FileName|Size" -> Value: FullPath
        private readonly ConcurrentDictionary<string, string> _shadowIndex = new();

        public async Task InitializeIndexAsync(IEnumerable<string> folders)
        {
            _shadowIndex.Clear();
            
            foreach (var folder in folders)
            {
                if (!Directory.Exists(folder)) continue;

                await Task.Run(() => 
                {
                    foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
                    {
                        if (IsAudioFile(file))
                        {
                            var info = new FileInfo(file);
                            string key = $"{info.Name}|{info.Length}";
                            _shadowIndex.TryAdd(key, file);
                        }
                    }
                });
            }
        }

        public Task<string?> AttemptSmartRelinkAsync(string fileName, long fileSize)
        {
            string key = $"{fileName}|{fileSize}";
            if (_shadowIndex.TryGetValue(key, out var path))
            {
                return Task.FromResult<string?>(path);
            }
            return Task.FromResult<string?>(null);
        }

        public double CalculateConfidence(string foundPath, string originalFileName, long originalFileSize, TimeSpan? duration = null)
        {
            var info = new FileInfo(foundPath);
            
            // Level 1: 100% Match (Name + Size)
            if (info.Name == originalFileName && info.Length == originalFileSize)
                return 1.0;

            // Level 2: Name match + Size within 5% (Possible tag edit)
            if (info.Name == originalFileName && Math.Abs(info.Length - originalFileSize) < (originalFileSize * 0.05))
                return 0.9;

            // Level 3: Duration Match (Requires TagLib read, implementation deferred to specific relink pass)
            return 0.5;
        }

        private bool IsAudioFile(string path)
        {
            string ext = Path.GetExtension(path).ToLower();
            return ext == ".mp3" || ext == ".flac" || ext == ".wav" || ext == ".m4a" || ext == ".ogg";
        }

        public Task EnsureDefaultFolderAsync(string? defaultPath = null)
        {
            // Stub for compatibility
            return Task.CompletedTask;
        }

        public Task<Dictionary<string, ScanResult>> ScanAllFoldersAsync(IProgress<ScanProgress>? progress = null)
        {
            // Stub for compatibility with older viewmodels. Core scanning is now driven by Job manager.
            var results = new Dictionary<string, ScanResult>();
            if (progress != null)
            {
                progress.Report(new ScanProgress { FilesDiscovered = 0, FilesImported = 0, FilesSkipped = 0 });
            }
            return Task.FromResult(results);
        }
    }
}
