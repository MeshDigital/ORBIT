using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Models;
using SLSKDONET.Services;
using Avalonia.Threading;

namespace SLSKDONET.ViewModels.Library;

/// <summary>
/// A collection that virtualizes data by loading tracks in pages from the database.
/// Optimized for large libraries (50k+ tracks).
/// </summary>
public class VirtualizedTrackCollection : IList<PlaylistTrackViewModel>, INotifyCollectionChanged, INotifyPropertyChanged, IDisposable
{
    private readonly ILibraryService _libraryService;
    private readonly IEventBus _eventBus;
    private readonly ArtworkCacheService _artworkCache;
    private readonly Guid _playlistId;
    private readonly string? _filter;
    private readonly bool? _downloadedOnly;
    private readonly int _pageSize;
    
    private int _count = -1;
    private readonly Dictionary<int, PageInfo> _pages = new();
    private readonly HashSet<int> _pendingPages = new();
    
    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public VirtualizedTrackCollection(
        ILibraryService libraryService,
        IEventBus eventBus,
        ArtworkCacheService artworkCache,
        Guid playlistId,
        string? filter = null,
        bool? downloadedOnly = null,
        int pageSize = 100)
    {
        _libraryService = libraryService;
        _eventBus = eventBus;
        _artworkCache = artworkCache;
        _playlistId = playlistId;
        _filter = filter;
        _downloadedOnly = downloadedOnly;
        _pageSize = pageSize;
        
        // Initial count load
        _ = LoadCountAsync();
    }

    private async Task LoadCountAsync()
    {
        var count = await _libraryService.GetTrackCountAsync(_playlistId, _filter, _downloadedOnly);
        _count = count;
        
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            OnPropertyChanged(nameof(Count));
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        });
    }

    public PlaylistTrackViewModel this[int index]
    {
        get
        {
            if (index < 0 || index >= Count) throw new IndexOutOfRangeException();

            int pageIndex = index / _pageSize;
            int itemIndex = index % _pageSize;

            if (_pages.TryGetValue(pageIndex, out var page))
            {
                if (itemIndex < page.Items.Count)
                    return page.Items[itemIndex];
            }

            // Trigger async load if not already pending
            if (!_pendingPages.Contains(pageIndex))
            {
                _ = LoadPageAsync(pageIndex);
            }

            // Return a placeholder or null (Avalonia ListBox handles null/placeholder rows if configured, 
            // but usually we want a temporary VM)
            return GetPlaceholder();
        }
        set => throw new NotSupportedException();
    }

    private PlaylistTrackViewModel? _placeholder;
    private PlaylistTrackViewModel GetPlaceholder()
    {
        if (_placeholder == null)
        {
            // Empty model for placeholder
            var model = new PlaylistTrack { Title = "Loading...", Artist = "..." };
            _placeholder = new PlaylistTrackViewModel(model, _eventBus, _libraryService, _artworkCache);
        }
        return _placeholder;
    }

    private async Task LoadPageAsync(int pageIndex)
    {
        if (!_pendingPages.Add(pageIndex)) return;

        try
        {
            int skip = pageIndex * _pageSize;
            var tracks = await _libraryService.GetPagedPlaylistTracksAsync(_playlistId, skip, _pageSize, _filter, _downloadedOnly);
            
            var viewModels = tracks.Select(t => new PlaylistTrackViewModel(t, _eventBus, _libraryService, _artworkCache)).ToList();
            
            _pages[pageIndex] = new PageInfo { Items = viewModels, LastAccess = DateTime.UtcNow };

            // Cleanup old pages if cache gets too big
            if (_pages.Count > 10) // Keep ~1000 items in memory
            {
                var oldest = _pages.OrderBy(p => p.Value.LastAccess).First();
                _pages.Remove(oldest.Key);
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Notify UI that a range of items changed
                // Avalonia's VirtualizingStackPanel will re-request these items
                // Notification for the entire page range
                int startIndex = pageIndex * _pageSize;
                // Note: NotifyCollectionChangedAction.Replace is often best for single items, 
                // but for bulk, Reset or individual notifies might be needed.
                // Modern Avalonia handles Reset gracefully for virtualization.
                CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            });
        }
        finally
        {
            _pendingPages.Remove(pageIndex);
        }
    }

    public int Count => _count == -1 ? 0 : _count;

    public bool IsReadOnly => true;

    public void Add(PlaylistTrackViewModel item) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Contains(PlaylistTrackViewModel item) => false;
    public void CopyTo(PlaylistTrackViewModel[] array, int arrayIndex) { }
    public IEnumerator<PlaylistTrackViewModel> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }
    public int IndexOf(PlaylistTrackViewModel item) => -1;
    public void Insert(int index, PlaylistTrackViewModel item) => throw new NotSupportedException();
    public bool Remove(PlaylistTrackViewModel item) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        foreach (var page in _pages.Values)
        {
            foreach (var vm in page.Items)
            {
                vm.Dispose();
            }
        }
        _pages.Clear();
    }

    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private class PageInfo
    {
        public List<PlaylistTrackViewModel> Items { get; set; } = new();
        public DateTime LastAccess { get; set; }
    }
}
