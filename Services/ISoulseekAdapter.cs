using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

public interface ISoulseekAdapter
{
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
}
