namespace Barbaric.Core.Audio;

/// <summary>Conversions between decibels and linear amplitude for per-track gain.</summary>
public static class Gain
{
    public const double MinDb = -24;
    public const double MaxDb = 12;

    public static double ClampDb(double db) => Math.Clamp(db, MinDb, MaxDb);

    public static float DbToLinear(double db) => (float)Math.Pow(10, ClampDb(db) / 20);
}
