using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.Wave;
using ReactiveUI;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;
using SLSKDONET.Views;

namespace SLSKDONET.Services.Audio
{
    public class StemExportJob : ReactiveObject
    {
        public PlaylistTrackViewModel Track { get; }
        public bool AcapellaOnly { get; }

        private double _progress;
        public double Progress
        {
            get => _progress;
            set => this.RaiseAndSetIfChanged(ref _progress, value);
        }

        private string _status = "Pending";
        public string Status
        {
            get => _status;
            set => this.RaiseAndSetIfChanged(ref _status, value);
        }

        public StemExportJob(PlaylistTrackViewModel track, bool acapellaOnly)
        {
            Track = track;
            AcapellaOnly = acapellaOnly;
        }
    }

    public class BatchStemExportService : ReactiveObject
    {
        private readonly ILibraryService _libraryService;
        private readonly AppConfig _appConfig;
        private readonly INotificationService _notificationService;

        private readonly ConcurrentQueue<StemExportJob> _queue = new();
        
        public ObservableCollection<StemExportJob> ActiveJobs { get; } = new();

        private bool _isProcessing = false;
        private CancellationTokenSource? _cts;

        private const int ChunkDurationSeconds = 30;

        public BatchStemExportService(
            ILibraryService libraryService,
            AppConfig appConfig,
            INotificationService notificationService)
        {
            _libraryService = libraryService;
            _appConfig = appConfig;
            _notificationService = notificationService;
        }

        public void EnqueueBatch(IEnumerable<PlaylistTrackViewModel> tracks, bool acapellaOnly)
        {
            foreach (var track in tracks)
            {
                var job = new StemExportJob(track, acapellaOnly);
                _queue.Enqueue(job);
                Dispatcher.UIThread.Post(() => ActiveJobs.Add(job));
            }

            if (!_isProcessing)
            {
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => ProcessQueueAsync(_cts.Token));
            }
        }

        public void CancelAll()
        {
            _cts?.Cancel();
            _queue.Clear();
            Dispatcher.UIThread.Post(() => ActiveJobs.Clear());
        }

