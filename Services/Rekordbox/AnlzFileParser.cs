using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using SLSKDONET.Utils;

namespace SLSKDONET.Services.Rekordbox;

/// <summary>
/// Parses Rekordbox ANLZ files (.DAT, .EXT, .2EX) to extract beat grids, waveforms, and cue points.
/// Uses Tag-Length-Value (TLV) pattern to read proprietary binary structures.
/// </summary>
public class AnlzFileParser
{
    private readonly ILogger<AnlzFileParser> _logger;
    private readonly XorService _xorService;
    
    public AnlzFileParser(ILogger<AnlzFileParser> logger, XorService xorService)
    {
        _logger = logger;
        _xorService = xorService;
    }
    
    /// <summary>
    /// Parses an ANLZ file and extracts all supported tags.
    /// </summary>
    public AnlzData Parse(string filePath)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("ANLZ file not found: {Path}", filePath);
            return new AnlzData();
        }
        
        _logger.LogInformation("Parsing ANLZ file: {Path}", filePath);
        
        var data = new AnlzData();
        
        using var fs = File.OpenRead(filePath);
        using var reader = new BinaryReader(fs);
        
        // Read and validate header
        var header = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (header != "PMAI")
        {
            _logger.LogWarning("Invalid ANLZ header: {Header}, expected PMAI", header);
            return data;
        }
        
        var headerLength = reader.ReadUInt32BigEndian();
        _logger.LogDebug("ANLZ header length: {Length} bytes", headerLength);
        
        // Parse tags using TLV pattern
        while (fs.Position < fs.Length - 8)
        {
            var tag = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var length = reader.ReadUInt32BigEndian();
            
            _logger.LogDebug("Found tag: {Tag}, length: {Length}", tag, length);
            
            switch (tag)
            {
                case "PQTZ": // Beat Grid (quantized ticks)
                case "PQT2": // Beat Grid v2
                    data.BeatGrid = ParseBeatGrid(reader, length);
                    _logger.LogInformation("Parsed beat grid: {Count} ticks", data.BeatGrid.Count);
                    break;
                    
                case "PCOB": // Cue Points
                case "PCO2": // Cue Points v2
                    data.CuePoints = ParseCuePoints(reader, length);
                    _logger.LogInformation("Parsed cue points: {Count} cues", data.CuePoints.Count);
                    break;
                    
                case "PWAV": // Waveform Preview
                case "PWV2": // Waveform v2
                case "PWV3": // Waveform v3
                    data.WaveformData = ParseWaveform(reader, length);
                    _logger.LogInformation("Parsed waveform: {Size} bytes", data.WaveformData.Length);
                    break;
                    
                case "PSSI": // Song Structure (encrypted)
                    data.SongStructure = ParseSongStructure(reader, length);
                    _logger.LogInformation("Parsed song structure: {Count} phrases", data.SongStructure.Phrases.Count);
                    break;
                    
                default:
                    // Skip unknown tag
                    reader.ReadBytes((int)length);
                    _logger.LogDebug("Skipped unknown tag: {Tag}", tag);
                    break;
            }
        }
        
        return data;
    }
    
    /// <summary>
    /// Parses PQTZ beat grid tag.
    /// Each tick represents a beat position with BPM and time.
    /// </summary>
    private List<BeatGridTick> ParseBeatGrid(BinaryReader reader, uint length)
    {
        var ticks = new List<BeatGridTick>();
        
        // Skip header (16 bytes)
        reader.ReadBytes(16);
        
        // Each tick is 8 bytes: beat(2) + tempo(2) + time(4)
        var tickCount = (length - 24) / 8; // 24 = header + footer
        
        for (int i = 0; i < tickCount; i++)
        {
            var tick = new BeatGridTick
            {
                Beat = reader.ReadUInt16BigEndian(),
                Tempo = reader.ReadUInt16BigEndian(), // BPM * 100
                TimeMs = reader.ReadUInt32BigEndian()
            };
            
            ticks.Add(tick);
        }
        
        // Skip footer (8 bytes)
        reader.ReadBytes(8);
        
        return ticks;
    }
    
    /// <summary>
    /// Parses PCOB cue point tag.
    /// </summary>
    private List<CuePoint> ParseCuePoints(BinaryReader reader, uint length)
    {
        var cues = new List<CuePoint>();
        
        // Skip header
        reader.ReadBytes(16);
        
        // Each cue is variable length, read until we hit the footer
        var bytesRead = 16;
        while (bytesRead < length - 8)
        {
            var cue = new CuePoint
            {
                Type = reader.ReadByte(),
                Position = reader.ReadUInt32BigEndian(),
                Red = reader.ReadByte(),
                Green = reader.ReadByte(),
                Blue = reader.ReadByte()
            };
            
            // Read name (null-terminated string, max 64 bytes)
            var nameBytes = new List<byte>();
            for (int i = 0; i < 64; i++)
            {
                var b = reader.ReadByte();
                if (b == 0) break;
                nameBytes.Add(b);
            }
            cue.Name = Encoding.UTF8.GetString(nameBytes.ToArray());
            
            cues.Add(cue);
            bytesRead += 71; // Approximate, actual size varies
        }
        
        return cues;
    }
    
    /// <summary>
    /// Parses PWAV waveform tag.
    /// Uses ArrayPool to avoid GC pressure for large waveforms.
    /// </summary>
    private byte[] ParseWaveform(BinaryReader reader, uint length)
    {
        // Use ArrayPool to avoid allocating large byte arrays
        var buffer = ArrayPool<byte>.Shared.Rent((int)length);
        
        try
        {
            reader.Read(buffer, 0, (int)length);
            
            // Copy to sized array for return
            var result = new byte[length];
            Array.Copy(buffer, result, (int)length);
            
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
    
    /// <summary>
    /// Parses PSSI song structure tag (encrypted).
    /// </summary>
    private SongStructure ParseSongStructure(BinaryReader reader, uint length)
    {
        var encryptedData = reader.ReadBytes((int)length);
        
        // Extract entry count from header (for XOR mask calculation)
        var lenEntries = BitConverter.ToInt32(encryptedData, 8);
        
        // Descramble using XOR service
        var decryptedData = _xorService.Descramble(encryptedData, lenEntries);
        
        // Parse phrases from decrypted data
        return ParsePhrases(decryptedData);
    }
    
    /// <summary>
    /// Parses phrase markers from descrambled PSSI data.
    /// </summary>
    private SongStructure ParsePhrases(byte[] data)
    {
        var structure = new SongStructure { Phrases = new List<Phrase>() };
        
        // TODO: Implement phrase parsing from binary data
        // This requires further reverse-engineering of the PSSI structure
        
        return structure;
    }
    
    /// <summary>
    /// Applies timing offset to beat grid (for MP3 → FLAC sample alignment).
    /// </summary>
    public void ApplyOffset(List<BeatGridTick> beatGrid, int offsetMs)
    {
        if (offsetMs == 0) return;
        
        _logger.LogInformation("Applying {Offset}ms offset to beat grid", offsetMs);
        
        foreach (var tick in beatGrid)
        {
            // Adjust time, ensuring it doesn't go negative
            var newTime = (int)tick.TimeMs + offsetMs;
            tick.TimeMs = (uint)Math.Max(0, newTime);
        }
    }
}

/// <summary>
/// Container for parsed ANLZ data.
/// </summary>
public class AnlzData
{
    public List<BeatGridTick> BeatGrid { get; set; } = new();
    public List<CuePoint> CuePoints { get; set; } = new();
    public byte[] WaveformData { get; set; } = Array.Empty<byte>();
    public SongStructure SongStructure { get; set; } = new();
}

/// <summary>
/// Represents a single beat grid tick from PQTZ tag.
/// </summary>
public class BeatGridTick
{
    public ushort Beat { get; set; }     // Beat number
    public ushort Tempo { get; set; }    // BPM * 100
    public uint TimeMs { get; set; }     // Milliseconds from start
    
    public double GetBPM() => Tempo / 100.0;
    
    public override string ToString() => $"Beat {Beat} @ {TimeMs}ms ({GetBPM()}BPM)";
}

/// <summary>
/// Represents a cue point from PCOB tag.
/// </summary>
public class CuePoint
{
    public byte Type { get; set; }       // 0=Memory, 1=Loop
    public uint Position { get; set; }   // Position in samples
    public byte Red { get; set; }        // RGB color
    public byte Green { get; set; }
    public byte Blue { get; set; }
    public string Name { get; set; } = string.Empty;
    
    public override string ToString() => $"{Name} @ {Position} ({Red},{Green},{Blue})";
}

/// <summary>
/// Represents song structure with phrase markers.
/// </summary>
public class SongStructure
{
    public List<Phrase> Phrases { get; set; } = new();
}
