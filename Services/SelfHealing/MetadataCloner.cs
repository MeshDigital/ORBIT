using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TagLib;
using SLSKDONET.Data;

namespace SLSKDONET.Services.SelfHealing;

/// <summary>
/// Clones metadata from source file to target file during library upgrades.
/// Preserves user tags, musical metadata, and ORBIT-specific custom fields.
/// Supports cross-format transfer (ID3 <-> Vorbis).
/// </summary>
public class MetadataCloner
{
    private readonly ILogger<MetadataCloner> _logger;
    
    // ORBIT custom tag keys
    private const string TAG_TRACK_ID = "ORBIT_TRACK_ID";
    private const string TAG_SPOTIFY_ID = "ORBIT_SPOTIFY_ID";
    private const string TAG_INTEGRITY = "ORBIT_INTEGRITY";
    private const string TAG_IMPORT_DATE = "ORBIT_IMPORT_DATE";
    private const string TAG_UPGRADE_SOURCE = "ORBIT_UPGRADE_SOURCE";
    private const string TAG_ORIGINAL_BITRATE = "ORBIT_ORIGINAL_BITRATE";
    
    public MetadataCloner(ILogger<MetadataCloner> logger)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// Clones all metadata from source file to target file.
    /// </summary>
    /// <param name="sourcePath">Path to original file (e.g., 128kbps MP3)</param>
    /// <param name="targetPath">Path to upgraded file (e.g., FLAC)</param>
    /// <param name="track">Database entity with resolved metadata</param>
    public async Task CloneAsync(string sourcePath, string targetPath, TrackEntity track)
    {
        _logger.LogInformation("Cloning metadata: {Source} → {Target}", 
            System.IO.Path.GetFileName(sourcePath), 
            System.IO.Path.GetFileName(targetPath));
        
        try
        {
            // Read both files
            using var sourceFile = TagLib.File.Create(sourcePath);
            using var targetFile = TagLib.File.Create(targetPath);
            
            // 1. Clone standard tags
            CloneStandardTags(sourceFile, targetFile);
            
            // 2. Clone musical metadata with Dual-Truth resolution
            CloneMusicalMetadata(targetFile, track);
            
            // 3. Clone album art
            CloneAlbumArt(sourceFile, targetFile);
            
            // 4. Clone ORBIT custom tags (CRITICAL: includes ORBIT_TRACK_ID for database link)
            CloneORBITCustomTags(targetFile, track);
            
            // 5. Save all changes
            targetFile.Save();
            
            _logger.LogInformation("✅ Metadata cloned successfully");
            
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clone metadata from {Source} to {Target}", sourcePath, targetPath);
            throw new MetadataCloneException("Metadata clone operation failed", ex);
        }
    }
    
    /// <summary>
    /// Clones standard ID3/Vorbis tags.
    /// </summary>
    private void CloneStandardTags(TagLib.File source, TagLib.File target)
    {
        target.Tag.Title = source.Tag.Title;
        target.Tag.Performers = source.Tag.Performers; // Artists
        target.Tag.Album = source.Tag.Album;
        target.Tag.AlbumArtists = source.Tag.AlbumArtists;
        target.Tag.Year = source.Tag.Year;
        target.Tag.Genres = source.Tag.Genres;
        target.Tag.Track = source.Tag.Track;
        target.Tag.TrackCount = source.Tag.TrackCount;
        target.Tag.Disc = source.Tag.Disc;
        target.Tag.DiscCount = source.Tag.DiscCount;
        target.Tag.Comment = source.Tag.Comment;
        target.Tag.Composers = source.Tag.Composers;
        
        _logger.LogDebug("Cloned standard tags: {Title} by {Artist}", target.Tag.Title, target.Tag.FirstPerformer);
    }
    
