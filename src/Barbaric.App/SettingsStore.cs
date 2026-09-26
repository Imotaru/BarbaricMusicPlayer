using Barbaric.Core.Library;

namespace Barbaric.App;

/// <summary>
/// Reads settings directly, but writes them from a background task, newest value per key, so the UI
/// thread never waits on a database a scan is writing to. <see cref="Flush"/> saves what's pending
/// when the app closes.
/// </summary>
public sealed class SettingsStore(SettingsRepository settings)
{
    private readonly Dictionary<string, string> _pending = [];
    private readonly object _pendingLock = new();
    private readonly object _writeLock = new();

    public event EventHandler<Exception>? WriteFailed;

    public T? Get<T>(string key) => settings.Get<T>(key);

    public string? GetRaw(string key) => settings.GetRaw(key);

    public void Save<T>(string key, T value) => SaveRaw(key, SettingsRepository.Serialize(value));

    public void SaveRaw(string key, string json)
    {
        lock (_pendingLock)
        {
            var idle = _pending.Count == 0;
            _pending[key] = json;
            if (idle)
            {
                _ = Task.Run(Flush);
            }
        }
    }

    /// <summary>Writes everything pending now, on the calling thread.</summary>
    public void Flush()
    {
        // One writer at a time, so an older value can never land after a newer one for the same key.
        lock (_writeLock)
        {
            KeyValuePair<string, string>[] batch;
            lock (_pendingLock)
            {
                batch = [.. _pending];
                _pending.Clear();
            }

            foreach (var (key, json) in batch)
            {
                try
                {
                    settings.SetRaw(key, json);
                }
                catch (Exception ex)
                {
                    WriteFailed?.Invoke(this, ex);
                }
            }
        }
    }
}
