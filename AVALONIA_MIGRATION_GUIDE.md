# Avalonia UI Migration Guide

## Current Status

✅ **Completed**:
- Resolved merge conflicts in `App.axaml.cs` and `Program.cs`
- Removed WPF multi-targeting from `.csproj` (now pure Avalonia `net8.0`)
- Removed WPF-specific packages (`LibVLCSharp.WPF`, `WPF-UI`)
- Removed old `App.xaml` (WPF)
- Kept all business logic (Services, Models, Database)

⏳ **Pending**:
- Remove/convert WPF-specific code from Views, ViewModels, Converters
- Create Avalonia equivalents for UI components
- Update navigation and UI services for Avalonia
- Convert WPF value converters to Avalonia equivalents

## Architecture Overview

### What Stays (Business Logic)
```
Services/
  ├── SoulseekAdapter.cs         ✅ Keep as-is
  ├── DownloadManager.cs         ✅ Keep as-is
  ├── LibraryService.cs          ✅ Keep as-is
  ├── DatabaseService.cs         ✅ Keep as-is
  ├── InputParsers/              ✅ Keep as-is
  └── ... (all non-UI services)

Models/
  ├── Track.cs                   ✅ Keep as-is
  ├── PlaylistJob.cs             ✅ Keep as-is
  └── ... (all data models)

Configuration/                     ✅ Keep as-is
Data/                             ✅ Keep as-is
Utils/                            ✅ Keep as-is
```

### What Changes (UI Layer)

#### Must Remove/Replace:
```
Converters/
  ├── BooleanToPlayPauseIconConverter.cs    ❌ WPF-specific IValueConverter
  ├── BooleanToTextConverter.cs             ❌ WPF-specific IValueConverter
  ├── ... (all WPF converters)
  └── All use System.Windows.Data.IValueConverter

Views/
  ├── MainWindow.xaml(.cs)       ❌ WPF Window
  ├── MainViewmodel.cs           ⚠️  References WPF UI
  ├── SearchPage.xaml(.cs)       ❌ WPF Page
  ├── LibraryPage.xaml(.cs)      ❌ WPF Page + ICollectionView
  ├── SettingsPage.xaml(.cs)     ❌ WPF Page
  ├── DownloadsPage.xaml(.cs)    ❌ WPF Page
  ├── NavigationService.cs       ❌ WPF Frame-based
  ├── DragAdorner.cs             ❌ WPF Adorner
  └── ... (WPF-specific helpers)

ViewModels/
  ├── LibraryViewModel.cs        ⚠️  Uses ICollectionView (WPF)
  └── PlaylistTrackViewModel.cs  ⚠️  References WPF.Media
```

## Migration Steps

### Phase 1: Remove WPF-Specific References
1. **Delete WPF Converters directory** (will create Avalonia binding converters)
2. **Remove WPF imports** from ViewModels and Views
3. **Update MainViewModel** to remove Wpf.Ui references

### Phase 2: Create Avalonia UI Shell
1. **Create `Views/Avalonia/MainWindow.axaml`** (Avalonia window)
2. **Create page containers** for navigation
3. **Update App.axaml** with proper Avalonia resources

### Phase 3: Implement Navigation
1. **Create Avalonia-compatible NavigationService**
2. **Use ContentControl or TransitioningContentControl** for page navigation
3. **Update ViewModels** to work with Avalonia data binding

### Phase 4: Recreate UI Pages
1. Convert each WPF Page to Avalonia UserControl
2. Recreate bindings without Converters (use markup extensions)
3. Update styling for Avalonia theme system

### Phase 5: Fix Data Binding
1. Remove `ICollectionView` from LibraryViewModel
2. Use Avalonia's ObservableCollection directly
3. Create Avalonia-compatible MVVM converters if needed

## Key Files to Fix

### High Priority (Blocking Build)
1. `ViewModels/MainViewModel.cs` - Remove `using Wpf.Ui.Controls;`
2. `ViewModels/LibraryViewModel.cs` - Remove `System.Windows.Data.ICollectionView`
3. `Views/NavigationService.cs` - Rewrite for Avalonia
4. Delete entire `Converters/` directory or rewrite as Avalonia converters
5. Delete entire old `Views/` directory (except Navigation-related)

### Medium Priority
1. `App.xaml.cs` - Already fixed, ensure DI is correct
2. All remaining `*.xaml.cs` files - Change from WPF Page to Avalonia UserControl
3. ViewModels using WPF-specific properties

## Avalonia Patterns Needed

### Data Binding Without Converters
```xaml
<!-- WPF -->
<TextBlock Text="{Binding Progress, Converter={StaticResource ProgressConverter}}" />

<!-- Avalonia - Use markup extensions or computed properties -->
<TextBlock Text="{Binding ProgressText}" />
```

### Navigation
```csharp
// WPF Frame-based
_navigationService.NavigateTo("LibraryPage");

// Avalonia - Use MVVM Router or ContentControl
public UserControl? CurrentPage { get; set; }
// Bind MainWindow to CurrentPage
```

### Converters
```csharp
// WPF: IValueConverter
// Avalonia: IValueConverter (same interface but different namespace)
using Avalonia.Data.Converters;

public class PlaylistNameConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // ...
    }
}
```

## Recommended Approach

**Option A: Minimal Refactor (Faster)**
- Keep business logic exactly as-is
- Create thin Avalonia UI layer that wraps ViewModels
- Remove all WPF-specific code
- Result: Rapid migration, minimal business logic changes

**Option B: Full Refactor (Better Design)**
- Separate UI concerns from business logic
- Create platform-agnostic ViewModel layer
- Implement Avalonia-specific UI patterns
- Result: Cleaner architecture, reusable business logic

## Next Steps

1. **Delete problematic files**:
   ```powershell
   Remove-Item Converters -Recurse -Force
   Remove-Item Views -Recurse -Force
   ```

2. **Recreate minimal Avalonia structure**:
   ```
   Views/Avalonia/
   ├── MainWindow.axaml
   ├── MainWindow.axaml.cs
   └── Pages/
       ├── SearchPage.axaml
       ├── LibraryPage.axaml
       ├── DownloadsPage.axaml
       └── SettingsPage.axaml
   ```

3. **Update App.xaml with Avalonia styling**

4. **Fix build errors incrementally**, starting with compilation errors

## Error Categories to Address

| Error | Solution |
|-------|----------|
| `System.Windows.Data` not found | Delete Converters, use Avalonia binding |
| `System.Windows.Controls` not found | Delete WPF Pages, create Avalonia UserControls |
| `ICollectionView` not found | Use ObservableCollection directly |
| `IValueConverter` not found | Use `Avalonia.Data.Converters.IValueConverter` |
| `Wpf.Ui` references | Remove from MainViewModel |
| `Window`, `Page`, `Adorner` not found | Replace with Avalonia equivalents |

## Testing Checklist

- [ ] Project builds without errors
- [ ] Application launches
- [ ] Navigation between pages works
- [ ] Data binding displays correctly
- [ ] Download manager updates UI
- [ ] Library displays playlist jobs
- [ ] Search functionality works

---

**Branch**: `avalonia-ui`  
**Status**: In Progress  
**Last Updated**: 2025-12-13
