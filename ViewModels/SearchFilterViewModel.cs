using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.ComponentModel;
using System.Collections.Specialized;
using DynamicData;
using ReactiveUI;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels;

public class SearchFilterViewModel : ReactiveObject
{
    // Throttled Bitrate
    private int _minBitrate = 320;
    public int MinBitrate 
    { 
        get => _minBitrate; 
        set => this.RaiseAndSetIfChanged(ref _minBitrate, value); 
    }

    // Formats
    public ObservableCollection<string> SelectedFormats { get; } = new ObservableCollection<string>(new[] { "MP3", "FLAC", "WAV" });
    
    // Reliability
    private bool _useHighReliability;
    public bool UseHighReliability 
    { 
        get => _useHighReliability; 
        set => this.RaiseAndSetIfChanged(ref _useHighReliability, value); 
    }

    // Format Toggles (Helpers for UI binding)
    public bool FilterMp3
    {
        get => SelectedFormats.Contains("MP3");
        set => ToggleFormat("MP3", value);
    }

    public bool FilterFlac
    {
        get => SelectedFormats.Contains("FLAC");
        set => ToggleFormat("FLAC", value);
    }

    public bool FilterWav
    {
        get => SelectedFormats.Contains("WAV");
        set => ToggleFormat("WAV", value);
    }

    public SearchFilterViewModel()
    {
        // React to collection changes to trigger UI updates for toggle properties
        SelectedFormats.CollectionChanged += (s, e) => 
        {
            this.RaisePropertyChanged(nameof(FilterMp3));
            this.RaisePropertyChanged(nameof(FilterFlac));
            this.RaisePropertyChanged(nameof(FilterWav));
        };
    }

    private void ToggleFormat(string format, bool isSelected)
    {
        if (isSelected && !SelectedFormats.Contains(format))
            SelectedFormats.Add(format);
        else if (!isSelected && SelectedFormats.Contains(format))
            SelectedFormats.Remove(format);
            
        // Trigger a re-evaluation of the predicate is handled by the WhenAnyValue in SearchViewModel observing this object
        // But for SelectedFormats (collection), we might need an observable.
        // Actually, simpler: The parent VM observes this object properties.
        // For collection changes, we might want to expose a "FilterChanged" observable.
    }

    public IObservable<Func<SearchResult, bool>> FilterChanged => 
        this.WhenAnyValue(
            x => x.MinBitrate,
            x => x.UseHighReliability)
            .Throttle(TimeSpan.FromMilliseconds(200), RxApp.MainThreadScheduler)
            .Merge(
                Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                    h => SelectedFormats.CollectionChanged += h, 
                    h => SelectedFormats.CollectionChanged -= h)
                .Select(_ => System.Reactive.Unit.Default)
                .Select(_ => (MinBitrate, UseHighReliability)) // Dummy value to match type
            )
            .Select(_ => GetFilterPredicate());


    public Func<SearchResult, bool> GetFilterPredicate()
    {
        // Capture current state values to avoid closure issues if they change during evaluation (though usually strictly sequential)
        var minBitrate = MinBitrate;
        var formats = SelectedFormats.Select(f => f.ToUpperInvariant()).ToHashSet(); // HashSet for O(1)
        var highReliability = UseHighReliability;
        
        // Return a single optimized function
        return result => 
        {
            if (result.Model == null) return false;

            // 1. Bitrate Check with "Bucket Logic" for VBR
            // If user asks for 320, we allow V0 (~240+)
            // If user asks for 256, we allow ~220
            int effectiveMin = minBitrate;
            if (minBitrate >= 320) effectiveMin = 240;      // Allow V0
            else if (minBitrate >= 256) effectiveMin = 220; // Allow V1
            else if (minBitrate >= 192) effectiveMin = 180; // Allow V2

            if (result.Bitrate < effectiveMin) return false;

            // 2. Format
            // Normalize extension
            var ext = System.IO.Path.GetExtension(result.Model.Filename)?.TrimStart('.')?.ToUpperInvariant() ?? "";
            
            // Map "MPEG Layer 3" etc if needed, but usually extension is "mp3"
            if (!formats.Contains(ext)) return false; 

            // 3. Reliability (Queue Length)
            // If High Reliability is ON, reject queues > 50
            if (highReliability && result.QueueLength > 50) return false;

            return true;
        };
    }

    public void Reset()
    {
        MinBitrate = 320;
        UseHighReliability = false;
        
        // Reset formats (avoid triggering too many updates)
        if (SelectedFormats.Count != 3) 
        {
             SelectedFormats.Clear();
             SelectedFormats.Add("MP3");
             SelectedFormats.Add("FLAC");
             SelectedFormats.Add("WAV");
        }
    }
}
