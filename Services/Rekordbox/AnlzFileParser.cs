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
    /// Attempts to find an associated Rekordbox ANLZ file and parse it.
    /// Probes standard directory structures (local and USB-style).
    /// </summary>
    public AnlzData TryFindAndParseAnlz(string audioPath)
    {
        var dir = Path.GetDirectoryName(audioPath);
        if (dir == null) return new AnlzData();

        var fileName = Path.GetFileNameWithoutExtension(audioPath);
        
        // Probing paths:
        // 1. Same directory, same name with .DAT extension
        // 2. Same directory, ANLZ subfolder
        // 3. PIONEER/USBANLZ structure (if on USB)
        
        string[] candidates = {
            Path.Combine(dir, fileName + ".DAT"),
            Path.Combine(dir, "ANLZ", fileName + ".DAT"),
            Path.Combine(dir, fileName + ".ext"),
            Path.Combine(dir, fileName + ".2ex")
        };
        
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                _logger.LogInformation("Found Rekordbox companion file: {Path}", candidate);
                return Parse(candidate);
            }
        }
        
        return new AnlzData();
    }

    /// <summary>
    /// Parses ANLZ data from a byte array.
    /// </summary>
    public AnlzData Parse(byte[] buffer)
    {
        using var ms = new MemoryStream(buffer);
        return Parse(ms);
    }

    /// <summary>
    /// Parses ANLZ data from a stream.
    /// </summary>
    public AnlzData Parse(Stream stream)
    {
        var data = new AnlzData();
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        
        // Read and validate header
        var header = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (header != "PMAI")
        {
            _logger.LogWarning("Invalid ANLZ header: {Header}, expected PMAI", header);
            return data;
        }
        
        var headerLength = reader.ReadUInt32BigEndian();
        
        // Parse tags using TLV pattern
        while (stream.Position < stream.Length - 8)
        {
            var tag = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var length = reader.ReadUInt32BigEndian();
            
            switch (tag)
            {
                case "PQTZ": 
                case "PQT2": 
                    data.BeatGrid = ParseBeatGrid(reader, length);
                    break;
                    
                case "PCOB": 
                case "PCO2": 
                    data.CuePoints = ParseCuePoints(reader, length);
                    break;
                    
                case "PWAV": 
                case "PWV2": 
                    data.WaveformData = ParseWaveform(reader, length);
                    break;
                case "PWV3":
                case "PWH1": // High-res/RGB tags
                case "PWH2":
                case "PWH3":
                    var rgbData = ParseWaveform(reader, length);
                    ExtractRgbFromPwh(rgbData, data);
                    break;
                    
                case "PSSI": 
                    data.SongStructure = ParseSongStructure(reader, length);
                    break;
                    
                default:
                    reader.ReadBytes((int)length);
                    break;
            }
        }
        
        return data;
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
        
        using var fs = File.OpenRead(filePath);
        return Parse(fs);
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
        return reader.ReadBytes((int)length);
    }

    /// <summary>
    /// Processes Rekordbox high-res waveform data (PWHx tags) which contains
    /// interleaved Low (Red), Mid (Green), and High (Blue) components.
    /// </summary>
    private void ExtractRgbFromPwh(byte[] data, AnlzData target)
    {
        // PWH3 usually starts with a 16-byte header, then interleaved bytes.
        // Each entry is often 3-4 bytes depending on the version.
        // For simplicity, we detect fixed offsets or patterns if known.
        // In many Rekordbox versions, it's 3 bytes per sample: Low, Mid, High.
        
        if (data.Length < 20) return;

        int headerSize = 16;
        int payloadSize = data.Length - headerSize;
        int sampleCount = payloadSize / 3; 
        
        var low = new byte[sampleCount];
        var mid = new byte[sampleCount];
        var high = new byte[sampleCount];
        var summary = new byte[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            int baseIdx = headerSize + (i * 3);
            low[i] = data[baseIdx];
            mid[i] = data[baseIdx + 1];
            high[i] = data[baseIdx + 2];
            // Summary is usually max of the three
            summary[i] = Math.Max(low[i], Math.Max(mid[i], high[i]));
        }

        target.LowData = low;
        target.MidData = mid;
        target.HighData = high;
        target.WaveformData = summary;
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
    public byte[] LowData { get; set; } = Array.Empty<byte>();
    public byte[] MidData { get; set; } = Array.Empty<byte>();
    public byte[] HighData { get; set; } = Array.Empty<byte>();
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
