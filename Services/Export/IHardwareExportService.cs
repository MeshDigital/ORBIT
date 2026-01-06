using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Export;

public enum HardwarePlatform
{
    Standard,   // Flat folder structure
    Pioneer,    // Rekordbox XML + Contents folder
    Denon       // Engine Library structure
}

public class ExportDriveInfo
{
    public string Name { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public long FreeSpaceBytes { get; set; }
    public string Format { get; set; } = string.Empty; // FAT32, exFAT, NTFS
    public bool IsRemovable { get; set; }
}

public class ExportProgressEventArgs : EventArgs
{
    public int CurrentTrack { get; set; }
    public int TotalTracks { get; set; }
    public string Status { get; set; } = string.Empty;
    public double Percentage => TotalTracks > 0 ? (double)CurrentTrack / TotalTracks * 100 : 0;
}

public interface IHardwareExportService
{
    IEnumerable<ExportDriveInfo> GetAvailableDrives();
    
    Task ExportProjectAsync(
        PlaylistJob project, 
        ExportDriveInfo targetDrive, 
        HardwarePlatform platform, 
        CancellationToken cancellationToken = default);
        
    event EventHandler<ExportProgressEventArgs>? ProgressChanged;
}
