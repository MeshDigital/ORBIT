using System;
using System.IO;
using Avalonia.Media;
using Soulseek;
using ReactiveUI;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels
{
    public class AnalyzedSearchResultViewModel : ReactiveObject
    {
        private readonly SearchResult _result;



        public SearchResult RawResult => _result;

        // Base Properties
        public string Filename => Path.GetFileName(_result.Filename);
        public string FullPath => _result.Filename;
        public long Size => _result.Size;
        public int BitRate => _result.Bitrate;
        public int? Length => _result.Length;
        public string User => _result.Username;
        public int UploadSpeed => _result.UploadSpeed;
        public int QueueLength => _result.QueueLength;
        public bool SlotFree => _result.SlotFree;
        
        // Formatted Values
        public string DisplayLength => Length.HasValue ? TimeSpan.FromSeconds(Length.Value).ToString(@"mm\:ss") : "--:--";
        public string DisplaySize => $"{Size / 1024.0 / 1024.0:F1} MB";
        
        // Search 2.0 Forensic Properties
        public int TrustScore { get; }
        public string ForensicAssessment { get; }
        public bool IsGoldenMatch { get; }
        public bool IsFake { get; }
        public bool IsSuspicious => IsFake;

        public IBrush ItemBackground
        {
            get
            {
                if (IsFake) return Brushes.Transparent; // Will look slightly dimmed due to text color
                if (IsGoldenMatch) return new SolidColorBrush(Color.Parse("#1A201A")); // Very subtle green tint
                return Brushes.Transparent;
            }
        }
        
        public IBrush ForegroundColor
        {
            get
            {
                if (IsFake) return new SolidColorBrush(Color.Parse("#666666")); // Dimmed
                if (IsGoldenMatch) return Brushes.White;
                return new SolidColorBrush(Color.Parse("#DDDDDD"));
            }
        }

        public string TrustColor
        {
            get
            {
                if (TrustScore >= 90) return "#1DB954"; // Green
                if (TrustScore >= 70) return "#2196F3"; // Blue
                if (TrustScore >= 50) return "#FFC107"; // Amber
                return "#F44336"; // Red
            }
        }
        
        // Trust Bar Visualization (Width 0-100)
        public double TrustBarWidth => TrustScore;

        public AnalyzedSearchResultViewModel(SearchResult result)
        {
            _result = result;

            // Calculate Metrics
            TrustScore = MetadataForensicService.CalculateTrustScore(result.Model);
            ForensicAssessment = MetadataForensicService.GetForensicAssessment(result.Model);
            IsGoldenMatch = MetadataForensicService.IsGoldenMatch(result.Model);
            IsFake = MetadataForensicService.IsFake(result.Model);
        }
    }
}
