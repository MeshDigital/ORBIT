using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Export;
using SLSKDONET.Services.Library;
using SLSKDONET.Models;
using SLSKDONET.Views;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels
{
    public class ExportManagerViewModel : INotifyPropertyChanged
    {
        private readonly ILogger<ExportManagerViewModel> _logger;
        private readonly IRekordboxExportService _exportService;
        private readonly SetListService _setListService;
        private readonly IDialogService _dialogService;
        private readonly IGigBagService _gigBagService;
        private readonly SLSKDONET.Services.Export.RekordboxXmlExporter _rekordboxExporter;
        private readonly ILibraryService _libraryService;
        private readonly IFileInteractionService _fileService;
        private readonly INotificationService _notificationService;

        public event PropertyChangedEventHandler? PropertyChanged;

        private SetListEntity? _selectedSet;
        public SetListEntity? SelectedSet
        {
            get => _selectedSet;
            set
            {
                if (SetProperty(ref _selectedSet, value))
                {
                    OnSelectedSetChanged(value);
                }
            }
        }

        private string _destinationPath = string.Empty;
        public string DestinationPath
        {
            get => _destinationPath;
            set
            {
                if (SetProperty(ref _destinationPath, value))
                {
                    ((AsyncRelayCommand)ExportCommand).RaiseCanExecuteChanged();
                }
            }
        }

        private double _progress;
        public double Progress
        {
            get => _progress;
            set => SetProperty(ref _progress, value);
        }

        private string _statusMessage = "Ready to export";
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        private bool _isExporting;
        public bool IsExporting
        {
            get => _isExporting;
            set
            {
                if (SetProperty(ref _isExporting, value))
                {
                    ((AsyncRelayCommand)ExportCommand).RaiseCanExecuteChanged();
                    ((AsyncRelayCommand)ValidateCommand).RaiseCanExecuteChanged();
                }
            }
        }

        private bool _isValidated;
        public bool IsValidated
        {
            get => _isValidated;
            set => SetProperty(ref _isValidated, value);
        }

        private ExportIntent _selectedIntent = ExportIntent.ClubReady;
        public ExportIntent SelectedIntent
        {
            get => _selectedIntent;
            set
            {
                if (SetProperty(ref _selectedIntent, value))
                {
                    Task.Run(() => UpdatePreviewAsync());
                }
            }
        }

        private ExportPreviewModel? _preview;
        public ExportPreviewModel? Preview
        {
            get => _preview;
            set => SetProperty(ref _preview, value);
        }

        public IEnumerable<ExportIntent> AvailableIntents => Enum.GetValues(typeof(ExportIntent)).Cast<ExportIntent>();

        public ObservableCollection<SetListEntity> AvailableSets { get; } = new();
        public ObservableCollection<string> ValidationErrors { get; } = new();
        
        /// <summary>
        /// Command Center Pipeline Steps - visible during export.
        /// </summary>
        public ObservableCollection<PipelineStepViewModel> ProgressSteps { get; } = new();
        
        private bool _showPipeline;
        /// <summary>
        /// Controls visibility of the pipeline steps UI.
        /// </summary>
        public bool ShowPipeline
        {
            get => _showPipeline;
            set => SetProperty(ref _showPipeline, value);
        }

        // Gig Bag Status (checkmarks)
        private bool _isMainUsbComplete;
        public bool IsMainUsbComplete { get => _isMainUsbComplete; set => SetProperty(ref _isMainUsbComplete, value); }

        private bool _isBackupComplete;
        public bool IsBackupComplete { get => _isBackupComplete; set => SetProperty(ref _isBackupComplete, value); }

        private bool _isEmergencyCardComplete;
        public bool IsEmergencyCardComplete { get => _isEmergencyCardComplete; set => SetProperty(ref _isEmergencyCardComplete, value); }

        private bool _isAutopsyComplete;
        public bool IsAutopsyComplete { get => _isAutopsyComplete; set => SetProperty(ref _isAutopsyComplete, value); }

        public AsyncRelayCommand LoadSetsCommand { get; }
        public AsyncRelayCommand BrowseCommand { get; }
        public AsyncRelayCommand ExportCommand { get; }
        public AsyncRelayCommand ValidateCommand { get; }
        public AsyncRelayCommand UpdatePreviewCommand { get; }
        public AsyncRelayCommand ExportToRekordboxCommand { get; }

        public ExportManagerViewModel(
            ILogger<ExportManagerViewModel> logger,
            IRekordboxExportService exportService,
            SetListService setListService,
            IDialogService dialogService,
            IGigBagService gigBagService,
            SLSKDONET.Services.Export.RekordboxXmlExporter rekordboxExporter,
            ILibraryService libraryService,
            IFileInteractionService fileService,
            INotificationService notificationService)
        {
            _logger = logger;
            _exportService = exportService;
            _setListService = setListService;
            _dialogService = dialogService;
            _gigBagService = gigBagService;
            _rekordboxExporter = rekordboxExporter;
            _libraryService = libraryService;
            _fileService = fileService;
            _notificationService = notificationService;

            LoadSetsCommand = new AsyncRelayCommand(LoadSetsAsync);
            BrowseCommand = new AsyncRelayCommand(BrowseAsync);
            ExportCommand = new AsyncRelayCommand(ExportAsync, () => SelectedSet != null && !string.IsNullOrEmpty(DestinationPath) && !IsExporting);
            ValidateCommand = new AsyncRelayCommand(ValidateAsync, () => SelectedSet != null && !IsExporting);
            UpdatePreviewCommand = new AsyncRelayCommand(UpdatePreviewAsync);
            ExportToRekordboxCommand = new AsyncRelayCommand(ExecuteExportToRekordboxAsync, () => !IsExporting);
        }

        private async Task ExecuteExportToRekordboxAsync()
        {
            IsExporting = true;
            try
            {
                var path = await _fileService.SaveFileDialogAsync("Rekordbox Export", "orbit_library.xml", "xml");
                if (string.IsNullOrEmpty(path)) return;

                StatusMessage = "Fetching library tracks...";
                
                var libraryEntries = await _libraryService.LoadAllLibraryEntriesAsync();
                var tracks = libraryEntries.Select(e => new SLSKDONET.Data.TrackEntity
                {
                    GlobalId = e.Id.ToString(),
                    Title = e.Title,
                    Artist = e.Artist,
                    Size = 0,
                    CanonicalDuration = e.DurationSeconds,
                    Bitrate = e.Bitrate,
                    BPM = e.BPM,
                    MusicalKey = e.MusicalKey,
                    Filename = e.FilePath
                }).ToList();

                StatusMessage = "Generating Rekordbox XML...";
                await _rekordboxExporter.ExportToXmlAsync(tracks, path);

                _notificationService.Show("Export Complete", $"Successfully exported {tracks.Count} tracks to Rekordbox XML.", SLSKDONET.Views.NotificationType.Success);
                StatusMessage = "✓ Rekordbox XML generated successfully";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to export Rekordbox XML");
                _notificationService.Show("Export Failed", "Failed to generate Rekordbox XML.", SLSKDONET.Views.NotificationType.Error);
                StatusMessage = "Export failed.";
            }
            finally
            {
                IsExporting = false;
            }
        }

        private async Task LoadSetsAsync()
        {
            try
            {
                var sets = await _setListService.GetAllSetListsAsync();
                AvailableSets.Clear();
                foreach (var set in sets)
                {
                    AvailableSets.Add(set);
                }
                
                if (AvailableSets.Any() && SelectedSet == null)
                {
                    SelectedSet = AvailableSets.First();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load sets for export");
                StatusMessage = "Error loading sets";
            }
        }

        private async Task BrowseAsync()
        {
            var result = await _dialogService.OpenFolderDialogAsync("Select Export Destination");
            if (!string.IsNullOrEmpty(result))
            {
                DestinationPath = result;
            }
        }

        private async Task UpdatePreviewAsync()
        {
            if (SelectedSet == null) return;
            try 
            {
                Preview = await _exportService.GetExportPreviewAsync(SelectedSet);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update export preview");
            }
        }

        private async Task ValidateAsync()
        {
            if (SelectedSet == null) return;

            IsExporting = true;
            StatusMessage = "Validating set...";
            ValidationErrors.Clear();

            try
            {
                var validation = await _exportService.ValidateSetAsync(SelectedSet);
                IsValidated = validation.IsValid;
                
                if (!validation.IsValid)
                {
                    foreach (var error in validation.Errors)
                    {
                        ValidationErrors.Add(error);
                    }
                    StatusMessage = "Validation failed. See errors below.";
                }
                else
                {
                    StatusMessage = "Set is valid and ready for export.";
                    await UpdatePreviewAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Validation failed");
                StatusMessage = "Validation error: " + ex.Message;
            }
            finally
            {
                IsExporting = false;
            }
        }

        private async Task ExportAsync()
        {
            if (SelectedSet == null || string.IsNullOrEmpty(DestinationPath)) return;

            IsExporting = true;
            Progress = 0;
            StatusMessage = "Initializing export...";
            ValidationErrors.Clear();

            // Reset Gig Bag status
            IsMainUsbComplete = false;
            IsBackupComplete = false;
            IsEmergencyCardComplete = false;
            IsAutopsyComplete = false;
            
            // Initialize Command Center Pipeline
            InitializePipelineSteps();
            ShowPipeline = true;

            try
            {
                var options = _exportService.GetOptionsFromIntent(SelectedIntent);
                
                var progressReporter = new Progress<ExportProgressStep>(step => 
                {
                    StatusMessage = step.Message;
                    Progress = step.Percentage;
                    
                    // Update Command Center step status
                    UpdatePipelineStep(step.StepIndex, step.IsComplete);
                });

                // Step 1: Main USB Export
                var result = await _exportService.ExportSetAsync(SelectedSet, DestinationPath, options, progressReporter);

                if (result.Success)
                {
                    IsMainUsbComplete = true;
                    StatusMessage = "✓ Main USB complete. Creating Gig Bag...";

                    // Step 2: Create Gig Bag (Backup, Emergency Card, Autopsy)
                    var gigBagOptions = new GigBagOptions
                    {
                        IncludeBackup = true,
                        IncludeEmergencyCard = true,
                        IncludeAutopsy = true
                    };

                    var gigBagResult = await _gigBagService.CreateGigBagAsync(SelectedSet, DestinationPath, gigBagOptions);

                    IsBackupComplete = gigBagResult.BackupComplete;
                    IsEmergencyCardComplete = gigBagResult.EmergencyCardComplete;
                    IsAutopsyComplete = gigBagResult.AutopsyComplete;

                    // Final Status
                    if (gigBagResult.AllComplete)
                    {
                        StatusMessage = "🎒 Gig Bag complete! You're club-ready.";
                        Progress = 100;
                        await _dialogService.ShowAlertAsync("Export Success", 
                            $"Set '{SelectedSet.Name}' exported with:\n✓ Main USB\n✓ Backup\n✓ Emergency Card\n✓ Set Autopsy");
                    }
                    else
                    {
                        StatusMessage = "⚠ Export complete with warnings. See errors.";
                        foreach (var error in gigBagResult.Errors)
                        {
                            ValidationErrors.Add(error);
                        }
                    }
                }
                else
                {
                    StatusMessage = "Export failed.";
                    foreach (var error in result.Errors)
                    {
                        ValidationErrors.Add(error);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Export failed");
                StatusMessage = "Critical Error: " + ex.Message;
            }
            finally
            {
                IsExporting = false;
            }
        }

        private void OnSelectedSetChanged(SetListEntity? value)
        {
            IsValidated = false;
            ValidationErrors.Clear();
            Preview = null;
            StatusMessage = value != null ? "Ready to validate" : "Select a set";
            ((AsyncRelayCommand)ExportCommand).RaiseCanExecuteChanged();
            ((AsyncRelayCommand)ValidateCommand).RaiseCanExecuteChanged();
            
            if (value != null)
            {
                Task.Run(() => UpdatePreviewAsync());
            }
        }

        /// <summary>
        /// Initializes the Command Center pipeline with DJ-facing step names.
        /// </summary>
        private void InitializePipelineSteps()
        {
            ProgressSteps.Clear();
            
            // Authoritative, DJ-grade copy for each step
            // The Emergency Card is the "lifeboat" — it gets its own visible slot
            var steps = new[]
            {
                "Securing Metadata...",
                "Optimizing Waveform Data...",
                "Mapping ORBIT Cues...",
                "Checking BPM Stability...",
                "Finalizing Rekordbox XML...",
                "Deploying to USB...",
                "Verifying Data Integrity...",
                "Creating Backup USB...",
                "Generating Emergency Card...",   // The lifeboat
                "Writing Set Autopsy..."
            };
            
            for (int i = 0; i < steps.Length; i++)
            {
                ProgressSteps.Add(PipelineStepViewModel.Create(i, steps[i]));
            }
        }

        /// <summary>
        /// Updates a pipeline step's status based on export progress.
        /// </summary>
        private void UpdatePipelineStep(int stepIndex, bool isComplete)
        {
            // Mark all previous steps as complete
            for (int i = 0; i < stepIndex && i < ProgressSteps.Count; i++)
            {
                if (ProgressSteps[i].Status != StepStatus.Complete)
                {
                    ProgressSteps[i].Status = StepStatus.Complete;
                }
            }
            
            // Update current step
            if (stepIndex >= 0 && stepIndex < ProgressSteps.Count)
            {
                ProgressSteps[stepIndex].Status = isComplete ? StepStatus.Complete : StepStatus.Active;
            }
        }

        /// <summary>
        /// Marks all pipeline steps as complete (for success state).
        /// </summary>
        private void CompletePipeline()
        {
            foreach (var step in ProgressSteps)
            {
                step.Status = StepStatus.Complete;
            }
        }


        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}

