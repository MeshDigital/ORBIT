using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services;
using SLSKDONET.Services.Musical;
using SLSKDONET.Services.Audio;

namespace SLSKDONET.ViewModels
{
    public class FlowBuilderViewModel : ReactiveObject
    {
        private readonly SetListService _setListService;
        private readonly ITransitionAdvisorService _advisor;
        private readonly ITransitionPreviewPlayer _previewPlayer;

        private SetListEntity? _currentSet;
        public SetListEntity? CurrentSet
        {
            get => _currentSet;
            set => this.RaiseAndSetIfChanged(ref _currentSet, value);
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
            ITransitionPreviewPlayer previewPlayer)
        {
            _setListService = setListService;
            _advisor = advisor;
            _previewPlayer = previewPlayer;

            RefreshFlowCommand = ReactiveCommand.Create(UpdateFlowAnalysis);
            SaveSetCommand = ReactiveCommand.CreateFromTask(SaveSetAsync);
        }

        public async Task LoadSetAsync(Guid setListId)
        {
            // Logic to load set from service
        }

        private void UpdateFlowAnalysis()
        {
            if (CurrentSet == null) return;
            FlowContinuityScore = _advisor.CalculateFlowContinuity(CurrentSet);
        }

        private async Task SaveSetAsync()
        {
            if (CurrentSet == null) return;
            // Logic to save set via _setListService
        }
    }

    public class SetTrackViewModel : ReactiveObject
    {
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public TransitionArchetype TransitionType { get; set; }
        public string CompatibilityWarning { get; set; } = string.Empty;
    }
}
