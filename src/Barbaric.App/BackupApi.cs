using System.IO;
using System.Text.Json;
using System.Windows;
using Barbaric.App.Bridge;
using Barbaric.Core.Library;
using Microsoft.Win32;

namespace Barbaric.App;

/// <summary>Exports the library to a backup file and merges one back in, as <c>backup.*</c> bridge methods.</summary>
public sealed class BackupApi
{
    private const string FileFilter = "Barbaric backups (*.json)|*.json|All files (*.*)|*.*";

    /// <summary>The settings a backup carries. Window placement, the queue and the background belong to this PC or follow the theme.</summary>
    private static readonly string[] SettingsKeys =
        [MainWindow.UiKey, PlayerApi.VolumeKey, PlayerApi.LoopTrackKey, PlayerApi.NormalizeKey, GlobalHotkeys.SettingsKey];

    private readonly LibraryBackup _backup;
    private readonly LibraryApi _library;
    private readonly SettingsStore _settings;
    private readonly Action<IReadOnlyDictionary<string, JsonElement>> _applySettings;
    private readonly Window _owner;

    /// <summary>The backup the user picked and confirmed the summary of, waiting for <c>backup.import</c>.</summary>
    private BackupFile? _picked;

    public BackupApi(
        LibraryBackup backup,
        LibraryApi library,
        SettingsStore settings,
        Action<IReadOnlyDictionary<string, JsonElement>> applySettings,
        WebBridge bridge,
        Window owner)
    {
        _backup = backup;
        _library = library;
        _settings = settings;
        _applySettings = applySettings;
        _owner = owner;

        bridge.QueryAsync("backup.export", async _ => await ExportAsync());
        bridge.QueryAsync("backup.pickImport", async _ => await PickImportAsync());
        bridge.QueryAsync("backup.import", async _ => await ImportAsync());
    }

    private async Task<object?> ExportAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export backup",
            Filter = FileFilter,
            FileName = $"Barbaric backup {DateTime.Now:yyyy-MM-dd}.json",
            DefaultExt = ".json",
            AddExtension = true,
        };
        if (dialog.ShowDialog(_owner) != true)
        {
            return null;
        }

        var path = dialog.FileName;
        var backup = await Task.Run(async () =>
        {
            _settings.Flush();
            var file = await _backup.ExportAsync(ReadSettings());

            // Written next to the target first, so a failure never leaves half a backup in its place.
            var temp = path + ".tmp";
            await using (var stream = File.Create(temp))
            {
                file.Write(stream);
            }

            File.Move(temp, path, overwrite: true);
            return file;
        });

        return new { path, songs = backup.Songs.Count, tags = backup.Tags.Count, playlists = backup.Playlists.Count };
    }

    private async Task<object?> PickImportAsync()
    {
        _picked = null;
        var dialog = new OpenFileDialog { Title = "Import backup", Filter = FileFilter };
        if (dialog.ShowDialog(_owner) != true)
        {
            return null;
        }

        var path = dialog.FileName;
        var backup = await Task.Run(() =>
        {
            using var stream = File.OpenRead(path);
            return BackupFile.Read(stream);
        });

        _picked = backup;
        return new
        {
            path,
            exported = backup.Exported,
            songs = backup.Songs.Count,
            tags = backup.Tags.Count,
            playlists = backup.Playlists.Count,
            hasSettings = backup.Settings.Keys.Any(SettingsKeys.Contains),
        };
    }

    private async Task<object?> ImportAsync()
    {
        var backup = _picked ?? throw new InvalidOperationException("Pick a backup to import first.");
        _picked = null;

        var result = await _library.RunExclusiveAsync(() => Task.Run(() => _backup.ImportAsync(backup)));

        var settings = backup.Settings.Where(s => SettingsKeys.Contains(s.Key)).ToDictionary();
        if (settings.Count > 0)
        {
            _applySettings(settings);
        }

        // Songs whose files are in the library folders (moved or not) come back now, so the report
        // only lists the ones that really need finding.
        await _library.ScanAndWaitAsync();
        var missing = await _backup.GetMissingAsync(result.TrackIds);
        _library.EmitTracksChanged();

        return new
        {
            result.Matched,
            result.Added,
            result.TagsCreated,
            result.PlaylistsCreated,
            result.PlaylistsReplaced,
            missing,
        };
    }

    private Dictionary<string, JsonElement> ReadSettings()
    {
        var settings = new Dictionary<string, JsonElement>();
        foreach (var key in SettingsKeys)
        {
            if (_settings.GetRaw(key) is not { } json)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                settings[key] = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // A damaged value isn't worth backing up.
            }
        }

        return settings;
    }
}
