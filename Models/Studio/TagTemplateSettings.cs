namespace SLSKDONET.Models.Studio;

public class TagTemplateSettings
{
    public string TitleTemplate { get; set; } = "[{CamelotKey}] {Title}";
    public string CommentsTemplate { get; set; } = "Energy: {EnergyLevel} | Orbit AI";
    public bool UpdateBpmField { get; set; } = true;
    public bool UpdateKeyField { get; set; } = true;
}
