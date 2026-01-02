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

    public ReactiveCommand<Unit, Unit> CreateStyleCommand { get; }
    public ReactiveCommand<StyleDefinitionEntity, Unit> DeleteStyleCommand { get; }
    public ReactiveCommand<StyleDefinitionEntity, Unit> TrainStyleCommand { get; }
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
        if (style == null) return;
        
        // Save referencing hash updates first if any?
        // Use classifier to recalc centroid
        await _classifier.TrainStyleAsync(style.Id);
        
        // Reload to update Centroid in UI if we show it
        using var context = await _dbFactory.CreateDbContextAsync();
        var updated = await context.StyleDefinitions.FindAsync(style.Id);
        if (updated != null)
        {
             // Force update properties if needed, or just notify
        }
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
            
        // Map to ViewModel (Simplified for now, just wrapping entity)
        // Ideally we'd reuse PlaylistTrackViewModel but we need PlaylistTrack model...
        // For Lab, maybe we just need basic info.
        // Let's create proper PlaylistTrack from TrackEntity to satisfy ViewModel
        foreach (var t in tracks)
        {
             var pt = new PlaylistTrack
             {
                 Id = Guid.NewGuid(), // Temp ID
                 TrackUniqueHash = t.GlobalId,
                 Artist = t.Artist,
                 Title = t.Title,
                 Album = string.Empty, // TrackEntity does not have Album property
                 Bitrate = t.Bitrate,
                 CanonicalDuration = t.CanonicalDuration
             };
             // Null EventBus is risky but for display only it might work
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
            SelectedStyle.ReferenceTrackHashes = current; // Trigger setter for JSON serialization
            
            using var context = await _dbFactory.CreateDbContextAsync();
            context.StyleDefinitions.Update(SelectedStyle);
            await context.SaveChangesAsync();
            
            await LoadReferenceTracksAsync();
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
        }
    }

    private string GenerateRandomColor()
    {
        // Simple random pastel color
        var rnd = new Random();
        return $"#{rnd.Next(100, 255):X2}{rnd.Next(100, 255):X2}{rnd.Next(100, 255):X2}";
    }
}
