using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.Musical;
using SLSKDONET.Services.Audio;
using SLSKDONET.Models.Musical;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels
{
    public class FlowBuilderViewModel : ReactiveObject
    {
        private readonly SetListService _setListService;
        private readonly ITransitionAdvisorService _advisor;
        private readonly ITransitionPreviewPlayer _previewPlayer;
        private readonly LibraryService _libraryService;

        private SetListEntity? _currentSet;
        public SetListEntity? CurrentSet
        {
            get => _currentSet;
            set => this.RaiseAndSetIfChanged(ref _currentSet, value);
        }

        private FlowWeightSettings _weights = new();
        public FlowWeightSettings Weights
        {
            get => _weights;
            set => this.RaiseAndSetIfChanged(ref _weights, value);
        }

        private double _flowContinuityScore;
        public double FlowContinuityScore
        {
            get => _flowContinuityScore;
            set => this.RaiseAndSetIfChanged(ref _flowContinuityScore, value);
        }

        public ObservableCollection<SetTrackViewModel> Tracks { get; } = new();

        public ReactiveCommand<Unit, Unit> RefreshFlowCommand { get; }
        public ReactiveCommand<Unit, Unit> SaveSetCommand { get; }

        public FlowBuilderViewModel(
            SetListService setListService,
            ITransitionAdvisorService advisor,
            ITransitionPreviewPlayer previewPlayer,
            LibraryService libraryService)
        {
            _setListService = setListService;
            _advisor = advisor;
            _previewPlayer = previewPlayer;
            _libraryService = libraryService;

            RefreshFlowCommand = ReactiveCommand.Create(UpdateFlowAnalysis);
            SaveSetCommand = ReactiveCommand.CreateFromTask(SaveSetAsync);

            // Auto-update flow when weights change (debounced)
            this.WhenAnyValue(x => x.Weights)
                .Throttle(TimeSpan.FromMilliseconds(300))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => UpdateFlowAnalysis());
        }

        public async Task LoadSetAsync(Guid setListId)
        {
            // Implementation to load set and parse weights from JSON
            var set = await _setListService.GetSetListAsync(setListId);
            if (set != null)
            {
                CurrentSet = set;
                if (!string.IsNullOrEmpty(set.FlowWeightsJson))
                {
                    Weights = System.Text.Json.JsonSerializer.Deserialize<FlowWeightSettings>(set.FlowWeightsJson) ?? new FlowWeightSettings();
                }
                
                // Clear and repopulate tracks
                Tracks.Clear();
                foreach (var track in set.Tracks.OrderBy(t => t.Position))
                {
                    // Map track entity to ViewModel
                    // In a real app, we'd fetch the LibraryEntry to get vocal metrics
                    var entry = await _libraryService.GetTrackEntityByHashAsync(track.TrackUniqueHash);
                    if (entry != null)
                    {
                        Tracks.Add(new SetTrackViewModel(entry, track));
                    }
                }
                
                UpdateFlowAnalysis();
            }
        }

        private void UpdateFlowAnalysis()
        {
            if (CurrentSet == null) return;
            FlowContinuityScore = _advisor.CalculateFlowContinuity(CurrentSet, Weights);

            // Update transition reasoning for each track pair
            for (int i = 0; i < Tracks.Count - 1; i++)
            {
                var current = Tracks[i];
                var next = Tracks[i + 1];
                
                var suggestion = _advisor.AdviseTransition(current.AudioFeatures, next.AudioFeatures, Weights);
                next.TransitionType = suggestion.Archetype;
                next.ForensicReasoning = suggestion.Reasoning;
                
                // Vocal Safety check
                var vocalReport = _advisor.CheckVocalConflict(current.AudioFeatures, next.AudioFeatures, next.ManualOffset);
                next.VocalSafetyScore = (int)(vocalReport.VocalSafetyScore * 100);
                next.CompatibilityWarning = vocalReport.WarningMessage ?? string.Empty;
            }
        }

        private async Task SaveSetAsync()
        {
            if (CurrentSet == null) return;
            CurrentSet.FlowWeightsJson = System.Text.Json.JsonSerializer.Serialize(Weights);
            await _setListService.UpdateSetListAsync(CurrentSet);
        }
    }

    public class SetTrackViewModel : ReactiveObject
    {
        public LibraryEntryEntity AudioFeatures { get; }
        public SetTrackEntity TrackEntity { get; }

        public string Title => AudioFeatures.Title ?? "Unknown";
        public string Artist => AudioFeatures.Artist ?? "Unknown";
        
        private TransitionArchetype _transitionType;
        public TransitionArchetype TransitionType
        {
            get => _transitionType;
            set => this.RaiseAndSetIfChanged(ref _transitionType, value);
        }

        private string _forensicReasoning = string.Empty;
        public string ForensicReasoning
        {
            get => _forensicReasoning;
            set => this.RaiseAndSetIfChanged(ref _forensicReasoning, value);
        }

        private int _vocalSafetyScore;
        public int VocalSafetyScore
        {
            get => _vocalSafetyScore;
            set => this.RaiseAndSetIfChanged(ref _vocalSafetyScore, value);
        }

        public string CompatibilityWarning { get; set; } = string.Empty;

        public double ManualOffset => TrackEntity.ManualOffset;

        public string VocalTag => AudioFeatures.VocalType switch
        {
            VocalType.Instrumental => "🎹 INST",
            VocalType.SparseVocals => "🎤 SPARSE",
            VocalType.HookOnly => "🎶 HOOK",
            VocalType.FullLyrics => "📜 LYRICS",
            _ => "❓ UNKNOWN"
        };

        public SetTrackViewModel(LibraryEntryEntity features, SetTrackEntity entity)
        {
            AudioFeatures = features;
            TrackEntity = entity;
            _transitionType = entity.TransitionType;
            _forensicReasoning = entity.TransitionReasoning ?? string.Empty;
        }
    }
}
