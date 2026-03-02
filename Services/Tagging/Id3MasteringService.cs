using System;
using System.IO;
using System.Threading.Tasks;
using SLSKDONET.Models.Studio;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Services.Tagging;

public class Id3MasteringService
{
    private readonly TagTemplateEngine _templateEngine;

    public Id3MasteringService(TagTemplateEngine templateEngine)
    {
        _templateEngine = templateEngine ?? throw new ArgumentNullException(nameof(templateEngine));
    }

    public async Task WriteTagsAsync(IDisplayableTrack track, TagTemplateSettings settings)
    {
        // 1. Resolve File Path
        string? filePath = null;
        if (track is PlaylistTrackViewModel ptvm)
        {
            filePath = ptvm.Model.ResolvedFilePath;
        }

        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            throw new FileNotFoundException("Could not resolve physical file path for track tagging", filePath);
        }

        await Task.Run(() =>
        {
            string? tempPath = null;
            try
            {
                // 2. Create Temporary Copy for Atomic Write
                tempPath = filePath + ".tmp_tagging";
                File.Copy(filePath, tempPath, true);

                // 3. Format Template Strings
                string newTitle = _templateEngine.FormatString(settings.TitleTemplate, track);
                string newComment = _templateEngine.FormatString(settings.CommentsTemplate, track);

                // 4. Write Tags to Temp File
                using (var file = TagLib.File.Create(tempPath))
                {
                    if (!string.IsNullOrEmpty(newTitle))
                        file.Tag.Title = newTitle;
                    
                    if (!string.IsNullOrEmpty(newComment))
                        file.Tag.Comment = newComment;

                    if (settings.UpdateBpmField && track.Bpm.HasValue)
                        file.Tag.BeatsPerMinute = (uint)Math.Round(track.Bpm.Value);

                    if (settings.UpdateKeyField && !string.IsNullOrEmpty(track.Key))
                        file.Tag.InitialKey = track.Key;

                    file.Save();
                }

                // 5. Atomic Overwrite
                File.Move(tempPath, filePath, true);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, $"Failed to write ID3 tags safely to {filePath}");
                if (tempPath != null && File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { /* Ignore cleanup errors */ }
                }
                throw;
            }
        });
    }
}
