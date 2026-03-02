using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using SLSKDONET.Models;
using SLSKDONET.Features.LibrarySidebar;
using SLSKDONET.Services.Tagging;
using SLSKDONET.Models.Studio;
using System.Threading;

namespace SLSKDONET.Features.LibrarySidebar.ViewModels;

public class MetadataSidebarViewModel : ReactiveObject, ISidebarContent
{
    private readonly ILibraryService _libraryService;
    private readonly ITaggerService _taggerService;
    private readonly ISpotifyMetadataService _spotifyService;
    private readonly Id3MasteringService _id3MasteringService;
    private CancellationTokenSource? _loadingCts;
    
    private PlaylistTrackViewModel? _activeTrack;
    private bool _isLoading;
    private ObservableCollection<MetadataFieldViewModel> _fields = new();

    public ObservableCollection<MetadataFieldViewModel> Fields => _fields;

    public bool IsLoading
    {
        get => _isLoading;
        set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    public ReactiveCommand<Unit, Unit> SaveToDbCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveToFileCommand { get; }
    public ReactiveCommand<Unit, Unit> SyncSpotifyCommand { get; }

    public MetadataSidebarViewModel(
        ILibraryService libraryService,
        ITaggerService taggerService,
        ISpotifyMetadataService spotifyService,
        Id3MasteringService id3MasteringService)
    {
        _libraryService = libraryService;
        _taggerService = taggerService;
        _spotifyService = spotifyService;
        _id3MasteringService = id3MasteringService;

        SaveToDbCommand = ReactiveCommand.CreateFromTask(SaveToDbAsync);
        SaveToFileCommand = ReactiveCommand.CreateFromTask(SaveToFileAsync);
        SyncSpotifyCommand = ReactiveCommand.CreateFromTask(SyncSpotifyAsync);
    }

    public async Task ActivateAsync(PlaylistTrackViewModel track)
    {
        _loadingCts?.Cancel();
        _loadingCts?.Dispose();
        _loadingCts = new CancellationTokenSource();
        var token = _loadingCts.Token;

        _activeTrack = track;
        IsLoading = true;
        Fields.Clear();

        try
        {
            var dbTrack = track.Model;
            token.ThrowIfCancellationRequested();
            // 1. Core Metadata (Editable)
            var fields = new List<MetadataFieldViewModel>
            {
                new("Artist", dbTrack.Artist) { DatabaseValue = dbTrack.Artist },
                new("Title", dbTrack.Title) { DatabaseValue = dbTrack.Title },
                new("Album", dbTrack.Album) { DatabaseValue = dbTrack.Album },
                new("Year", dbTrack.ReleaseDate?.Year.ToString() ?? "") { DatabaseValue = dbTrack.ReleaseDate?.Year.ToString() },
                new("Label", dbTrack.Label) { DatabaseValue = dbTrack.Label },
                new("Bpm", dbTrack.BPM?.ToString("F1")) { DatabaseValue = dbTrack.BPM?.ToString("F1") },
                new("Key", dbTrack.MusicalKey) { DatabaseValue = dbTrack.MusicalKey },
                new("Genre", dbTrack.Genres) { DatabaseValue = dbTrack.Genres },
                new("Comment", dbTrack.Comments) { DatabaseValue = dbTrack.Comments }
            };

            // 2. Technical Metadata (Read-only)
            var technicalFields = new List<MetadataFieldViewModel>
            {
                new("Path", dbTrack.ResolvedFilePath) { DatabaseValue = dbTrack.ResolvedFilePath, IsReadOnly = true },
                new("Bitrate", dbTrack.Bitrate?.ToString() + " kbps") { DatabaseValue = dbTrack.Bitrate?.ToString() + " kbps", IsReadOnly = true },
                new("Format", dbTrack.Format?.ToUpper()) { DatabaseValue = dbTrack.Format?.ToUpper(), IsReadOnly = true },
                new("Duration", FormatDuration(dbTrack.CanonicalDuration ?? 0)) { DatabaseValue = FormatDuration(dbTrack.CanonicalDuration ?? 0), IsReadOnly = true },
                new("Size", FormatSize(0)) { DatabaseValue = "Checking file...", IsReadOnly = true } // Size often comes from file
            };

            foreach (var f in fields) Fields.Add(f);
            foreach (var f in technicalFields) Fields.Add(f);

            // 3. Load File Tags concurrently
            if (!string.IsNullOrEmpty(track.Model.ResolvedFilePath))
            {
                var fileTags = await _taggerService.ReadTagsAsync(track.Model.ResolvedFilePath);
                token.ThrowIfCancellationRequested();

                if (fileTags != null)
                {
                    UpdateField("Artist", f => f.FileValue = fileTags.Artist);
                    UpdateField("Title", f => f.FileValue = fileTags.Title);
                    UpdateField("Album", f => f.FileValue = fileTags.Album);
                    UpdateField("Year", f => f.FileValue = fileTags.Metadata?.ContainsKey("Year") == true ? fileTags.Metadata["Year"]?.ToString() : "");
                    UpdateField("Label", f => f.FileValue = fileTags.Label);
                    UpdateField("Bpm", f => f.FileValue = fileTags.BPM?.ToString("F1"));
                    UpdateField("Key", f => f.FileValue = fileTags.MusicalKey);
                    UpdateField("Genre", f => f.FileValue = fileTags.Metadata?.ContainsKey("Genres") == true ? string.Join(", ", (string[])fileTags.Metadata["Genres"]) : "");
                    UpdateField("Comment", f => f.FileValue = fileTags.Metadata?.ContainsKey("Comment") == true ? fileTags.Metadata["Comment"]?.ToString() : "");
                    
                    // Technical updates from file
                    UpdateField("Bitrate", f => f.FileValue = fileTags.Bitrate.ToString() + " kbps");
                    UpdateField("Format", f => f.FileValue = fileTags.Format?.ToUpper());
                    UpdateField("Duration", f => f.FileValue = FormatDuration(fileTags.Length * 1000 ?? 0));
                    UpdateField("Size", f => {
                        f.FileValue = FormatSize(fileTags.Size ?? 0);
                        f.DatabaseValue = FormatSize(fileTags.Size ?? 0); // Local DB size might be empty, so use file size as ref
                    });

                    // Extra technicals if available in metadata dictionary
                    if (fileTags.Metadata != null)
                    {
                        if (fileTags.Metadata.TryGetValue("SampleRate", out var sr))
                            Fields.Add(new MetadataFieldViewModel("Sample Rate", sr.ToString() + " Hz") { FileValue = sr.ToString() + " Hz", IsReadOnly = true });
                        if (fileTags.Metadata.TryGetValue("Channels", out var ch))
                            Fields.Add(new MetadataFieldViewModel("Channels", ch.ToString()) { FileValue = ch.ToString(), IsReadOnly = true });
                    }
                }
            }

            // 4. Load Spotify Data
            var spotifyTrack = await _spotifyService.FindTrackAsync(dbTrack.Artist, dbTrack.Title);
            token.ThrowIfCancellationRequested();

            if (spotifyTrack != null)
            {
                UpdateField("Artist", f => f.SpotifyValue = spotifyTrack.Artists.FirstOrDefault()?.Name);
                UpdateField("Title", f => f.SpotifyValue = spotifyTrack.Name);
                UpdateField("Album", f => f.SpotifyValue = spotifyTrack.Album.Name);
                UpdateField("Year", f => f.SpotifyValue = spotifyTrack.Album.ReleaseDate);
            }
        }
        catch (OperationCanceledException) { /* Discarded */ }
        catch (Exception ex)
        {
             Serilog.Log.Error(ex, "Error activating Metadata Sidebar");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private string FormatDuration(int ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
    }

    private string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] suf = { "B", "KB", "MB", "GB", "TB" };
        int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
        double num = Math.Round(bytes / Math.Pow(1024, place), 1);
        return num.ToString() + " " + suf[place];
    }

    private void UpdateField(string name, Action<MetadataFieldViewModel> update)
    {
        var field = Fields.FirstOrDefault(f => f.FieldName == name);
        if (field != null) update(field);
    }

    private async Task SaveToDbAsync()
    {
        if (_activeTrack == null) return;

        foreach (var field in Fields.Where(f => f.IsModified))
        {
            ApplyFieldToModel(_activeTrack.Model, field);
        }

        await _libraryService.SavePlaylistTrackAsync(_activeTrack.Model);
        
        // Refresh values
        foreach (var field in Fields)
        {
            field.DatabaseValue = field.EditValue;
        }
    }

    private async Task SaveToFileAsync()
    {
        if (_activeTrack == null || string.IsNullOrEmpty(_activeTrack.Model.ResolvedFilePath)) return;

        // First save to DB to ensure we have the latest
        await SaveToDbAsync();

        // Then write tags using the high-performance Atomic Master Engine
        var settings = new TagTemplateSettings
        {
            CommentsTemplate = "{Comments} | ORBIT MASTERED"
        };

        // Use the ViewModel directly as it implements IDisplayableTrack for consistent property mapping
        await _id3MasteringService.WriteTagsAsync(_activeTrack, settings);
        
        // Refresh file values in UI
        foreach (var field in Fields)
        {
            field.FileValue = field.DatabaseValue;
        }
    }

    private async Task SyncSpotifyAsync()
    {
        foreach (var field in Fields.Where(f => !string.IsNullOrEmpty(f.SpotifyValue)))
        {
            field.EditValue = field.SpotifyValue;
        }
    }

    private void ApplyFieldToModel(PlaylistTrack model, MetadataFieldViewModel field)
    {
        switch (field.FieldName)
        {
            case "Artist": model.Artist = field.EditValue ?? ""; break;
            case "Title": model.Title = field.EditValue ?? ""; break;
            case "Album": model.Album = field.EditValue ?? ""; break;
            case "Label": model.Label = field.EditValue; break;
            case "Year": 
                if (DateTime.TryParse(field.EditValue, out var dt)) model.ReleaseDate = dt;
                break;
            case "Genre": model.Genres = field.EditValue; break;
            case "Comment": model.Comments = field.EditValue; break;
            case "Key":
            case "MusicalKey": model.MusicalKey = field.EditValue; break;
            case "Bpm": 
            case "BPM": 
                if (double.TryParse(field.EditValue, out var bpm)) model.BPM = bpm;
                break;
        }
    }

    public async Task ActivateBulkAsync(IReadOnlyList<PlaylistTrackViewModel> tracks)
    {
        await Task.CompletedTask;
    }

    public void Deactivate()
    {
        _loadingCts?.Cancel();
        _loadingCts?.Dispose();
        _loadingCts = null;
        _activeTrack = null;
        Fields.Clear();
    }
}
