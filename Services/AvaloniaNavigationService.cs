using Avalonia.Controls;
using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace SLSKDONET.Services;

/// <summary>
/// Avalonia-based navigation service.
/// Uses ContentControl binding instead of WPF Frame navigation.
/// Pages are registered as view models/controls and swapped via ContentControl.Content binding.
/// </summary>
public interface INavigationService
{
    void RegisterPage(string key, Type pageType);
    void NavigateTo(string pageKey);
    void GoBack();
    object? CurrentPage { get; }
}

public class NavigationService : INavigationService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly Dictionary<string, Type> _pages = new();
    private readonly Stack<object?> _pageHistory = new();
    private object? _currentPage;

    public object? CurrentPage => _currentPage;

    public NavigationService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public void RegisterPage(string key, Type pageType)
    {
        _pages[key] = pageType;
    }

    public void NavigateTo(string pageKey)
    {
        if (_pages.TryGetValue(pageKey, out var pageType))
        {
            // Save current page in history (if exists)
            if (_currentPage != null)
            {
                _pageHistory.Push(_currentPage);
            }

            // Create new page instance via DI
            var page = _serviceProvider.GetService(pageType) as UserControl;
            if (page != null)
            {
                _currentPage = page;
                OnPageChanged();
            }
        }
    }

    public void GoBack()
    {
        if (_pageHistory.Count > 0)
        {
            _currentPage = _pageHistory.Pop();
            OnPageChanged();
        }
    }

    private void OnPageChanged()
    {
        // Notify listeners (will be implemented via property change in MainViewModel)
    }
}
