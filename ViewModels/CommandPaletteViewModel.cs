using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using SLSKDONET.Services;
using System.Windows.Input;

namespace SLSKDONET.ViewModels
{
    public class CommandPaletteViewModel : ReactiveObject, IDisposable
    {
        private readonly INavigationService _navigationService;
        private readonly ILibraryService _libraryService;
        private readonly ActiveWorkspace _activeWorkspace;
        private readonly ICommand _queueToTopCommand;
        private readonly CompositeDisposable _disposables = new();

        private bool _isVisible;
        public bool IsVisible
        {
            get => _isVisible;
            set => this.RaiseAndSetIfChanged(ref _isVisible, value);
        }

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set => this.RaiseAndSetIfChanged(ref _searchText, value);
        }

        private List<TrackIndexEntry> _trackIndex = new();
        public ObservableCollection<CommandItem> AllCommands { get; } = new();
        public ObservableCollection<CommandItem> FilteredCommands { get; } = new();

        public ReactiveCommand<Unit, Unit> CloseCommand { get; }

        public CommandPaletteViewModel(
            INavigationService navigationService, 
            ILibraryService libraryService,
            ActiveWorkspace activeWorkspace,
            ICommand queueToTopCommand)
        {
            _navigationService = navigationService;
            _libraryService = libraryService;
            _activeWorkspace = activeWorkspace;
            _queueToTopCommand = queueToTopCommand;

            CloseCommand = ReactiveCommand.Create(() => { IsVisible = false; });

            InitializeCommands();
            UpdateFilter();

            // Background indexing
            Task.Run(StartIndexing);

            this.WhenAnyValue(x => x.SearchText)
                .Throttle(TimeSpan.FromMilliseconds(150))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => UpdateFilter())
                .DisposeWith(_disposables);

            // Re-filter when selection changes to update contextual actions
            _activeWorkspace.WhenAnyValue(x => x.SelectedTrack)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => UpdateFilter())
                .DisposeWith(_disposables);
        }

        private async Task StartIndexing()
        {
            try
            {
                var entries = await _libraryService.LoadAllLibraryEntriesAsync();
                _trackIndex = entries.Select(e => new TrackIndexEntry(e.Title, e.Artist, e.UniqueHash)).ToList();
            }
            catch (Exception ex)
            {
                // Log error through a standard way if needed
                System.Diagnostics.Debug.WriteLine($"Failed to build Command Palette index: {ex.Message}");
            }
        }

        private void InitializeCommands()
        {
            AllCommands.Clear();
            AllCommands.Add(new CommandItem("Go to Dashboard", "Navigation", () => _navigationService.NavigateTo("Home")));
            AllCommands.Add(new CommandItem("Go to Library", "Navigation", () => _navigationService.NavigateTo("Library")));
            AllCommands.Add(new CommandItem("Go to Search", "Navigation", () => _navigationService.NavigateTo("Search")));
            AllCommands.Add(new CommandItem("Go to Download Center", "App", () => _navigationService.NavigateTo("Projects")));
            AllCommands.Add(new CommandItem("Go to Import", "App", () => _navigationService.NavigateTo("Import")));
            AllCommands.Add(new CommandItem("Go to Engine Stats", "System", () => _navigationService.NavigateTo("AnalysisQueue")));
            AllCommands.Add(new CommandItem("Go to Settings", "System", () => _navigationService.NavigateTo("Settings")));
            AllCommands.Add(new CommandItem("Go to Export Manager", "Deliver", () => _navigationService.NavigateTo("Export")));
            AllCommands.Add(new CommandItem("Go to Discovery Hub", "Acquire", () => _navigationService.NavigateTo("DiscoveryHub")));
        }

        private void UpdateFilter()
        {
            var query = (SearchText ?? "").ToLower().Trim();
            var matches = new List<(CommandItem Item, double Score)>();

            // 1. Static Commands
            foreach (var cmd in AllCommands)
            {
                double score = CalculateFuzzyScore(cmd.Name, query);
                if (score > 0) matches.Add((cmd, score + 100)); // Static commands preferred
            }

            // 2. Contextual Actions
            if (_activeWorkspace.SelectedTrack != null)
            {
                var track = _activeWorkspace.SelectedTrack;
                var contextual = new List<CommandItem>
                {
                    new CommandItem($"Analyze '{track.Title}'", "Actions", () => { _activeWorkspace.ToggleAnalysisCommand?.Execute(track); }),
                    new CommandItem($"Queue '{track.Title}' to Top", "Actions", () => { _queueToTopCommand?.Execute(null); })
                };

                foreach (var cmd in contextual)
                {
                    double score = CalculateFuzzyScore(cmd.Name, query);
                    if (score > 0) matches.Add((cmd, score + 200)); // Contextual actions highly preferred
                }
            }

            // 3. Indexed Tracks (Limited to top matches for performance)
            if (!string.IsNullOrWhiteSpace(query))
            {
                foreach (var track in _trackIndex)
                {
                    double score = CalculateFuzzyScore($"{track.Artist} {track.Title}", query);
                    if (score > 0.5)
                    {
                        matches.Add((new CommandItem($"{track.Artist} - {track.Title}", "Library", () => 
                        {
                            // Navigate to track
                        }), score));
                    }
                    if (matches.Count > 50) break; // Performance guard
                }
            }

            FilteredCommands.Clear();
            foreach (var match in matches.OrderByDescending(m => m.Score).Take(20))
            {
                FilteredCommands.Add(match.Item);
            }
        }

        private double CalculateFuzzyScore(string text, string query)
        {
            if (string.IsNullOrEmpty(query)) return 1.0;
            text = text.ToLower();
            
            if (text == query) return 10.0;
            if (text.StartsWith(query)) return 8.0;

            // Levenshtein-based similarity
            int distance = LevenshteinDistance(text, query);
            double maxLen = Math.Max(text.Length, query.Length);
            double similarity = 1.0 - (distance / maxLen);
            
            if (text.Contains(query)) similarity += 0.5;

            return similarity > 0.4 ? similarity : 0;
        }

        private int LevenshteinDistance(string s, string t)
        {
            int n = s.Length;
            int m = t.Length;
            int[,] d = new int[n + 1, m + 1];

            if (n == 0) return m;
            if (m == 0) return n;

            for (int i = 0; i <= n; d[i, 0] = i++) ;
            for (int j = 0; j <= m; d[j, 0] = j++) ;

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }
            return d[n, m];
        }

        public void ExecuteCommand(CommandItem item)
        {
            if (item == null) return;
            item.Action?.Invoke();
            IsVisible = false;
            SearchText = "";
        }

        public void Dispose()
        {
            _disposables.Dispose();
        }
    }

    public class CommandItem
    {
        public string Name { get; }
        public string Category { get; }
        public Action Action { get; }

        public CommandItem(string name, string category, Action action)
        {
            Name = name;
            Category = category;
            Action = action;
        }
    }

    public record TrackIndexEntry(string Title, string Artist, string Hash);
}
