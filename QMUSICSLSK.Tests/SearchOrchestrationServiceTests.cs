using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.InputParsers;
using Xunit;

namespace QMUSICSLSK.Tests;

/// <summary>
/// Unit tests for SearchOrchestrationService
/// These tests verify the business logic in isolation using mocked dependencies
/// </summary>
public class SearchOrchestrationServiceTests
{
    private readonly Mock<ISoulseekAdapter> _mockSoulseek;
    private readonly Mock<ILogger<SearchOrchestrationService>> _mockLogger;
    private readonly SearchQueryNormalizer _normalizer;
    private readonly AppConfig _config;
    private readonly SearchOrchestrationService _service;

    public SearchOrchestrationServiceTests()
    {
        // Arrange - setup mocks
        _mockSoulseek = new Mock<ISoulseekAdapter>();
        _mockLogger = new Mock<ILogger<SearchOrchestrationService>>();
        _normalizer = new SearchQueryNormalizer();
        _config = new AppConfig
        {
            PreferredFormats = new List<string> { "mp3", "flac" },
            PreferredMinBitrate = 192,
            PreferredMaxBitrate = 320
        };
        
        // Act - create service under test
        _service = new SearchOrchestrationService(
            _mockLogger.Object,
            _mockSoulseek.Object,
            _normalizer,
            _config
        );
    }

    [Fact]
    public async Task SearchAsync_CallsSoulseekAdapter_WithCorrectParameters()
    {
        // Arrange
        string query = "Artist - Title";
        string preferredFormats = "mp3,flac";
        
        _mockSoulseek
            .Setup(s => s.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<(int?, int?)>(),
                It.IsAny<DownloadMode>(),
                It.IsAny<Action<IEnumerable<Track>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        // Act
        await _service.SearchAsync(query, preferredFormats, 192, 320, false, null, CancellationToken.None);

        // Assert
        _mockSoulseek.Verify(s => s.SearchAsync(
            It.Is<string>(q => q == query),
            It.IsAny<IEnumerable<string>>(),
            It.IsAny<(int?, int?)>(),
            It.IsAny<DownloadMode>(),
            It.IsAny<Action<IEnumerable<Track>>>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SearchAsync_PassesCorrectFormatFilter()
    {
        // Arrange
        string preferredFormats = "mp3,flac,wav";
        List<string>? capturedFormats = null;
        
        _mockSoulseek
            .Setup(s => s.SearchAsync(
                It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(),
                It.IsAny<(int?, int?)>(),
                It.IsAny<DownloadMode>(),
                It.IsAny<Action<IEnumerable<Track>>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IEnumerable<string>, (int?, int?), DownloadMode, Action<IEnumerable<Track>>, CancellationToken>(
                (q, f, b, m, cb, ct) => capturedFormats = f.ToList())
            .ReturnsAsync(1);

        // Act
        await _service.SearchAsync("test", preferredFormats, 0, 0, false, null, CancellationToken.None);

        // Assert
        Assert.NotNull(capturedFormats);
        Assert.Equal(3, capturedFormats.Count);
        Assert.Contains("mp3", capturedFormats);
        Assert.Contains("flac", capturedFormats);
        Assert.Contains("wav", capturedFormats);
    }
}
