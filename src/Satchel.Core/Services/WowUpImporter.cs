using System.Text.Json;
using System.Text.RegularExpressions;
using Satchel.Core.Models;
using Satchel.Core.Providers;
using Satchel.Core.Storage;

namespace Satchel.Core.Services;

/// <summary>Reads WowUp / WowUp-CF data (installations and tracked addons) so switching is one click.</summary>
public static partial class WowUpImporter
{
    public sealed record ImportResult(List<WowInstallation> Installations, List<Addon> Addons, string Source);

    public static IEnumerable<string> CandidateDirs()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(appData, "WowUpCf");
        yield return Path.Combine(appData, "WowUp");
    }

    public static bool IsAvailable() => CandidateDirs().Any(d => File.Exists(Path.Combine(d, "preferences.json")));

    /// <summary>Imports from every WowUp data folder found, CurseForge edition first.</summary>
    public static ImportResult? Import(IEnumerable<string>? dataDirs = null)
    {
        var installs = new List<WowInstallation>();
        var addons = new List<Addon>();
        var sources = new List<string>();
        var byPath = new Dictionary<string, WowInstallation>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in dataDirs ?? CandidateDirs())
        {
            try
            {
                var prefsPath = Path.Combine(dir, "preferences.json");
                if (!File.Exists(prefsPath)) continue;
                using var prefs = JsonDocument.Parse(File.ReadAllText(prefsPath));
                if (!prefs.RootElement.TryGetProperty("wow_installations", out var list) || list.ValueKind != JsonValueKind.Array) continue;

                var idMap = new Dictionary<string, WowInstallation>();
                foreach (var w in list.EnumerateArray())
                {
                    var location = Str(w, "location");
                    if (string.IsNullOrEmpty(location)) continue;
                    var folder = Path.GetDirectoryName(location) ?? location;
                    var type = (WowClientType)Int(w, "clientType", (int)WowClientType.None);
                    var fromFolder = WowClients.FromFolderName(Path.GetFileName(folder));
                    if (fromFolder != WowClientType.None) type = fromFolder;
                    if (type == WowClientType.None) continue;

                    var key = InstallationDetector.Normalize(folder);
                    if (!byPath.TryGetValue(key, out var install))
                    {
                        var label = Str(w, "label");
                        install = new WowInstallation
                        {
                            Id = Str(w, "id") is { Length: > 0 } id ? id : Guid.NewGuid().ToString(),
                            ClientType = type,
                            Path = folder,
                            Label = label.Contains('{') ? "" : label,
                            DefaultChannel = (AddonChannel)Math.Clamp(Int(w, "defaultAddonChannelType", 0), 0, 2),
                            DefaultAutoUpdate = Bool(w, "defaultAutoUpdate"),
                        };
                        byPath[key] = install;
                        installs.Add(install);
                    }
                    idMap[Str(w, "id")] = install;
                }

                var addonsPath = Path.Combine(dir, "addons.json");
                if (File.Exists(addonsPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(addonsPath));
                    var items = doc.RootElement.ValueKind == JsonValueKind.Object
                        ? doc.RootElement.EnumerateObject().Select(p => p.Value)
                        : doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray() : [];
                    foreach (var a in items)
                    {
                        if (!idMap.TryGetValue(Str(a, "installationId"), out var install)) continue;
                        var addon = MapAddon(a, install);
                        if (addon == null) continue;
                        // Don't double-import the same addon from the second WowUp folder.
                        if (addons.Any(x => x.InstallationId == addon.InstallationId && x.ProviderName == addon.ProviderName && x.ExternalId == addon.ExternalId && x.IsManaged))
                            continue;
                        addons.Add(addon);
                    }
                }
                sources.Add(Path.GetFileName(dir));
            }
            catch (Exception ex)
            {
                Log.Error($"WowUp import from {dir} failed", ex);
            }
        }

        return installs.Count == 0 ? null : new ImportResult(installs, addons, string.Join(", ", sources));
    }

    private static Addon? MapAddon(JsonElement a, WowInstallation install)
    {
        var folders = Str(a, "installedFolders").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (folders.Count == 0 && a.TryGetProperty("installedFolderList", out var fl) && fl.ValueKind == JsonValueKind.Array)
            folders = fl.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
        if (folders.Count == 0) return null;

        var (provider, externalId) = MapProvider(a);
        var managed = provider.Length > 0 && externalId.Length > 0;
        if (!managed) return null; // untracked folders are rediscovered by the scan
        return new Addon
        {
            InstallationId = install.Id,
            Name = Str(a, "name"),
            Author = Str(a, "author"),
            Summary = Str(a, "summary"),
            ThumbnailUrl = Str(a, "thumbnailUrl"),
            WebsiteUrl = Str(a, "externalUrl") is var url && !url.Contains("wowup.io") ? url : "",
            ProviderName = provider,
            ExternalId = externalId,
            Channel = (AddonChannel)Math.Clamp(Int(a, "channelType", 0), 0, 2),
            AutoUpdate = Bool(a, "autoUpdateEnabled"),
            Ignored = Bool(a, "isIgnored"),
            InstalledVersion = Str(a, "installedVersion"),
            // CurseForge file ids mean the same thing in both apps; other providers fall back to version compare.
            InstalledFileId = managed && Str(a, "providerName") == "Curse" ? Str(a, "installedExternalReleaseId") : "",
            InstalledAt = DateTimeOffset.TryParse(Str(a, "installedAt"), out var at) ? at : null,
            Folders = folders,
            LatestVersion = Str(a, "latestVersion"),
            LatestFileId = Str(a, "providerName") is "Curse" ? Str(a, "externalLatestReleaseId") : "",
            LatestReleasedAt = DateTimeOffset.TryParse(Str(a, "releasedAt"), out var rel) ? rel : null,
        };
    }

    private static (string Provider, string ExternalId) MapProvider(JsonElement a)
    {
        var provider = Str(a, "providerName");
        var id = Str(a, "externalId");
        var alternates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (a.TryGetProperty("externalIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in ids.EnumerateArray()) alternates.TryAdd(Str(e, "providerName"), Str(e, "id"));
        }

        switch (provider)
        {
            case "Curse":
                return (ProviderNames.CurseForge, id);
            case "WowInterface":
                return (ProviderNames.WowInterface, id);
            case "TukUI":
                return (ProviderNames.Tukui, id switch { "-2" => "elvui", "-1" => "tukui", _ => id });
            case "GitHub":
                if (Uri.TryCreate(id, UriKind.Absolute, out var gh)) return (ProviderNames.GitHub, GitHubProvider.RepoFromUrl(gh) ?? "");
                return (ProviderNames.GitHub, id);
        }

        // Providers we don't talk to (WowUp Hub, Wago): fall back to an equivalent source.
        var repo = GitHubRepoRegex().Match(Str(a, "downloadUrl"));
        if (repo.Success) return (ProviderNames.GitHub, $"{repo.Groups[1].Value}/{repo.Groups[2].Value}");
        if (alternates.TryGetValue("Curse", out var cf) && cf.Length > 0) return (ProviderNames.CurseForge, cf);
        if (alternates.TryGetValue("WowInterface", out var wowi) && wowi.Length > 0) return (ProviderNames.WowInterface, wowi);
        return ("", "");
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            _ => "",
        } : "";

    private static int Int(JsonElement e, string name, int fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : fallback;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    [GeneratedRegex(@"github\.com/([^/]+)/([^/]+)/releases/", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubRepoRegex();
}
