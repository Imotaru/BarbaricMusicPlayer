using Barbaric.Core.Library;
using Dapper;

namespace Barbaric.Core.Tests.Library;

public sealed class SettingsTests : IDisposable
{
    private readonly LibraryFixture _library = new();
    private readonly SettingsRepository _settings;

    public SettingsTests()
    {
        _settings = new SettingsRepository(_library.Database);
    }

    public void Dispose() => _library.Dispose();

    private sealed record Sample(string Name, double Volume, int[] Numbers);

    [Fact]
    public void Values_RoundTrip_AndCanBeReplaced()
    {
        Assert.Null(_settings.Get<Sample>("sample"));

        _settings.Set("sample", new Sample("x", 0.5, [1, 2]));
        _settings.Set("sample", new Sample("y", 0.25, [3]));

        var value = _settings.Get<Sample>("sample")!;
        Assert.Equal("y", value.Name);
        Assert.Equal(0.25, value.Volume);
        Assert.Equal([3], value.Numbers);
    }

    [Fact]
    public void RawJson_IsStoredAsIs_AndCanBeRemoved()
    {
        const string json = """{"theme":"paper"}""";
        _settings.SetRaw("ui", json);
        Assert.Equal(json, _settings.GetRaw("ui"));

        _settings.Remove("ui");
        Assert.Null(_settings.GetRaw("ui"));
    }

    [Fact]
    public void DamagedValue_ReadsAsDefault()
    {
        _settings.SetRaw("volume", "not json");
        Assert.Equal(0, _settings.Get<double>("volume"));
        Assert.Null(_settings.Get<Sample>("volume"));
    }

    [Fact]
    public void OlderDatabase_GetsTheSettingsTable()
    {
        using (var connection = _library.Database.Open())
        {
            connection.Execute(
                """
                DROP TABLE settings;
                ALTER TABLE tracks DROP COLUMN overrides;
                ALTER TABLE tracks DROP COLUMN loudness_lufs;
                ALTER TABLE tracks DROP COLUMN peak_db;
                ALTER TABLE tracks DROP COLUMN loudness_analyzed;
                PRAGMA user_version = 3;
                """);
        }

        var reopened = new LibraryDatabase(Path.Combine(_library.Root, "library.db"));
        new SettingsRepository(reopened).Set("volume", 0.5);

        using var check = reopened.Open();
        Assert.Equal(6, check.ExecuteScalar<long>("PRAGMA user_version;"));
        Assert.Equal(0.5, new SettingsRepository(reopened).Get<double>("volume"));
    }
}
