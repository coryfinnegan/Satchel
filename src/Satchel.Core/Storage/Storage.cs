using System.Text;
using System.Text.Json;
using Satchel.Core.Models;
using Satchel.Core.Net;

namespace Satchel.Core.Storage;

public static class AppPaths
{
    public static string DataDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Satchel");

    public static string LocalDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Satchel");

    public static string CacheDir => Path.Combine(LocalDir, "cache");

    public static string DownloadsDir => Path.Combine(LocalDir, "downloads");

    public static string LogsDir => Path.Combine(LocalDir, "logs");
}

/// <summary>Encrypts secrets at rest (the WPF app uses Windows DPAPI, scoped to the current user).</summary>
public interface ISecretProtector
{
    string Protect(string plainText);

    string? Unprotect(string protectedText);
}

public sealed class WindowBounds
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public List<WowInstallation> Installations { get; set; } = [];

    public string? SelectedInstallationId { get; set; }

    public string? CurseForgeApiKeyProtected { get; set; }

    public string? GitHubTokenProtected { get; set; }

    public bool CheckForUpdatesOnStartup { get; set; } = true;

    /// <summary>Background update check interval; 0 disables it.</summary>
    public int CheckIntervalHours { get; set; } = 3;

    public bool MinimizeToTray { get; set; } = true;

    public bool StartWithWindows { get; set; }

    public bool InstallRequiredDependencies { get; set; } = true;

    /// <summary>Folders scanned on CurseForge by fingerprint to identify manually installed addons.</summary>
    public bool IdentifyUnknownAddons { get; set; } = true;

    public bool WowUpImportOffered { get; set; }

    public WindowBounds? Window { get; set; }
}

/// <summary>Atomic JSON file persistence with a last-good backup.</summary>
public static class JsonFile
{
    private static readonly JsonSerializerOptions Options = new(Http.Json) { WriteIndented = true };

    public static T? Read<T>(string path) where T : class
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                using var stream = File.OpenRead(candidate);
                var value = JsonSerializer.Deserialize<T>(stream, Options);
                if (value != null) return value;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                Log.Error($"Could not read {candidate}", ex);
            }
        }
        return null;
    }

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, path);
    }
}

public sealed class SettingsStore(ISecretProtector protector)
{
    private readonly object _gate = new();

    public string FilePath { get; } = Path.Combine(AppPaths.DataDir, "settings.json");

    public AppSettings Settings { get; private set; } = new();

    public void Load() => Settings = JsonFile.Read<AppSettings>(FilePath) ?? new AppSettings();

    public void Save()
    {
        lock (_gate) JsonFile.Write(FilePath, Settings);
    }

    public string? CurseForgeApiKey
    {
        get => Unprotect(Settings.CurseForgeApiKeyProtected);
        set => Settings.CurseForgeApiKeyProtected = string.IsNullOrWhiteSpace(value) ? null : protector.Protect(value.Trim());
    }

    public string? GitHubToken
    {
        get => Unprotect(Settings.GitHubTokenProtected);
        set => Settings.GitHubTokenProtected = string.IsNullOrWhiteSpace(value) ? null : protector.Protect(value.Trim());
    }

    // Decrypting on every request is wasteful; cache the last value.
    private string? _lastProtected;
    private string? _lastPlain;

    private string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        if (protectedValue == _lastProtected) return _lastPlain;
        var plain = protector.Unprotect(protectedValue);
        _lastProtected = protectedValue;
        _lastPlain = plain;
        return plain;
    }
}

/// <summary>All tracked addons across installations, persisted to addons.json.</summary>
public sealed class AddonStore
{
    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private List<Addon> _addons = [];

    public string FilePath { get; } = Path.Combine(AppPaths.DataDir, "addons.json");

    public void Load()
    {
        lock (_gate) _addons = JsonFile.Read<List<Addon>>(FilePath) ?? [];
    }

    public void Save()
    {
        List<Addon> snapshot;
        lock (_gate) snapshot = [.. _addons];
        lock (_writeGate) JsonFile.Write(FilePath, snapshot);
    }

    public IReadOnlyList<Addon> ForInstallation(string installationId)
    {
        lock (_gate) return _addons.Where(a => a.InstallationId == installationId).ToList();
    }

    public IReadOnlyList<Addon> All()
    {
        lock (_gate) return [.. _addons];
    }

    public void Upsert(Addon addon)
    {
        lock (_gate)
        {
            var i = _addons.FindIndex(a => a.Id == addon.Id);
            if (i >= 0) _addons[i] = addon;
            else _addons.Add(addon);
        }
    }

    public void Remove(Addon addon)
    {
        lock (_gate) _addons.RemoveAll(a => a.Id == addon.Id);
    }

    public void ReplaceForInstallation(string installationId, IEnumerable<Addon> addons)
    {
        lock (_gate)
        {
            _addons.RemoveAll(a => a.InstallationId == installationId);
            _addons.AddRange(addons);
        }
    }

    /// <summary>Swaps the given rows of one installation for <paramref name="rows"/>, leaving any other rows alone.</summary>
    public void ReplaceRows(string installationId, IEnumerable<string> oldIds, IEnumerable<Addon> rows)
    {
        lock (_gate)
        {
            var remove = oldIds.ToHashSet();
            _addons.RemoveAll(a => a.InstallationId == installationId && remove.Contains(a.Id));
            foreach (var row in rows)
            {
                var i = _addons.FindIndex(a => a.Id == row.Id);
                if (i >= 0) _addons[i] = row;
                else _addons.Add(row);
            }
        }
    }

    public void RemoveInstallation(string installationId)
    {
        lock (_gate) _addons.RemoveAll(a => a.InstallationId == installationId);
    }
}

public static class Log
{
    private static readonly object Gate = new();

    private static string FilePath => Path.Combine(AppPaths.LogsDir, "satchel.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.LogsDir);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1_000_000) File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
