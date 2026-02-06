using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Models.Musical;
using SLSKDONET.Services.Musical;

namespace SLSKDONET.ViewModels
{
    /// <summary>
    /// Phase 5.4/6: Forensic Inspector ViewModel
    /// Displays detailed forensic reasoning for selected stress points from the Setlist Stress-Test.
    /// When user clicks a red/yellow segment on the HealthBar, this panel explains:
    /// - Why the transition fails (detailed reasoning)
    /// - What problems were detected
    /// - 1-3 rescue track suggestions with reasoning
    /// - Actionable mentor-style advice
    ///
    /// Phase 6: Added ApplyRescueTrackCommand for one-click rescue application.
    /// </summary>
    public class ForensicInspectorViewModel : ReactiveObject
    {
        private TransitionStressPoint? _selectedStressPoint;
        private string _displayReasoning = string.Empty;
        private bool _hasRescueSuggestions;
        private int _selectedRescueIndex = -1;
        private bool _isApplying;

        /// <summary>
        /// Currently displayed stress point (from HealthBar selection).
        /// </summary>
        public TransitionStressPoint? SelectedStressPoint
        {
            get => _selectedStressPoint;
            set => this.RaiseAndSetIfChanged(ref _selectedStressPoint, value);
        }

        /// <summary>
        /// Parsed forensic verdict entries from the stress point's reasoning text.
        /// Rendered in monospaced font for readability.
        /// </summary>
        public ObservableCollection<ForensicVerdictEntry> ForensicVerdicts { get; }
            = new ObservableCollection<ForensicVerdictEntry>();

        /// <summary>
        /// Full text reasoning (from MentorReasoningBuilder output).
        /// </summary>
        public string DisplayReasoning
        {
            get => _displayReasoning;
            set => this.RaiseAndSetIfChanged(ref _displayReasoning, value);
        }

        /// <summary>
        /// True if selected stress point has rescue suggestions.
        /// </summary>
        public bool HasRescueSuggestions
        {
            get => _hasRescueSuggestions;
            set => this.RaiseAndSetIfChanged(ref _hasRescueSuggestions, value);
        }

        /// <summary>
        /// Collection of rescue suggestions for the selected transition.
        /// </summary>
        public ObservableCollection<RescueSuggestion> RescueSuggestions { get; }
            = new ObservableCollection<RescueSuggestion>();

        /// <summary>
        /// Index of currently selected rescue track (for detail display).
        /// </summary>
        public int SelectedRescueIndex
        {
            get => _selectedRescueIndex;
            set => this.RaiseAndSetIfChanged(ref _selectedRescueIndex, value);
        }

        /// <summary>
        /// True while applying a rescue track to the setlist.
        /// </summary>
        public bool IsApplying
        {
            get => _isApplying;
            set => this.RaiseAndSetIfChanged(ref _isApplying, value);
        }

        /// <summary>
        /// The currently displayed rescue track (if selected).
        /// </summary>
        public RescueSuggestion? SelectedRescue
        {
            get
            {
                if (_selectedRescueIndex >= 0 && _selectedRescueIndex < RescueSuggestions.Count)
                    return RescueSuggestions[_selectedRescueIndex];
                return null;
            }
        }

        /// <summary>
        /// Context message for display (e.g., "Transition 4→5: Analyzing...").
        /// </summary>
        public string ContextMessage
        {
            get
            {
                if (SelectedStressPoint != null)
                    return $"Transition {SelectedStressPoint.FromTrackIndex}→{SelectedStressPoint.ToTrackIndex}: {SelectedStressPoint.PrimaryProblem}";
                return "Select a segment on the HealthBar to analyze.";
            }
        }

        /// <summary>
        /// Phase 6: Command to apply the selected rescue track to the setlist.
        /// Inputs: (transitionIndex, rescueSuggestion)
        /// Output: ApplyRescueResult with success status and updated report.
        /// </summary>
        public ReactiveCommand<Unit, ApplyRescueResult> ApplyRescueTrackCommand { get; private set; }

        /// <summary>
        /// Handler to notify parent ViewModel when rescue is applied.
        /// </summary>
        public Func<int, RescueSuggestion, Task<ApplyRescueResult>>? OnApplyRescueTrack { get; set; }

        public ForensicInspectorViewModel()
        {
            // Phase 6: Initialize ApplyRescueTrackCommand
            ApplyRescueTrackCommand = ReactiveCommand.CreateFromTask<Unit, ApplyRescueResult>(
                async _ => await ExecuteApplyRescueTrackAsync());
        }

