using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ReactiveUI;
using SLSKDONET.Models.Studio;
using SLSKDONET.Services.Tagging;

namespace SLSKDONET.ViewModels.Studio;

public class StudioTagExportViewModel : ReactiveObject, IStudioModuleViewModel, IDisposable
{
    private readonly TagTemplateEngine _templateEngine;
    private readonly Id3MasteringService _id3Service;
    private IDisplayableTrack? _currentTrack;
    public IDisplayableTrack? CurrentTrack
    {
        get => _currentTrack;
        set => this.RaiseAndSetIfChanged(ref _currentTrack, value);
    }

    private string _titleTemplate = "[{CamelotKey}] {Title}";
    public string TitleTemplate
    {
        get => _titleTemplate;
        set 
        {
            this.RaiseAndSetIfChanged(ref _titleTemplate, value);
            UpdatePreview();
        }
    }

    private string _commentsTemplate = "Energy: {EnergyLevel} | Orbit AI";
    public string CommentsTemplate
    {
        get => _commentsTemplate;
        set 
        {
            this.RaiseAndSetIfChanged(ref _commentsTemplate, value);
            UpdatePreview();
        }
    }

    private string _previewTitle = "—";
    public string PreviewTitle
    {
        get => _previewTitle;
        set => this.RaiseAndSetIfChanged(ref _previewTitle, value);
    }

    private string _previewComments = "—";
    public string PreviewComments
    {
        get => _previewComments;
        set => this.RaiseAndSetIfChanged(ref _previewComments, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    public ICommand WriteTagsCommand { get; }

    public StudioTagExportViewModel(TagTemplateEngine templateEngine, Id3MasteringService id3Service)
    {
        _templateEngine = templateEngine ?? throw new ArgumentNullException(nameof(templateEngine));
        _id3Service = id3Service ?? throw new ArgumentNullException(nameof(id3Service));

        WriteTagsCommand = ReactiveCommand.CreateFromTask(OnWriteTagsAsync, 
            this.WhenAnyValue(x => x.IsBusy, x => x.CurrentTrack, (busy, track) => !busy && track != null));
    }

    private void UpdatePreview()
    {
        if (_currentTrack == null)
        {
            PreviewTitle = "—";
            PreviewComments = "—";
            return;
        }

        PreviewTitle = _templateEngine.FormatString(TitleTemplate, _currentTrack);
        PreviewComments = _templateEngine.FormatString(CommentsTemplate, _currentTrack);
    }

    private async Task OnWriteTagsAsync()
    {
        if (_currentTrack == null) return;

        IsBusy = true;
        try
        {
            var settings = new TagTemplateSettings
            {
                TitleTemplate = TitleTemplate,
                CommentsTemplate = CommentsTemplate,
                UpdateBpmField = true,
                UpdateKeyField = true
            };

            await _id3Service.WriteTagsAsync(_currentTrack, settings);
            
            // Show success in UI (optional: update status text)
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to export tags from Studio Inspector");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task LoadTrackContextAsync(IDisplayableTrack track, CancellationToken cancellationToken)
    {
        CurrentTrack = track;
        UpdatePreview();
        return Task.CompletedTask;
    }

    public void ClearContext()
    {
        CurrentTrack = null;
        UpdatePreview();
    }

    public void Dispose()
    {
        // Currently no specific resources to dispose, but added for IStudioModuleViewModel consistency
    }
}
