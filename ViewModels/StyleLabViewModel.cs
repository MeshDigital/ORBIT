using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services; // For IEventBus
using SLSKDONET.Services.AI;

namespace SLSKDONET.ViewModels;

public class StyleLabViewModel : ReactiveObject
{
    private readonly IStyleClassifierService _classifier;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<StyleLabViewModel> _logger;
    private readonly IEventBus _eventBus;

    public ObservableCollection<StyleDefinitionEntity> Styles { get; } = new();
    public ObservableCollection<PlaylistTrackViewModel> ReferenceTracks { get; } = new();

    private StyleDefinitionEntity? _selectedStyle;
    public StyleDefinitionEntity? SelectedStyle
    {
        get => _selectedStyle;
        set 
        {
            this.RaiseAndSetIfChanged(ref _selectedStyle, value);
            _ = LoadReferenceTracksAsync();
        }
    }

    private string _newStyleName = string.Empty;
    public string NewStyleName
    {
        get => _newStyleName;
        set => this.RaiseAndSetIfChanged(ref _newStyleName, value);
    }
    
    private bool _isModelOutdated;
    public bool IsModelOutdated
    {
        get => _isModelOutdated;
        set => this.RaiseAndSetIfChanged(ref _isModelOutdated, value);
    }

    public ReactiveCommand<Unit, Unit> CreateStyleCommand { get; }
    public ReactiveCommand<StyleDefinitionEntity, Unit> DeleteStyleCommand { get; }
    public ReactiveCommand<StyleDefinitionEntity, Unit> TrainStyleCommand { get; }
    public ReactiveCommand<Unit, Unit> TrainGlobalModelCommand { get; }
    public ReactiveCommand<Unit, Unit> ScanLibraryCommand { get; }
    public ReactiveCommand<string, Unit> AddTrackToStyleCommand { get; }
    public ReactiveCommand<string, Unit> RemoveTrackFromStyleCommand { get; }

    public StyleLabViewModel(
        IStyleClassifierService classifier,
        IDbContextFactory<AppDbContext> dbFactory,
        ILogger<StyleLabViewModel> logger,
        IEventBus eventBus)
    {
        _classifier = classifier;
        _dbFactory = dbFactory;
        _logger = logger;
        _eventBus = eventBus;

        CreateStyleCommand = ReactiveCommand.CreateFromTask(CreateStyleAsync);
        DeleteStyleCommand = ReactiveCommand.CreateFromTask<StyleDefinitionEntity>(DeleteStyleAsync);
        TrainStyleCommand = ReactiveCommand.CreateFromTask<StyleDefinitionEntity>(TrainStyleAsync);
        TrainGlobalModelCommand = ReactiveCommand.CreateFromTask(TrainGlobalModelAsync);
        ScanLibraryCommand = ReactiveCommand.CreateFromTask(ScanLibraryAsync);
        
        AddTrackToStyleCommand = ReactiveCommand.CreateFromTask<string>(AddTrackToStyleAsync);
        RemoveTrackFromStyleCommand = ReactiveCommand.CreateFromTask<string>(RemoveTrackFromStyleAsync);

        _ = LoadStylesAsync();
    }

    public async Task LoadStylesAsync()
    {
        Styles.Clear();
        using var context = await _dbFactory.CreateDbContextAsync();
        var styles = await context.StyleDefinitions.ToListAsync();
        foreach (var style in styles)
        {
            Styles.Add(style);
        }
    }

    private async Task CreateStyleAsync()
    {
        if (string.IsNullOrWhiteSpace(NewStyleName)) return;

        var style = new StyleDefinitionEntity
        {
            Name = NewStyleName,
            ColorHex = GenerateRandomColor()
        };

        using var context = await _dbFactory.CreateDbContextAsync();
        context.StyleDefinitions.Add(style);
        await context.SaveChangesAsync();

        Styles.Add(style);
        NewStyleName = string.Empty; // Reset input
        _eventBus.Publish(new StyleDefinitionsUpdatedEvent());
    }

    private async Task DeleteStyleAsync(StyleDefinitionEntity style)
    {
        using var context = await _dbFactory.CreateDbContextAsync();
        context.StyleDefinitions.Remove(style);
        await context.SaveChangesAsync();
        Styles.Remove(style);
        _eventBus.Publish(new StyleDefinitionsUpdatedEvent());
    }

    private async Task TrainStyleAsync(StyleDefinitionEntity style)
    {
        // For backwards compatibility or single-style focus.
        // In ML.NET model, we usually train all.
        await TrainGlobalModelAsync();
    }
    
    private async Task TrainGlobalModelAsync()
    {
        // Trigger global training via service (using Guid.Empty to signal global if needed, 
        // but our upgraded implementation ignores ID anyway)
        await _classifier.TrainStyleAsync(Guid.Empty);
        IsModelOutdated = false;
    }

    private async Task ScanLibraryAsync()
    {
        await _classifier.ScanLibraryAsync();
    }

    private async Task LoadReferenceTracksAsync()
    {
        ReferenceTracks.Clear();
        if (SelectedStyle == null) return;
        
        var hashes = SelectedStyle.ReferenceTrackHashes;
        if (!hashes.Any()) return;
        
        using var context = await _dbFactory.CreateDbContextAsync();
        var tracks = await context.Tracks
            .Where(t => hashes.Contains(t.GlobalId))
            .ToListAsync();
            
        foreach (var t in tracks)
        {
             var pt = new PlaylistTrack
             {
                 Id = Guid.NewGuid(), // Temp ID
                 TrackUniqueHash = t.GlobalId,
                 Artist = t.Artist,
                 Title = t.Title,
                 Album = string.Empty, 
                 Bitrate = t.Bitrate,
                 CanonicalDuration = t.CanonicalDuration,
                 DetectedSubGenre = SelectedStyle.Name // Assuming these are teaching examples
             };
             ReferenceTracks.Add(new PlaylistTrackViewModel(pt, null));
        }
    }

    private async Task AddTrackToStyleAsync(string trackHash)
    {
        if (SelectedStyle == null || string.IsNullOrEmpty(trackHash)) return;
        
        var current = SelectedStyle.ReferenceTrackHashes;
        if (!current.Contains(trackHash))
        {
            current.Add(trackHash);
            SelectedStyle.ReferenceTrackHashes = current; 
            
            using var context = await _dbFactory.CreateDbContextAsync();
            context.StyleDefinitions.Update(SelectedStyle);
            await context.SaveChangesAsync();
            
            await LoadReferenceTracksAsync();
            IsModelOutdated = true;
        }
    }

    private async Task RemoveTrackFromStyleAsync(string trackHash)
    {
        if (SelectedStyle == null || string.IsNullOrEmpty(trackHash)) return;
        
        var current = SelectedStyle.ReferenceTrackHashes;
        if (current.Contains(trackHash))
        {
            current.Remove(trackHash);
            SelectedStyle.ReferenceTrackHashes = current;
            
            using var context = await _dbFactory.CreateDbContextAsync();
            context.StyleDefinitions.Update(SelectedStyle);
            await context.SaveChangesAsync();
            
            await LoadReferenceTracksAsync();
            IsModelOutdated = true;
        }
    }

    private string GenerateRandomColor()
    {
        var rnd = new Random();
        return $"#{rnd.Next(100, 255):X2}{rnd.Next(100, 255):X2}{rnd.Next(100, 255):X2}";
    }
}
