using System.Text.Json;

namespace Barbaric.Core.Analysis;

/// <summary>Where a song's sound starts and ends when anything quieter than <see cref="Db"/> counts as silence.</summary>
public sealed record SilenceEdge(int Db, long StartMs, long EndMs);

/// <summary>
/// The silence before and after a song's sound. Analysis finds the edges for every threshold from
/// <see cref="MinDb"/> to <see cref="MaxDb"/> in <see cref="StepDb"/> steps, so changing the threshold
/// takes effect without measuring the songs again.
/// </summary>
public static class Silence
{
    public const int MinDb = -70;

    public const int MaxDb = -30;

    public const int StepDb = 5;

    public const int DefaultDb = -50;

    /// <summary>Kept before the first sound, so a soft attack isn't cut.</summary>
    public const long LeadMs = 100;

    /// <summary>Kept after the last sound, so a fading tail isn't cut.</summary>
    public const long TailMs = 300;

    public static IReadOnlyList<int> Thresholds { get; } =
        [.. Enumerable.Range(0, (MaxDb - MinDb) / StepDb + 1).Select(i => MinDb + i * StepDb)];

    /// <summary>The nearest threshold analysis has edges for.</summary>
    public static int Snap(int db) => Math.Clamp((int)Math.Round(db / (double)StepDb) * StepDb, MinDb, MaxDb);

    /// <summary>The edges for a threshold, or null when the song hasn't been measured.</summary>
    public static SilenceEdge? At(IReadOnlyList<SilenceEdge>? edges, int db)
    {
        var snapped = Snap(db);
        return edges?.FirstOrDefault(e => e.Db == snapped);
    }

    /// <summary>The stored form: <c>[[db, startMs, endMs], ...]</c>.</summary>
    public static string? ToJson(IReadOnlyList<SilenceEdge>? edges) =>
        edges is null ? null : JsonSerializer.Serialize(edges.Select(e => new[] { e.Db, e.StartMs, e.EndMs }));

    public static IReadOnlyList<SilenceEdge>? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<long[][]>(json)?
                .Where(e => e.Length == 3)
                .Select(e => new SilenceEdge((int)e[0], e[1], e[2]))
                .ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
