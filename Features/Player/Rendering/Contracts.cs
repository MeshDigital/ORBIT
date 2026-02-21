using System;

namespace SLSKDONET.Features.Player.Rendering
{
    public record FftFrame(float[] Magnitudes, int SampleRate, long Timestamp);

    public interface IFftStream : IObservable<FftFrame>
    {
    }
}
