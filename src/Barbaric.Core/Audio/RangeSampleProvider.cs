using NAudio.Wave;

namespace Barbaric.Core.Audio;

/// <summary>
/// Ends the stream at a set time, fading the last few milliseconds out so a cut in the middle of the
/// music doesn't click, and fades in after a jump to the start of the range. Reads and repositioning
/// happen under the engine's lock, which <see cref="GainSampleProvider"/> holds around every read.
/// </summary>
internal sealed class RangeSampleProvider(WaveStream reader, ISampleProvider source) : ISampleProvider
{
    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(20);

    private readonly int _channels = source.WaveFormat.Channels;
    private readonly int _rate = source.WaveFormat.SampleRate;
    private long _frame;
    private long _endFrame = long.MaxValue;
    private int _fadeInFrames;
    private int _fadeInDone;

    public WaveFormat WaveFormat => source.WaveFormat;

    /// <summary>True from a jump to the range start until the first samples are read.</summary>
    public bool AtStart { get; private set; } = true;

    /// <summary>Whether the stream has reached the end of the range.</summary>
    public bool Finished => _frame >= _endFrame;

    /// <summary>Picks up the reader's position after it moved; <paramref name="toStart"/> fades in from it.</summary>
    public void Sync(bool toStart)
    {
        _frame = Frames(reader.CurrentTime);
        AtStart = toStart;
        _fadeInFrames = toStart && _frame > 0 ? (int)Frames(FadeIn) : 0;
        _fadeInDone = 0;
    }

    /// <summary>Where the stream ends; null plays to the end of the file.</summary>
    public void SetEnd(TimeSpan? end) => _endFrame = end is { } e ? Frames(e) : long.MaxValue;

    public int Read(Span<float> buffer)
    {
        var left = _endFrame - _frame;
        if (left <= 0)
        {
            return 0;
        }

        var frames = (int)Math.Min(buffer.Length / _channels, left);
        var read = source.Read(buffer[..(frames * _channels)]);
        var readFrames = read / _channels;
        if (readFrames == 0)
        {
            return read;
        }

        AtStart = false;
        var fadeOutFrames = _endFrame == long.MaxValue ? 0 : Frames(FadeOut);
        for (var f = 0; f < readFrames; f++)
        {
            var gain = 1f;
            if (_fadeInDone < _fadeInFrames)
            {
                gain = (float)_fadeInDone++ / _fadeInFrames;
            }

            var toEnd = _endFrame - (_frame + f);
            if (toEnd < fadeOutFrames)
            {
                gain *= (float)toEnd / fadeOutFrames;
            }

            if (gain < 1f)
            {
                var samples = buffer.Slice(f * _channels, _channels);
                for (var c = 0; c < _channels; c++)
                {
                    samples[c] *= gain;
                }
            }
        }

        _frame += readFrames;
        return read;
    }

    private long Frames(TimeSpan time) => (long)Math.Round(time.TotalSeconds * _rate);
}
