using ReactiveUI;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using System.Collections.ObjectModel;

namespace SLSKDONET.ViewModels.Stem;

/// <summary>
/// Manages the stem separation workspace including track loading, mixing, and project management.
/// </summary>
public class StemWorkspaceViewModel : ReactiveObject
{
    private readonly StemSeparationService _separationService;
    private readonly RealTimeStemEngine _audioEngine;
    private readonly ILibraryService _libraryService; 
    private readonly WaveformAnalysisService _waveformAnalysisService;
    private readonly StemProjectService _projectService;
    private readonly IDialogService _dialogService;

    public StemMixerViewModel Mixer { get; }
    
    private bool _isVisible;
    public bool IsVisible
    {
        get => _isVisible;
        set => this.RaiseAndSetIfChanged(ref _isVisible, value);
    }

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        set => this.RaiseAndSetIfChanged(ref _isPlaying, value);
    }

    private string _currentTrackDisplay = "No track loaded";
    public string CurrentTrackDisplay
    {
        get => _currentTrackDisplay;
        set => this.RaiseAndSetIfChanged(ref _currentTrackDisplay, value);
    }

    public string PlayButtonLabel => IsPlaying ? "⏸ Pause" : "▶ Play";

    public ObservableCollection<SeparationHistoryItem> SeparationHistory { get; } = new();

    private SeparationHistoryItem? _selectedHistoryItem;
    public SeparationHistoryItem? SelectedHistoryItem
    {
        get => _selectedHistoryItem;
        set
        {
            if (this.RaiseAndSetIfChanged(ref _selectedHistoryItem, value) && value != null)
            {
                // Reload the selected historical track
                _ = LoadTrackAsync(value.TrackGlobalId);
            }
        }
    }

    public ObservableCollection<Models.Stem.StemEditProject> SavedProjects { get; } = new();

    private string _currentTrackId = string.Empty;

    public System.Windows.Input.ICommand CloseCommand { get; }
    public System.Windows.Input.ICommand SaveProjectCommand { get; }
    public System.Windows.Input.ICommand LoadProjectCommand { get; }
    public System.Windows.Input.ICommand TogglePlayCommand { get; }

    public StemWorkspaceViewModel(
        StemSeparationService separationService,
        ILibraryService libraryService,
        WaveformAnalysisService waveformAnalysisService,
        StemProjectService projectService,
        IDialogService dialogService,
        RealTimeStemEngine audioEngine)
    {
        _separationService = separationService;
        _libraryService = libraryService;
        _waveformAnalysisService = waveformAnalysisService;
        _projectService = projectService;
        _dialogService = dialogService;
        _audioEngine = audioEngine;
        
        Mixer = new StemMixerViewModel(_audioEngine);
        
        CloseCommand = ReactiveCommand.Create(() => IsVisible = false);
        SaveProjectCommand = ReactiveCommand.CreateFromTask(SaveProjectAsync);
        LoadProjectCommand = ReactiveCommand.CreateFromTask<Models.Stem.StemEditProject>(LoadProjectAsync);
        TogglePlayCommand = ReactiveCommand.Create(TogglePlay);
    }
    
    /// <summary>
    /// Loads a track for stem separation.
    /// </summary>
    public async Task LoadTrackAsync(string trackGlobalId)
    {
        _currentTrackId = trackGlobalId;
        
        try
        {
            // 1. Resolve track info from library
            var entry = await _libraryService.FindLibraryEntryAsync(trackGlobalId);
            if (entry == null)
            {
                await _dialogService.ShowAlertAsync("Error", "Track not found in library.");
                return;
            }

            string filePath = entry.FilePath ?? "";
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
            {
                await _dialogService.ShowAlertAsync("Error", "Track file not found.");
                return;
            }

            // Update display
            CurrentTrackDisplay = $"{entry.Artist} - {entry.Title}";

            // 2. Trigger Separation
            var dict = await _separationService.SeparateTrackAsync(filePath, trackGlobalId);
            
            // 3. Load into audio engine
            _audioEngine.LoadStems(dict);
            
            // 4. Populate mixer channels
            Mixer.Channels.Clear();
            foreach (var stem in dict)
            {
                var settings = new Models.Stem.StemSettings { Volume = 0.8f }; 
                var channel = new StemChannelViewModel(stem.Key, settings, _audioEngine);
                
                // Generate waveform asynchronously (fire and forget)
                _ = GenerateWaveformAsync(channel, stem.Value);
                
                Mixer.Channels.Add(channel);
            }
            
            // 5. Add to history
            var historyItem = new SeparationHistoryItem
            {
                TrackGlobalId = trackGlobalId,
                TrackName = entry.Title,
                Artist = entry.Artist,
                SeparatedDate = DateTime.UtcNow
            };
            
            // Remove if already exists, then add to top
            SeparationHistory.RemoveAll(h => h.TrackGlobalId == trackGlobalId);
            SeparationHistory.Insert(0, historyItem);

            // 6. Load saved projects for this track
            await LoadProjectsForTrackAsync(trackGlobalId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading track: {ex.Message}");
            await _dialogService.ShowAlertAsync("Error", $"Failed to load track: {ex.Message}");
            CurrentTrackDisplay = "Error loading track";
        }
    }

    private async Task GenerateWaveformAsync(StemChannelViewModel channel, string stemFilePath)
    {
        try
        {
            var waveform = await _waveformAnalysisService.GenerateWaveformAsync(stemFilePath, System.Threading.CancellationToken.None);
            channel.WaveformData = waveform;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to generate waveform for {channel.Name}: {ex.Message}");
        }
    }

    private async Task LoadProjectsForTrackAsync(string trackGlobalId)
    {
        try
        {
            SavedProjects.Clear();
            var projects = await _projectService.GetProjectsForTrackAsync(trackGlobalId);
            foreach (var p in projects)
            {
                SavedProjects.Add(p);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading projects: {ex.Message}");
        }
    }

    public async Task LoadProjectAsync(Models.Stem.StemEditProject project)
    {
        try
        {
            // Restore mixer settings
            foreach (var channel in Mixer.Channels)
            {
                if (project.CurrentSettings.TryGetValue(channel.Type, out var setting))
                {
                    channel.Volume = setting.Volume;
                    channel.Pan = setting.Pan;
                    channel.IsMuted = setting.IsMuted;
                    channel.IsSolo = setting.IsSolo;
                }
            }
            await _dialogService.ShowAlertAsync("Success", $"Restored mix '{project.Name}'.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading project: {ex.Message}");
            await _dialogService.ShowAlertAsync("Error", $"Failed to load project: {ex.Message}");
        }
    }

    private void TogglePlay()
    {
        if (IsPlaying)
        {
            _audioEngine.Pause();
            IsPlaying = false;
        }
        else
        {
            _audioEngine.Play();
            IsPlaying = true;
        }
        this.RaisePropertyChanged(nameof(PlayButtonLabel));
    }
    
    private async Task SaveProjectAsync()
    {
        if (string.IsNullOrEmpty(_currentTrackId))
        {
            await _dialogService.ShowAlertAsync("No track loaded", "Please load a track before saving a project.");
            return;
        }

        var defaultName = $"Mix {DateTime.Now:yyyy-MM-dd HHmm}";
        var name = await _dialogService.ShowPromptAsync("Save Mix", "Enter a name for this mix:", defaultName);
        
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var project = new Models.Stem.StemEditProject
            {
                OriginalTrackId = _currentTrackId,
                Name = name,
                Created = DateTime.UtcNow,
                Modified = DateTime.UtcNow
            };
            
            // Capture mixer state
            foreach (var channel in Mixer.Channels)
            {
                var settings = new Models.Stem.StemSettings
                {
                    Volume = channel.Volume,
                    Pan = channel.Pan,
                    IsMuted = channel.IsMuted,
                    IsSolo = channel.IsSolo
                };
                project.CurrentSettings[channel.Type] = settings;
            }
            
            await _projectService.SaveProjectAsync(project);
            
            // Refresh project list
            await LoadProjectsForTrackAsync(_currentTrackId);
            
            await _dialogService.ShowAlertAsync("Success", $"Mix '{project.Name}' saved successfully.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error saving project: {ex.Message}");
            await _dialogService.ShowAlertAsync("Error", $"Failed to save mix: {ex.Message}");
        }
    }
}

/// <summary>
/// Represents a track in the separation history.
/// </summary>
public class SeparationHistoryItem
{
    public string TrackGlobalId { get; set; } = "";
    public string TrackName { get; set; } = "";
    public string Artist { get; set; } = "";
    public DateTime SeparatedDate { get; set; } = DateTime.UtcNow;
}
