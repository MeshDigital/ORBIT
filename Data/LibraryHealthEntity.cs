using System;
using System.ComponentModel.DataAnnotations;

namespace SLSKDONET.Data;

public class LibraryHealthEntity
{
    [Key]
    public int Id { get; set; } // We only ever keep one record (Id=1)
    
    public int TotalTracks { get; set; }
    public int HqTracks { get; set; } // > 256kbps or FLAC
    public int UpgradableCount { get; set; } // Low bitrate tracks
    public int PendingUpdates { get; set; } // Tracks needing metadata/enrichment
    
    public long TotalStorageBytes { get; set; }
    public long FreeStorageBytes { get; set; }
    
    public DateTime LastScanDate { get; set; }
    public string? TopGenresJson { get; set; } // Serialized top genres
}
