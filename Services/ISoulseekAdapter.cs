using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

public interface ISoulseekAdapter
{
    bool IsConnected { get; }
    Task ConnectAsync(string? password = null, CancellationToken ct = default);
    Task DisconnectAsync();
    void Disconnect();
    Task<int> SearchAsync(
        string query,
        IEnumerable<string>? formatFilter,
        (int? Min, int? Max) bitrateFilter,
        DownloadMode mode,
        Action<IEnumerable<Track>> onTracksFound,
        CancellationToken ct = default);

    IAsyncEnumerable<Track> StreamResultsAsync(
        string query,
        IEnumerable<string>? formatFilter,
        (int? Min, int? Max) bitrateFilter,
        DownloadMode mode,
        CancellationToken ct = default);

    Task<bool> DownloadAsync(
        string username,
        string filename,
        string outputPath,
        long? size = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    Task<int> ProgressiveSearchAsync(
        string artist,
        string title,
        string? album,
        IEnumerable<string>? formatFilter,
        (int? Min, int? Max) bitrateFilter,
        Action<IEnumerable<Track>> onTracksFound,
        CancellationToken ct = default);    
}
