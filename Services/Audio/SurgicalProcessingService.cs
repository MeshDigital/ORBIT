using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Linq;

using SLSKDONET.Models;

namespace SLSKDONET.Services.Audio
{
    /// <summary>
    /// Phase 2: Surgical Editing Engine.
    /// Handles lossless audio stitching, segment cutting, and range-selective stem separation.
    /// </summary>
    public interface ISurgicalProcessingService
    {
        Task<string> CutAndCombineSegmentsAsync(string sourcePath, IEnumerable<PhraseSegment> segments, CancellationToken ct = default);
        Task<string> IsolateStemInRangeAsync(string sourcePath, float startTime, float duration, string stemType, CancellationToken ct = default);
        Task<bool> RenderPreviewAsync(string sourcePath, IEnumerable<PhraseSegment> segments, CancellationToken ct = default);
    }

    public class SurgicalProcessingService : ISurgicalProcessingService
    {
        private readonly ILogger<SurgicalProcessingService> _logger;
        private readonly StemCacheService _stemCache;
        private readonly string _surgicalWorkDir;

        public SurgicalProcessingService(ILogger<SurgicalProcessingService> logger, StemCacheService stemCache)
        {
            _logger = logger;
            _stemCache = stemCache;
            _surgicalWorkDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ORBIT", "SurgicalTemp");
            Directory.CreateDirectory(_surgicalWorkDir);
        }

        public async Task<string> CutAndCombineSegmentsAsync(string sourcePath, IEnumerable<PhraseSegment> segments, CancellationToken ct = default)
        {
            _logger.LogInformation("✂️ Surgical Surgery: Orchestrating FFmpeg for {Path}", sourcePath);
            
            var sortedSegments = segments.OrderBy(s => s.Start).ToList();
            if (!sortedSegments.Any()) return string.Empty;

            string outputFileName = $"Surgical_{Path.GetFileNameWithoutExtension(sourcePath)}_{DateTime.Now:yyyyMMddHHmmss}.flac";
            string outputPath = Path.Combine(_surgicalWorkDir, outputFileName);

            var filterParts = new List<string>();
            var labels = new List<string>();

            for (int i = 0; i < sortedSegments.Count; i++)
            {
                var seg = sortedSegments[i];
                string label = $"s{i}";
                labels.Add(label);
                
                string start = seg.Start.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                string end = (seg.Start + seg.Duration).ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                
                filterParts.Add($"[0:a]atrim=start={start}:end={end},asetpts=PTS-STARTPTS[{label}]");
            }

            string crossfadeFilter = string.Empty;
            if (labels.Count > 1)
            {
                string currentOut = labels[0];
                for (int i = 1; i < labels.Count; i++)
                {
                    string nextOut = (i == labels.Count - 1) ? "out" : $"merged{i}";
                    crossfadeFilter += $"[{currentOut}][{labels[i]}]acrossfade=d=0.05:c1=tri:c2=tri[{nextOut}];";
                    currentOut = nextOut;
                }
                crossfadeFilter = crossfadeFilter.TrimEnd(';');
            }
            else
            {
                crossfadeFilter = $"[{labels[0]}]asplit[out]"; // asplit or similar to just map it
            }

            string fullFilter = string.Join(";", filterParts) + ";" + crossfadeFilter;
            if (labels.Count == 1) fullFilter = filterParts[0].Replace($"[{labels[0]}]", "[out]");

            // Build command: ffmpeg -i source -filter_complex "..." -map "[out]" output
            var startInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -i \"{sourcePath}\" -filter_complex \"{fullFilter}\" -map \"[out]\" -c:a flac \"{outputPath}\"",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _logger.LogDebug("🎬 FFmpeg Surgical Command: ffmpeg {Args}", startInfo.Arguments);

            using var process = new Process { StartInfo = startInfo };
            var errorOutput = new StringBuilder();
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) errorOutput.AppendLine(e.Data); };

            process.Start();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                _logger.LogError("❌ FFmpeg Surgical Failed (Exit {Code}): {Error}", process.ExitCode, errorOutput.ToString());
                return string.Empty;
            }

            _logger.LogInformation("✅ Surgical Render Complete: {Path}", outputPath);
            return outputPath;
        }

        public async Task<string> IsolateStemInRangeAsync(string sourcePath, float startTime, float duration, string stemType, CancellationToken ct = default)
        {
            _logger.LogInformation("🔬 Surgical Surgery: Isolating {Stem} from {Start}s for {Duration}s", stemType, startTime, duration);
            
            string trackHash = Path.GetFileNameWithoutExtension(sourcePath); 
            
            var cached = await _stemCache.TryGetCachedStemAsync(trackHash, startTime, duration, stemType);
            if (cached != null) return cached;

            // Phase 7: Real Stem Isolation via FFmpeg/Spleeter/UVRLib
            // For now, we crop the segment as a placeholder, then in Phase 8 we'll hook up the Neural separator.
            string stemResultPath = Path.Combine(_surgicalWorkDir, $"stem_{stemType}_{Guid.NewGuid().ToString().Substring(0,8)}.flac");
            
            string startStr = startTime.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
            string durStr = duration.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

            var startInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -ss {startStr} -t {durStr} -i \"{sourcePath}\" -c:a flac \"{stemResultPath}\"",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            await process.WaitForExitAsync(ct);

            if (process.ExitCode == 0)
            {
                await _stemCache.StoreStemAsync(trackHash, startTime, duration, stemType, stemResultPath);
                return stemResultPath;
            }
            
            return string.Empty;
        }

        public async Task<bool> RenderPreviewAsync(string sourcePath, IEnumerable<PhraseSegment> segments, CancellationToken ct = default)
        {
            _logger.LogInformation("🎧 Surgical Surgery: Preparing render preview for {Path}", sourcePath);
            
            var previewPath = await CutAndCombineSegmentsAsync(sourcePath, segments, ct);
            
            // In a real app, we'd trigger the player to play this temp file.
            return !string.IsNullOrEmpty(previewPath);
        }
    }
}
