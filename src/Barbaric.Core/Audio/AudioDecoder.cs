using NAudio.Vorbis;
using NAudio.Wave;

namespace Barbaric.Core.Audio;

/// <summary>Opens audio files for decoding, shared by playback and analysis.</summary>
public static class AudioDecoder
{
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static WaveStream Open(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Audio file not found.", path);
        }

        return Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            ? new VorbisWaveReader(path)
            : new MediaFoundationReader(path);
    }
}