    /// <summary>
    /// Clones musical metadata using Dual-Truth resolution.
    /// Priority: Manual > Analyzed > Spotify
    /// </summary>
    private void CloneMusicalMetadata(TagLib.File target, TrackEntity track)
    {
        // Resolve BPM (Manual > BPM > SpotifyBPM)
        var resolvedBPM = track.ManualBPM ?? track.BPM ?? track.SpotifyBPM;
        if (resolvedBPM.HasValue)
        {
            WriteBPMTag(target, resolvedBPM.Value);
            _logger.LogDebug("Wrote BPM: {BPM}", resolvedBPM.Value);
        }
        
        // Resolve Key (Manual > MusicalKey > SpotifyKey)
        var resolvedKey = track.ManualKey ?? track.MusicalKey ?? track.SpotifyKey;
        if (!string.IsNullOrEmpty(resolvedKey))
        {
            WriteKeyTag(target, resolvedKey);
            _logger.LogDebug("Wrote Key: {Key}", resolvedKey);
        }
    }
    
    /// <summary>
    /// Writes BPM to appropriate tag frame based on file format.
    /// </summary>
    private void WriteBPMTag(TagLib.File file, double bpm)
    {
        var rounded = Math.Round(bpm, 2);
        
        // ID3v2 (MP3, WAV)
        if (file.TagTypes.HasFlag(TagTypes.Id3v2))
        {
            var id3Tag = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2);
            var bpmFrame = TagLib.Id3v2.TextInformationFrame.Get(id3Tag, "TBPM", true);
            bpmFrame.Text = new[] { rounded.ToString("F2") };
        }
        // Vorbis Comments (FLAC, OGG)
        else if (file.TagTypes.HasFlag(TagTypes.Xiph))
        {
            var vorbisTag = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph);
            vorbisTag.SetField("BPM", rounded.ToString("F2"));
        }
        // APE (Musepack, APE)
        else if (file.TagTypes.HasFlag(TagTypes.Ape))
        {
            var apeTag = (TagLib.Ape.Tag)file.GetTag(TagTypes.Ape);
            apeTag.SetValue("BPM", rounded.ToString("F2"));
        }
    }
    
    /// <summary>
    /// Writes musical key to appropriate tag frame.
    /// </summary>
    private void WriteKeyTag(TagLib.File file, string key)
    {
        // ID3v2
        if (file.TagTypes.HasFlag(TagTypes.Id3v2))
        {
            var id3Tag = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2);
            
            // TKEY frame (standard)
            var keyFrame = TagLib.Id3v2.TextInformationFrame.Get(id3Tag, "TKEY", true);
            keyFrame.Text = new[] { key };
            
            // Also write to TXXX:KEY for Rekordbox compatibility
            var customFrame = TagLib.Id3v2.UserTextInformationFrame.Get(id3Tag, "KEY", true);
            customFrame.Text = new[] { key };
        }
        // Vorbis Comments
        else if (file.TagTypes.HasFlag(TagTypes.Xiph))
        {
            var vorbisTag = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph);
            vorbisTag.SetField("INITIALKEY", key); // Standard Vorbis key field
            vorbisTag.SetField("KEY", key); // Alternative field
        }
        // APE
        else if (file.TagTypes.HasFlag(TagTypes.Ape))
        {
            var apeTag = (TagLib.Ape.Tag)file.GetTag(TagTypes.Ape);
            apeTag.SetValue("KEY", key);
        }
    }
    
    /// <summary>
    /// Clones album art images.
    /// </summary>
    private void CloneAlbumArt(TagLib.File source, TagLib.File target)
    {
        var pictures = source.Tag.Pictures;
        if (pictures.Length > 0)
        {
            target.Tag.Pictures = pictures;
            _logger.LogDebug("Cloned {Count} album art image(s)", pictures.Length);
        }
    }
    
    /// <summary>
    /// Writes ORBIT-specific custom tags.
    /// CRITICAL: ORBIT_TRACK_ID maintains database-to-file link.
    /// </summary>
    private void CloneORBITCustomTags(TagLib.File target, TrackEntity track)
    {
        var customTags = new System.Collections.Generic.Dictionary<string, string>
        {
            [TAG_TRACK_ID] = track.GlobalId, // CRITICAL for database link
            [TAG_SPOTIFY_ID] = track.SpotifyTrackId ?? "",
            [TAG_INTEGRITY] = track.Integrity.ToString(),
            [TAG_IMPORT_DATE] = track.AddedAt.ToString("yyyy-MM-dd"),
            [TAG_UPGRADE_SOURCE] = "Auto", // Mark as auto-upgraded
            [TAG_ORIGINAL_BITRATE] = track.PreviousBitrate ?? $"{track.Bitrate}kbps"
        };
        
        foreach (var (key, value) in customTags)
        {
            WriteCustomTag(target, key, value);
        }
        
        _logger.LogDebug("Wrote {Count} ORBIT custom tags (including critical ORBIT_TRACK_ID)", customTags.Count);
    }
    
    /// <summary>
    /// Writes a custom tag to the appropriate format.
    /// </summary>
    private void WriteCustomTag(TagLib.File file, string key, string value)
    {
        // ID3v2: Use TXXX (User-defined text frame)
        if (file.TagTypes.HasFlag(TagTypes.Id3v2))
        {
            var id3Tag = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2);
            var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3Tag, key, true);
            frame.Text = new[] { value };
        }
        // Vorbis Comments: Direct field
        else if (file.TagTypes.HasFlag(TagTypes.Xiph))
        {
            var vorbisTag = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph);
            vorbisTag.SetField(key, value);
        }
        // APE: Direct value
        else if (file.TagTypes.HasFlag(TagTypes.Ape))
        {
            var apeTag = (TagLib.Ape.Tag)file.GetTag(TagTypes.Ape);
            apeTag.SetValue(key, value);
        }
    }
    
    /// <summary>
    /// Reads a custom tag value.
    /// </summary>
    private string? ReadCustomTag(TagLib.File file, string key)
    {
        try
        {
            // ID3v2
            if (file.TagTypes.HasFlag(TagTypes.Id3v2))
            {
                var id3Tag = (TagLib.Id3v2.Tag)file.GetTag(TagTypes.Id3v2);
                var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3Tag, key, false);
                return frame?.Text.FirstOrDefault();
            }
            // Vorbis Comments
            else if (file.TagTypes.HasFlag(TagTypes.Xiph))
            {
                var vorbisTag = (TagLib.Ogg.XiphComment)file.GetTag(TagTypes.Xiph);
                return vorbisTag.GetFirstField(key);
            }
            // APE
            else if (file.TagTypes.HasFlag(TagTypes.Ape))
            {
                var apeTag = (TagLib.Ape.Tag)file.GetTag(TagTypes.Ape);
                var item = apeTag.GetItem(key);
                return item?.ToString();
            }
        }
        catch
        {
            // Tag doesn't exist
        }
        
        return null;
    }
    
    /// <summary>
    /// Verifies that metadata was cloned correctly.
    /// </summary>
    public async Task<bool> VerifyCloneAsync(string targetPath, TrackEntity expectedTrack)
    {
        _logger.LogDebug("Verifying metadata clone for {Path}", targetPath);
        
        try
        {
            using var file = TagLib.File.Create(targetPath);
            
            // Verify critical fields
            if (file.Tag.Title != expectedTrack.Title)
            {
                _logger.LogWarning("Title mismatch: Expected '{Expected}', Got '{Actual}'", 
                    expectedTrack.Title, file.Tag.Title);
                return false;
            }
            
            if (file.Tag.FirstPerformer != expectedTrack.Artist)
            {
                _logger.LogWarning("Artist mismatch: Expected '{Expected}', Got '{Actual}'", 
                    expectedTrack.Artist, file.Tag.FirstPerformer);
                return false;
            }
            
            // CRITICAL: Verify ORBIT_TRACK_ID (database link)
            var orbitId = ReadCustomTag(file, TAG_TRACK_ID);
            if (orbitId != expectedTrack.GlobalId)
            {
                _logger.LogError("ORBIT_TRACK_ID mismatch! Expected '{Expected}', Got '{Actual}'. Database link broken!",
                    expectedTrack.GlobalId, orbitId);
                return false;
            }
            
            _logger.LogDebug("✅ Metadata verification passed");
            return await Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Metadata verification failed for {Path}", targetPath);
            return false;
        }
    }
}

/// <summary>
/// Exception thrown when metadata cloning fails.
/// </summary>
public class MetadataCloneException : Exception
{
    public MetadataCloneException(string message) : base(message) { }
    public MetadataCloneException(string message, Exception innerException) : base(message, innerException) { }
}
