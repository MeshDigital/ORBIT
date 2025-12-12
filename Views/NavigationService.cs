using System;
using System.Collections.Generic;
using System.Windows.Controls;
using SLSKDONET.Views;

namespace SLSKDONET.Services;

public interface INavigationService
{
    void SetFrame(Frame frame);
    void RegisterPage(string key, Type pageType);
    void NavigateTo(string pageKey);
}

public class NavigationService : INavigationService
{
    private readonly IServiceProvider _serviceProvider;
    private Frame? _frame;
    private readonly Dictionary<string, Type> _pages = new();

    public NavigationService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public void RegisterPage(string key, Type pageType)
    {
        _pages[key] = pageType;
    }

    public void SetFrame(Frame frame)
    {
        _frame = frame;
    }

    public void NavigateTo(string pageKey)
    {
        if (_frame != null && _pages.TryGetValue(pageKey, out var pageType))
        {
            var page = _serviceProvider.GetService(pageType) as Page;
            if (page != null)
            {
                // FIX: Only set the DataContext if the page doesn't already have one.
                // Pages with dedicated ViewModels (like LibraryPage) set their own DataContext
                // in their constructor via dependency injection.
                if (page.DataContext == null)
                {
                    var mainVm = _serviceProvider.GetService(typeof(MainViewModel)) as MainViewModel;
                    if (page is ImportPreviewPage && mainVm?.ImportPreviewViewModel != null)
                    {
                        page.DataContext = mainVm.ImportPreviewViewModel;
                    }
                    else
                    {
                        page.DataContext = mainVm;
                    }
                }
                _frame.Navigate(page);
            }
        }
    }
}