        private async Task ProcessQueueAsync(CancellationToken ct)
        {
            _isProcessing = true;
            InferenceSession? session = null;
            
            try
            {
                string modelPath = Path.Combine("Tools", "Essentia", "models", "spleeter-5stems.onnx");
                if (!File.Exists(modelPath))
                {
                    _notificationService.Show("ONNX Error", "Spleeter model not found.", NotificationType.Error);
                    return;
                }

                // Model Lifecycle: Load ONNX session ONLY when queue starts
                session = new InferenceSession(modelPath);

                string libraryRoot = _appConfig.LibraryRootPaths.FirstOrDefault() 
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Antigravity", "Library");
                string stemsDir = Path.Combine(libraryRoot, "Stems");
                if (!Directory.Exists(stemsDir)) Directory.CreateDirectory(stemsDir);

                while (_queue.TryDequeue(out var job))
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        job.Status = "Processing...";
                        await ProcessJobChunkedAsync(job, session, stemsDir, ct);
                        job.Status = "Completed";
                        job.Progress = 1.0;
                    }
                    catch (OperationCanceledException)
                    {
                        job.Status = "Canceled";
                        break; // Break the while loop if cancelled
                    }
                    catch (Exception ex)
                    {
                        job.Status = "Failed";
                        Console.WriteLine($"[BatchStemExport] Chunking Error: {ex.Message}");
                    }
                    finally
                    {
                        // Clean up UI list optionally or leave it for user to clear
                    }
                }
            }
            finally
            {
                // Model Lifecycle: Dispose immediately when queue is empty to free VRAM
                session?.Dispose();
                _isProcessing = false;
                
                if (ActiveJobs.Any(j => j.Status == "Completed"))
                {
                    _notificationService.Show("Acapella Factory", "Batch processing complete.", NotificationType.Success);
                }
            }
        }

        private async Task ProcessJobChunkedAsync(StemExportJob job, InferenceSession session, string stemsDir, CancellationToken ct)
        {
            string inputPath = job.Track.Model.ResolvedFilePath;
            if (string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath))
                throw new FileNotFoundException("Track file not found", inputPath);

            string baseName = job.Track.Title ?? "Unknown Track";
            foreach (char c in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c, '_');

            string vocalPath = Path.Combine(stemsDir, $"{baseName} (Acapella).wav");
            string instPath = Path.Combine(stemsDir, $"{baseName} (Instrumental).wav");

            // Avoid rewriting if it exists
            if (File.Exists(vocalPath) && (job.AcapellaOnly || File.Exists(instPath)))
            {
                // Phase 2 target: Auto-import existing if needed, but for now skip
                return;
            }

            using var reader = new AudioFileReader(inputPath);
            int sampleRate = reader.WaveFormat.SampleRate;
            int channels = reader.WaveFormat.Channels;
            
            // Standardize output to 44.1kHz Stereo 16-bit
            var outFormat = new WaveFormat(sampleRate, 16, 2);

            using var vocalWriter = new WaveFileWriter(vocalPath, outFormat);
            using var instWriter = job.AcapellaOnly ? null : new WaveFileWriter(instPath, outFormat);

            // 30 seconds chunks
            int framesPerChunk = ChunkDurationSeconds * sampleRate;
            float[] buffer = new float[framesPerChunk * channels];
            
            long totalFrames = reader.Length / (reader.WaveFormat.BitsPerSample / 8) / channels;
            long processedFrames = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                int floatsRead = reader.Read(buffer, 0, buffer.Length);
                if (floatsRead == 0) break; // EOF

                int framesRead = floatsRead / channels;

                // De-interleave and load into Tensor [Frames, 2]
                var inputTensor = new DenseTensor<float>(new[] { framesRead, 2 });
                if (channels == 2)
                {
                    for (int i = 0; i < framesRead; i++)
                    {
                        inputTensor[i, 0] = buffer[i * 2];
                        inputTensor[i, 1] = buffer[i * 2 + 1];
                    }
                }
                else
                {
                    for (int i = 0; i < framesRead; i++)
                    {
                        inputTensor[i, 0] = buffer[i];
                        inputTensor[i, 1] = buffer[i];
                    }
                }

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("waveform:0", inputTensor)
                };

                // ONNX Inference on a 30s chunk
                using var results = session.Run(inputs);

                // Write Vocals
                var vocalTensor = results.First(r => r.Name == "waveform_vocals:0").AsTensor<float>();
                WriteTensorToWav(vocalTensor, vocalWriter, framesRead);

                // Write Instrumental
                if (!job.AcapellaOnly && instWriter != null)
                {
                    // For the 5-stem spleeter, instrumental is technically Drums+Bass+Piano+Other.
                    // If we just want a unified instrumental, we sum them.
                    var drums = results.First(r => r.Name == "waveform_drums:0").AsTensor<float>();
                    var bass = results.First(r => r.Name == "waveform_bass:0").AsTensor<float>();
                    var piano = results.First(r => r.Name == "waveform_piano:0").AsTensor<float>();
                    var other = results.First(r => r.Name == "waveform_other:0").AsTensor<float>();

                    float[] instBuffer = new float[framesRead * 2];
                    for (int i = 0; i < framesRead; i++)
                    {
                        instBuffer[i * 2] = drums[i, 0] + bass[i, 0] + piano[i, 0] + other[i, 0];
                        instBuffer[i * 2 + 1] = drums[i, 1] + bass[i, 1] + piano[i, 1] + other[i, 1];
                    }
                    instWriter.WriteSamples(instBuffer, 0, instBuffer.Length);
                }

                processedFrames += framesRead;
                job.Progress = (double)processedFrames / totalFrames;
            }

            // Flush streams
            vocalWriter.Flush();
            if (instWriter != null) instWriter.Flush();
            
            // Phase 2: DB import
            var parentEntity = await _libraryService.GetTrackEntityByHashAsync(job.Track.Model.TrackUniqueHash);
            if (parentEntity != null)
            {
                var parentTrackEntity = new SLSKDONET.Data.TrackEntity
                {
                    GlobalId = parentEntity.UniqueHash,
                    Artist = parentEntity.Artist,
                    Title = parentEntity.Title,
                    BPM = parentEntity.BPM,
                    MusicalKey = parentEntity.MusicalKey,
                    Energy = parentEntity.Energy,
                    PrimaryGenre = parentEntity.PrimaryGenre,
                    DetectedSubGenre = parentEntity.DetectedSubGenre,
                    ReleaseDate = parentEntity.ReleaseDate
                };

                // Auto-import Vocals
                await _libraryService.ImportGeneratedStemAsync(parentTrackEntity, vocalPath, SLSKDONET.Models.Stem.StemType.Vocals);

                // Auto-import Instrumentals if generated
                if (!job.AcapellaOnly && instWriter != null)
                {
                    await _libraryService.ImportGeneratedStemAsync(parentTrackEntity, instPath, SLSKDONET.Models.Stem.StemType.Instrumental);
                }
            }
        }

        private void WriteTensorToWav(Tensor<float> tensor, WaveFileWriter writer, int frames)
        {
            float[] stereoBuffer = new float[frames * 2];
            for (int i = 0; i < frames; i++)
            {
                stereoBuffer[i * 2] = tensor[i, 0];
                stereoBuffer[i * 2 + 1] = tensor[i, 1];
            }
            writer.WriteSamples(stereoBuffer, 0, stereoBuffer.Length);
        }
    }
}
