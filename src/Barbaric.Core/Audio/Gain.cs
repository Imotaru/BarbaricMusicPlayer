namespace Barbaric.Core.Audio;

/// <summary>Conversions between decibels and linear amplitude for per-track gain.</summary>
public static class Gain
{
    public const double MinDb = -24;
    public const double MaxDb = 12;

    /// <summary>
    /// How loud the loud parts of every song are made to sound, in LUFS. Most songs peak at full scale
    /// and so can't be turned up; a target this low lets quieter ones reach it too.
    /// </summary>
    public const double TargetLufs = -14;

    /// <summary>Automatic boosts stop this far below full scale, so a quiet song never clips.</summary>
    public const double HeadroomDb = 0.5;

    public static double ClampDb(double db) => Math.Clamp(db, MinDb, MaxDb);

    public static float DbToLinear(double db) => (float)Math.Pow(10, ClampDb(db) / 20);

    /// <summary>
    /// The gain that brings a song's loud parts to <see cref="TargetLufs"/>. A boost is capped so the
    /// loudest sample stays <see cref="HeadroomDb"/> below full scale; an unmeasured or silent song is left as is.
    /// </summary>
    public static double AutoGainDb(double? loudPartLufs, double? peakDb)
    {
        if (loudPartLufs is not { } loudness)
        {
            return 0;
        }

        var gain = TargetLufs - loudness;
        if (peakDb is { } peak)
        {
            gain = Math.Min(gain, Math.Max(0, -HeadroomDb - peak));
        }

        return Math.Round(ClampDb(gain), 2);
    }
}
