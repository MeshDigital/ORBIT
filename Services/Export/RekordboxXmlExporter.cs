using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml;
using SLSKDONET.Data;
using SLSKDONET.Models;
using System.Linq;

namespace SLSKDONET.Services.Export
{
    public class RekordboxXmlExporter
    {
        public async Task ExportToXmlAsync(IEnumerable<TrackEntity> tracks, string outputPath)
        {
            var settings = new XmlWriterSettings
            {
                Indent = true,
                Encoding = Encoding.UTF8,
                Async = true
            };

            await using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await using var writer = XmlWriter.Create(fileStream, settings);

            await writer.WriteStartDocumentAsync();
            await writer.WriteStartElementAsync(null, "DJ_PLAYLISTS", null);
            await writer.WriteAttributeStringAsync(null, "Version", null, "1.0.0");

            await writer.WriteStartElementAsync(null, "PRODUCT", null);
            await writer.WriteAttributeStringAsync(null, "Name", null, "rekordbox");
            await writer.WriteAttributeStringAsync(null, "Version", null, "6.0.0");
            await writer.WriteAttributeStringAsync(null, "Company", null, "Pioneer DJ");
            await writer.WriteEndElementAsync(); // PRODUCT

            // Ensure we have a count without multiple enumeration if possible
            List<TrackEntity> trackList = tracks.ToList();
            int totalTracks = trackList.Count;

            await writer.WriteStartElementAsync(null, "COLLECTION", null);
            await writer.WriteAttributeStringAsync(null, "Entries", null, totalTracks.ToString());

            int trackIdCounter = 1;

            foreach (var track in trackList)
            {
                await writer.WriteStartElementAsync(null, "TRACK", null);
                
                await writer.WriteAttributeStringAsync(null, "TrackID", null, trackIdCounter.ToString());
                await writer.WriteAttributeStringAsync(null, "Name", null, track.Title ?? "Unknown");
                await writer.WriteAttributeStringAsync(null, "Artist", null, track.Artist ?? "Unknown");
                
                string kind = "MP3 File";
                if (!string.IsNullOrEmpty(track.Filename) && track.Filename.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    kind = "WAV File";
                }
                
                await writer.WriteAttributeStringAsync(null, "Kind", null, kind);
                await writer.WriteAttributeStringAsync(null, "Size", null, track.Size.ToString());
                
                int totalTime = track.CanonicalDuration ?? 0;
                await writer.WriteAttributeStringAsync(null, "TotalTime", null, totalTime.ToString());
                
                await writer.WriteAttributeStringAsync(null, "BitRate", null, (track.Bitrate > 0 ? track.Bitrate.ToString() : "320000"));
                await writer.WriteAttributeStringAsync(null, "SampleRate", null, "44100");
                
                string bpmStr = (track.BPM ?? 0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                await writer.WriteAttributeStringAsync(null, "AverageBpm", null, bpmStr);
                
                if (!string.IsNullOrEmpty(track.MusicalKey))
                {
                    await writer.WriteAttributeStringAsync(null, "Tonality", null, track.MusicalKey);
                }

                string location = PathNormalizer.ToRekordboxUri(track.Filename ?? string.Empty);
                await writer.WriteAttributeStringAsync(null, "Location", null, location);

                // Write Cues if any
                if (!string.IsNullOrEmpty(track.CuePointsJson))
                {
                    try
                    {
                        var cues = JsonSerializer.Deserialize<List<OrbitCue>>(track.CuePointsJson);
                        if (cues != null)
                        {
                            int cueIndex = 0;
                            foreach (var cue in cues)
                            {
                                await writer.WriteStartElementAsync(null, "POSITION_MARK", null);
                                await writer.WriteAttributeStringAsync(null, "Name", null, cue.Name ?? "Cue");
                                await writer.WriteAttributeStringAsync(null, "Type", null, "0");
                                
                                string startStr = cue.Timestamp.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
                                await writer.WriteAttributeStringAsync(null, "Start", null, startStr);
                                await writer.WriteAttributeStringAsync(null, "Num", null, cueIndex.ToString());
                                await writer.WriteEndElementAsync(); // POSITION_MARK
                                
                                cueIndex++;
                                if (cueIndex >= 8) break; // Rekordbox officially limits UI Hot Cues, 8 is safe.
                            }
                        }
                    }
                    catch
                    {
                        // Ignore any JSON parsing issues for cues and continue exporting
                    }
                }

                await writer.WriteEndElementAsync(); // TRACK
                trackIdCounter++;
            }

            await writer.WriteEndElementAsync(); // COLLECTION

            // Write Optional PLAYLISTS Block to group the export
            await writer.WriteStartElementAsync(null, "PLAYLISTS", null);
            await writer.WriteStartElementAsync(null, "NODE", null);
            await writer.WriteAttributeStringAsync(null, "Name", null, "ROOT");
            await writer.WriteAttributeStringAsync(null, "Type", null, "root");

            await writer.WriteStartElementAsync(null, "NODE", null);
            await writer.WriteAttributeStringAsync(null, "Name", null, "ORBIT Export");
            await writer.WriteAttributeStringAsync(null, "Type", null, "playlist");

            for (int i = 1; i < trackIdCounter; i++)
            {
                await writer.WriteStartElementAsync(null, "TRACK", null);
                await writer.WriteAttributeStringAsync(null, "Key", null, i.ToString());
                await writer.WriteEndElementAsync(); // TRACK
            }

            await writer.WriteEndElementAsync(); // NODE (playlist)
            await writer.WriteEndElementAsync(); // NODE (root)
            await writer.WriteEndElementAsync(); // PLAYLISTS

            await writer.WriteEndElementAsync(); // DJ_PLAYLISTS
            await writer.WriteEndDocumentAsync();
            await writer.FlushAsync();
        }
    }
}
