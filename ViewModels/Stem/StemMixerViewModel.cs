using System;
using System.Collections.ObjectModel;
using ReactiveUI;
using SLSKDONET.Models.Stem;
using SLSKDONET.Services.Audio;

namespace SLSKDONET.ViewModels.Stem;

public class StemMixerViewModel : ReactiveObject, IDisposable
{
    private readonly RealTimeStemEngine _engine;
    public ObservableCollection<StemChannelViewModel> Channels { get; } = new();

    public StemMixerViewModel(RealTimeStemEngine engine)
    {
        _engine = engine;
        // Initialize channels with default settings
        LoadPreset(StemPreset.Default);
    }

    public void LoadProject(StemEditProject project)
    {
        LoadSettings(project.CurrentSettings);
    }

    public void LoadPreset(StemPreset preset)
    {
        LoadSettings(preset.Settings);
    }

    private void LoadSettings(System.Collections.Generic.Dictionary<StemType, StemSettings> settings)
    {
        Channels.Clear();
        foreach (StemType type in Enum.GetValues(typeof(StemType)))
        {
            var channelCallback = settings.ContainsKey(type) ? settings[type] : new StemSettings();
            var channelVm = new StemChannelViewModel(type, channelCallback, _engine);
            Channels.Add(channelVm);
        }
    }

    public void Dispose()
    {
        foreach (var channel in Channels)
        {
            channel.Dispose();
        }
        Channels.Clear();
    }
}
