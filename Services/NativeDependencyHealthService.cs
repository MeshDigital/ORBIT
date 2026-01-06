using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Services
{
    public interface INativeDependencyHealthService
    {
        Task<DependencyHealthStatus> CheckHealthAsync();
        bool IsFfmpegAvailable { get; }
        bool IsEssentiaAvailable { get; }
    }

    public class NativeDependencyHealthService : INativeDependencyHealthService
    {
        private readonly ILogger<NativeDependencyHealthService> _logger;
        private readonly PathProviderService _pathProvider;
        
        // Cache status to avoid disk I/O on every UI update
        private bool? _isFfmpegAvailable;
        private bool? _isEssentiaAvailable;
        private DateTime _lastCheck = DateTime.MinValue;
        private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(5);

        private const string FFMPEG_EXE = "ffmpeg.exe";
        private const string ESSENTIA_EXE = "essentia_streaming_extractor_music.exe";

        public bool IsFfmpegAvailable => _isFfmpegAvailable ?? false;
        public bool IsEssentiaAvailable => _isEssentiaAvailable ?? false;

        public NativeDependencyHealthService(
            ILogger<NativeDependencyHealthService> logger,
            PathProviderService pathProvider)
        {
            _logger = logger;
            _pathProvider = pathProvider;
        }

        public async Task<DependencyHealthStatus> CheckHealthAsync()
        {
            // Simple cache invalidation
            if (_isFfmpegAvailable.HasValue && DateTime.UtcNow - _lastCheck < _checkInterval)
            {
                return new DependencyHealthStatus 
                { 
                    IsFfmpegReady = _isFfmpegAvailable.Value,
                    IsEssentiaReady = _isEssentiaAvailable.Value 
                };
            }

            return await Task.Run(() =>
            {
                // Check Tools Directory (Priority)
                var toolsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools");
                var ffmpegPath = Path.Combine(toolsDir, "FFmpeg", FFMPEG_EXE);
                var essentiaPath = Path.Combine(toolsDir, "Essentia", ESSENTIA_EXE);

                // 1. Check FFmpeg
                bool ffmpegFound = File.Exists(ffmpegPath);
                if (!ffmpegFound)
                {
                    // Fallback to PATH (Simple check)
                    ffmpegFound = CheckPathForBinary(FFMPEG_EXE);
                }

                // 2. Check Essentia
                bool essentiaFound = File.Exists(essentiaPath);
                if (!essentiaFound)
                {
                    essentiaFound = CheckPathForBinary(ESSENTIA_EXE);
                }

                _isFfmpegAvailable = ffmpegFound;
                _isEssentiaAvailable = essentiaFound;
                _lastCheck = DateTime.UtcNow;

                if (!ffmpegFound || !essentiaFound)
                {
                    _logger.LogWarning("Dependency Health Check Failed: FFmpeg={Ffmpeg}, Essentia={Essentia}", 
                        ffmpegFound, essentiaFound);
                }
                else
                {
                     _logger.LogInformation("Dependency Health Check Passed");
                }

                return new DependencyHealthStatus
                {
                    IsFfmpegReady = ffmpegFound,
                    IsEssentiaReady = essentiaFound
                };
            });
        }

        private bool CheckPathForBinary(string binaryName)
        {
            try
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                foreach (var dir in pathEnv.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    
                    try 
                    {
                        var fullPath = Path.Combine(dir, binaryName);
                        if (File.Exists(fullPath)) return true;
                    }
                    catch { /* Ignore invalid paths in PATH env */ }
                }
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Failed to check PATH for {Binary}", binaryName);
            }
            return false;
        }
    }

    public class DependencyHealthStatus
    {
        public bool IsFfmpegReady { get; set; }
        public bool IsEssentiaReady { get; set; }
        public bool IsHealthy => IsFfmpegReady && IsEssentiaReady;
    }
}
