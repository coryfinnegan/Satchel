using System.Collections.Concurrent;
using Satchel.Core.Storage;

namespace Satchel.Core.Scanning;

public readonly record struct FolderStamp(int FileCount, long TotalBytes, long MaxWriteTicks);

/// <summary>Remembers folder fingerprints keyed by a cheap metadata stamp, persisted between runs.</summary>
public sealed class FingerprintCache
{
    private sealed record Entry(FolderStamp Stamp, uint Fingerprint);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private bool _dirty;

    public FingerprintCache(string path)
    {
        _path = path;
        var loaded = JsonFile.Read<Dictionary<string, Entry>>(path);
        if (loaded != null)
        {
            foreach (var kv in loaded) _entries[kv.Key] = kv.Value;
        }
    }

    public bool TryGet(string folder, FolderStamp stamp, out uint fingerprint)
    {
        if (_entries.TryGetValue(folder, out var e) && e.Stamp == stamp)
        {
            fingerprint = e.Fingerprint;
            return true;
        }
        fingerprint = 0;
        return false;
    }

    public void Set(string folder, FolderStamp stamp, uint fingerprint)
    {
        _entries[folder] = new Entry(stamp, fingerprint);
        _dirty = true;
    }

    public void Save()
    {
        if (!_dirty) return;
        try
        {
            JsonFile.Write(_path, new Dictionary<string, Entry>(_entries, StringComparer.OrdinalIgnoreCase));
            _dirty = false;
        }
        catch (IOException ex)
        {
            Log.Error("Could not save fingerprint cache", ex);
        }
    }
}
