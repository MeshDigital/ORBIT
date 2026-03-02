using System.Text;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Services.Tagging;

public class TagTemplateEngine
{
    public string FormatString(string template, IDisplayableTrack track)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;

        var sb = new StringBuilder(template);
        
        sb.Replace("{CamelotKey}", track.Key ?? "??");
        sb.Replace("{BPM}", track.Bpm?.ToString("0") ?? "??");
        sb.Replace("{Title}", track.Title);
        sb.Replace("{Artist}", track.Artist);
        sb.Replace("{EnergyLevel}", track.Energy?.ToString("0") ?? "??");

        return sb.ToString();
    }
}