        /// <summary>
        /// Displays detailed analysis for a selected stress point.
        /// Parses reasoning text, populates rescue suggestions, updates UI.
        /// </summary>
        public void DisplayStressPointDetail(TransitionStressPoint stressPoint)
        {
            if (stressPoint == null)
            {
                ClearDisplay();
                return;
            }

            SelectedStressPoint = stressPoint;
            DisplayReasoning = stressPoint.FailureReasoning;

            // Parse reasoninginto ForensicVerdictEntries
            ParseForensicReasoning(stressPoint.FailureReasoning);

            // Populate rescue suggestions
            RescueSuggestions.Clear();
            if (stressPoint.RescueSuggestions != null && stressPoint.RescueSuggestions.Count > 0)
            {
                foreach (var rescue in stressPoint.RescueSuggestions)
                {
                    RescueSuggestions.Add(rescue);
                }
                HasRescueSuggestions = true;
                SelectedRescueIndex = 0; // Auto-select first rescue
            }
            else
            {
                HasRescueSuggestions = false;
                SelectedRescueIndex = -1;
            }

            this.RaisePropertyChanged(nameof(ContextMessage));
        }

        /// <summary>
        /// Clears all displayed content.
        /// </summary>
        public void ClearDisplay()
        {
            SelectedStressPoint = null;
            DisplayReasoning = string.Empty;
            ForensicVerdicts.Clear();
            RescueSuggestions.Clear();
            HasRescueSuggestions = false;
            SelectedRescueIndex = -1;
            this.RaisePropertyChanged(nameof(ContextMessage));
        }

        /// <summary>
        /// Parses MentorReasoningBuilder output into structured ForensicVerdictEntries.
        /// Recognizes: ▓ (Section), • (Bullet), ⚠ (Warning), ✓ (Success), → (Detail)
        /// </summary>
        private void ParseForensicReasoning(string reasoningText)
        {
            ForensicVerdicts.Clear();

            if (string.IsNullOrWhiteSpace(reasoningText))
                return;

            var lines = reasoningText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            bool inVerdict = false;

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine))
                    continue;

                // Section header
                if (trimmedLine.StartsWith("▓"))
                {
                    var title = trimmedLine.TrimStart('▓', ' ').Trim();
                    if (title.Contains("VERDICT"))
                        inVerdict = true;
                    ForensicVerdicts.Add(ForensicVerdictEntry.Section(title));
                }
                // Standard bullet
                else if (trimmedLine.StartsWith("•"))
                {
                    ForensicVerdicts.Add(ForensicVerdictEntry.Bullet(trimmedLine.TrimStart('•', ' ').Trim()));
                }
                // Warning
                else if (trimmedLine.StartsWith("⚠"))
                {
                    ForensicVerdicts.Add(ForensicVerdictEntry.Warning(trimmedLine.TrimStart('⚠', ' ').Trim()));
                }
                // Success
                else if (trimmedLine.StartsWith("✓"))
                {
                    ForensicVerdicts.Add(ForensicVerdictEntry.Success(trimmedLine.TrimStart('✓', ' ').Trim()));
                }
                // Detail/sub-item
                else if (trimmedLine.StartsWith("→"))
                {
                    ForensicVerdicts.Add(ForensicVerdictEntry.Detail(trimmedLine.TrimStart('→', ' ').Trim()));
                }
                // Separator lines (skip)
                else if (trimmedLine.StartsWith("═"))
                {
                    // Skip
                }
                // Default handling
                else
                {
                    if (inVerdict)
                    {
                        ForensicVerdicts.Add(ForensicVerdictEntry.Verdict(trimmedLine));
                    }
                    else
                    {
                        ForensicVerdicts.Add(ForensicVerdictEntry.Bullet(trimmedLine));
                    }
                }
            }
        }

        /// <summary>
        /// Gets the color for a severity level for visual display.
        /// </summary>
        public static string GetSeverityColor(StressSeverity severity)
        {
            return severity switch
            {
                StressSeverity.Healthy => "#22dd22",
                StressSeverity.Warning => "#ffcc00",
                StressSeverity.Critical => "#ff3333",
                _ => "#666666"
            };
        }

        /// <summary>
        /// Phase 6: Executes the rescue track application.
        /// Delegates to parent ViewModel (DJCompanionViewModel) which orchestrates the full flow.
        /// </summary>
        private async Task<ApplyRescueResult> ExecuteApplyRescueTrackAsync()
        {
            if (SelectedStressPoint == null || SelectedRescue == null)
            {
                return new ApplyRescueResult
                {
                    Success = false,
                    Message = "No rescue suggestion selected."
                };
            }

            try
            {
                IsApplying = true;

                // Call the handler (wired from DJCompanionViewModel)
                if (OnApplyRescueTrack != null)
                {
                    var result = await OnApplyRescueTrack(
                        SelectedStressPoint.FromTrackIndex,
                        SelectedRescue);
                    return result;
                }
                else
                {
                    return new ApplyRescueResult
                    {
                        Success = false,
                        Message = "Rescue handler not configured."
                    };
                }
            }
            finally
            {
                IsApplying = false;
            }
        }
    }
}
