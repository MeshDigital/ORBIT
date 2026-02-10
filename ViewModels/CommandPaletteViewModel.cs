using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels
{
    public class CommandPaletteViewModel : ReactiveObject
    {
        private readonly INavigationService _navigationService;

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

        public ObservableCollection<CommandItem> AllCommands { get; } = new();
        public ObservableCollection<CommandItem> FilteredCommands { get; } = new();

        public ReactiveCommand<Unit, Unit> CloseCommand { get; }

        public CommandPaletteViewModel(INavigationService navigationService)
        {
            _navigationService = navigationService;
            CloseCommand = ReactiveCommand.Create(() => { IsVisible = false; });

            // Initialize default commands
            InitializeCommands();
            UpdateFilter(); // Initial state

            // Subscribe to SearchText changes
            this.WhenAnyValue(x => x.SearchText)
                .Throttle(TimeSpan.FromMilliseconds(50))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => UpdateFilter());
        }

        private void InitializeCommands()
        {
            AllCommands.Clear();
            AllCommands.Add(new CommandItem("Go to Dashboard", "Home", () => _navigationService.NavigateTo("Home")));
            AllCommands.Add(new CommandItem("Go to Library", "Library", () => _navigationService.NavigateTo("Library")));
            AllCommands.Add(new CommandItem("Go to Search", "Search", () => _navigationService.NavigateTo("Search")));
            AllCommands.Add(new CommandItem("Go to Inbox", "App", () => _navigationService.NavigateTo("Projects")));
            AllCommands.Add(new CommandItem("Go to Import", "App", () => _navigationService.NavigateTo("Import")));
            AllCommands.Add(new CommandItem("Go to The Processor", "AI Lab", () => _navigationService.NavigateTo("AnalysisQueue")));
            AllCommands.Add(new CommandItem("Go to Style Lab", "AI Lab", () => _navigationService.NavigateTo("StyleLab")));
            AllCommands.Add(new CommandItem("Go to DJ Companion", "Curate", () => _navigationService.NavigateTo("DJCompanion")));
            AllCommands.Add(new CommandItem("Go to Timeline", "Curate", () => _navigationService.NavigateTo("DawTimeline")));
            AllCommands.Add(new CommandItem("Go to Flow Builder", "Curate", () => _navigationService.NavigateTo("FlowBuilder")));
            AllCommands.Add(new CommandItem("Go to Export Manager", "Deliver", () => _navigationService.NavigateTo("Export")));
            AllCommands.Add(new CommandItem("Go to Settings", "System", () => _navigationService.NavigateTo("Settings")));
        }

        private void UpdateFilter()
        {
            // We can't clear/add on a background thread if bound to UI, 
            // but ObserveOn(RxApp.MainThreadScheduler) should handle it.
            // However, a completely new list creation might be safer if concurrency is high, 
            // but FilteredCommands is ObservableCollection bound to UI.
            
            var query = SearchText?.ToLower() ?? "";
            
            // Allow empty query to show all (or maybe top 5 recent?)
            // For now, allow empty query to show all.

            var matches = AllCommands.Where(cmd => 
                string.IsNullOrWhiteSpace(query) || 
                cmd.Name.ToLower().Contains(query) || 
                cmd.Category.ToLower().Contains(query)).ToList();

            FilteredCommands.Clear();
            foreach (var cmd in matches)
            {
                FilteredCommands.Add(cmd);
            }
        }

        public void ExecuteCommand(CommandItem item)
        {
            if (item == null) return;
            
            item.Action?.Invoke();
            IsVisible = false;
            SearchText = "";
        }
    }

    public class CommandItem
    {
        public string Name { get; }
        public string Category { get; }
        public System.Action Action { get; }

        public CommandItem(string name, string category, System.Action action)
        {
            Name = name;
            Category = category;
            Action = action;
        }
    }
}
