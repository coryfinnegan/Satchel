using System.Net.Http.Json;
using Satchel.Core.Models;
using Satchel.Core.Net;
using Satchel.Core.Scanning;

namespace Satchel.Core.Providers;

/// <summary>
/// CurseForge (api.curseforge.com). Requires the user's own API key from https://console.curseforge.com.
/// The key is sent only to api.curseforge.com and the CurseForge CDN (forgecdn.net).
/// </summary>
public sealed class CurseForgeProvider(Func<string?> apiKey) : IAddonProvider
{
    private const string BaseUrl = "https://api.curseforge.com";
    private const int WowGameId = 1;

    public string Name => ProviderNames.CurseForge;

    public bool IsAvailable => !string.IsNullOrWhiteSpace(apiKey());

    public string UnavailableMessage => "Add your CurseForge API key in Settings to enable CurseForge.";

    public bool SupportsSearch => true;

    private HttpRequestMessage Request(HttpMethod method, string path, object? body = null)
    {
        var key = apiKey();
        if (string.IsNullOrWhiteSpace(key)) throw new ProviderException(UnavailableMessage);
        var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Add("x-api-key", key.Trim());
        if (body != null) request.Content = JsonContent.Create(body, options: Http.Json);
        return request;
    }

    public async Task<IReadOnlyList<RemoteAddon>> SearchAsync(string query, WowInstallation installation, CancellationToken ct)
    {
        var typeId = WowClients.CurseGameVersionTypeId(installation.Group);
        var path = $"/v1/mods/search?gameId={WowGameId}&gameVersionTypeId={typeId}&sortField=2&sortOrder=desc&pageSize=50";
        if (!string.IsNullOrWhiteSpace(query)) path += "&searchFilter=" + Uri.EscapeDataString(query.Trim());
        var result = await Http.GetJsonAsync<CfResponse<List<CfMod>>>(Request(HttpMethod.Get, path), ct).ConfigureAwait(false);
        return result.Data.Select(m => ToRemote(m, typeId, [])).ToList();
    }

    public Task<IReadOnlyList<RemoteAddon>> GetPopularAsync(WowInstallation installation, CancellationToken ct) =>
        SearchAsync("", installation, ct);

    public async Task<IReadOnlyDictionary<string, RemoteAddon>> GetByIdsAsync(IReadOnlyCollection<string> externalIds, WowInstallation installation, CancellationToken ct)
    {
        var ids = externalIds.Select(id => int.TryParse(id, out var n) ? n : 0).Where(n => n > 0).Distinct().ToList();
        var result = new Dictionary<string, RemoteAddon>();
        if (ids.Count == 0) return result;
        var typeId = WowClients.CurseGameVersionTypeId(installation.Group);

        var mods = new List<CfMod>();
        foreach (var chunk in ids.Chunk(200))
        {
            var response = await Http.GetJsonAsync<CfResponse<List<CfMod>>>(
                Request(HttpMethod.Post, "/v1/mods", new { modIds = chunk, filterPcOnly = true }), ct).ConfigureAwait(false);
            mods.AddRange(response.Data);
        }

        // latestFiles doesn't always include the newest build for every flavor; fetch any the index points at.
        var known = mods.SelectMany(m => m.LatestFiles).Select(f => f.Id).ToHashSet();
        var missing = mods
            .SelectMany(m => m.LatestFilesIndexes.Where(i => i.GameVersionTypeId == typeId)
                .GroupBy(i => i.ReleaseType)
                .Select(g => g.Max(i => i.FileId)))
            .Where(id => !known.Contains(id))
            .Distinct()
            .ToList();
        var extraFiles = new List<CfFile>();
        foreach (var chunk in missing.Chunk(200))
        {
            var response = await Http.GetJsonAsync<CfResponse<List<CfFile>>>(
                Request(HttpMethod.Post, "/v1/mods/files", new { fileIds = chunk }), ct).ConfigureAwait(false);
            extraFiles.AddRange(response.Data);
        }

        var extraByMod = extraFiles.GroupBy(f => f.ModId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var mod in mods)
        {
            result[mod.Id.ToString()] = ToRemote(mod, typeId, extraByMod.GetValueOrDefault(mod.Id) ?? []);
        }
        return result;
    }

    public bool CanHandleUrl(Uri url) =>
        url.Host.EndsWith("curseforge.com", StringComparison.OrdinalIgnoreCase) &&
        url.AbsolutePath.StartsWith("/wow/addons/", StringComparison.OrdinalIgnoreCase);

    public async Task<RemoteAddon?> GetByUrlAsync(Uri url, WowInstallation installation, CancellationToken ct)
    {
        var parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null;
        var slug = parts[2];
        var path = $"/v1/mods/search?gameId={WowGameId}&slug={Uri.EscapeDataString(slug)}";
        var search = await Http.GetJsonAsync<CfResponse<List<CfMod>>>(Request(HttpMethod.Get, path), ct).ConfigureAwait(false);
        var mod = search.Data.FirstOrDefault(m => string.Equals(m.Slug, slug, StringComparison.OrdinalIgnoreCase)) ?? search.Data.FirstOrDefault();
        if (mod == null) return null;
        var full = await GetByIdsAsync([mod.Id.ToString()], installation, ct).ConfigureAwait(false);
        return full.GetValueOrDefault(mod.Id.ToString());
    }

    public string? GetTocHint(AddonFolder folder)
    {
        var id = folder.Toc?.CurseProjectId;
        if (string.IsNullOrWhiteSpace(id)) id = folder.Tocs.Select(t => t.CurseProjectId).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        return !string.IsNullOrWhiteSpace(id) && int.TryParse(id, out var n) && n > 0 ? n.ToString() : null;
    }

    public async Task<HttpRequestMessage> CreateDownloadRequestAsync(RemoteAddon addon, RemoteFile file, CancellationToken ct)
    {
        var url = file.DownloadUrl;
        if (string.IsNullOrEmpty(url))
        {
            try
            {
                var response = await Http.GetJsonAsync<CfResponse<string>>(
                    Request(HttpMethod.Get, $"/v1/mods/{addon.ExternalId}/files/{file.FileId}/download-url"), ct).ConfigureAwait(false);
                url = response.Data;
            }
            catch (ProviderException) { url = ""; }
        }

        if (string.IsNullOrEmpty(url))
        {
            throw new ProviderException($"The author of {addon.Name} has turned off downloads from third-party apps. Download it from the CurseForge website instead.");
        }

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (Http.IsCurseForgeHost(request.RequestUri!))
        {
            var key = apiKey();
            if (!string.IsNullOrWhiteSpace(key)) request.Headers.Add("x-api-key", key.Trim());
        }
        return request;
    }

    /// <summary>Identifies folders by fingerprint. Returns, per matched mod id, the folders it covers.</summary>
    public async Task<IReadOnlyList<FingerprintMatch>> MatchFingerprintsAsync(IReadOnlyDictionary<string, uint> folderFingerprints, CancellationToken ct)
    {
        if (folderFingerprints.Count == 0) return [];
        var byPrint = folderFingerprints.GroupBy(kv => kv.Value).ToDictionary(g => (long)g.Key, g => g.Select(kv => kv.Key).ToList());
        var matches = new List<FingerprintMatch>();
        foreach (var chunk in byPrint.Keys.Chunk(500))
        {
            var response = await Http.GetJsonAsync<CfResponse<CfFingerprintResult>>(
                Request(HttpMethod.Post, $"/v1/fingerprints/{WowGameId}", new { fingerprints = chunk }), ct).ConfigureAwait(false);
            foreach (var m in response.Data.ExactMatches)
            {
                var folders = m.File.Modules
                    .Where(mod => byPrint.ContainsKey(mod.Fingerprint))
                    .SelectMany(mod => byPrint[mod.Fingerprint])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (folders.Count == 0) continue;
                matches.Add(new FingerprintMatch(m.Id.ToString(), m.File.Id.ToString(), DisplayVersion(m.File), folders,
                    m.File.Modules.Select(x => x.Name).ToList()));
            }
        }
        return matches;
    }

    private static string DisplayVersion(CfFile f) => string.IsNullOrWhiteSpace(f.DisplayName) ? f.FileName : f.DisplayName;

    private static RemoteAddon ToRemote(CfMod mod, int typeId, List<CfFile> extraFiles)
    {
        var files = mod.LatestFiles.Concat(extraFiles)
            .Where(f => f.IsAvailable != false && f.SortableGameVersions.Any(s => s.GameVersionTypeId == typeId))
            .DistinctBy(f => f.Id)
            .OrderByDescending(f => f.FileDate)
            .Select(f => new RemoteFile
            {
                FileId = f.Id.ToString(),
                Version = DisplayVersion(f),
                FileName = f.FileName,
                DownloadUrl = f.DownloadUrl ?? "",
                ReleasedAt = f.FileDate,
                Channel = f.ReleaseType switch { 2 => AddonChannel.Beta, 3 => AddonChannel.Alpha, _ => AddonChannel.Stable },
                GameVersion = f.SortableGameVersions.Where(s => s.GameVersionTypeId == typeId)
                    .Select(s => s.GameVersionName)
                    .OrderByDescending(v => Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0))
                    .FirstOrDefault() ?? "",
                Folders = f.Modules.Select(m => m.Name).ToList(),
                Dependencies = f.Dependencies.Where(d => d.RelationType == 3)
                    .Select(d => new AddonDependency { ProviderName = ProviderNames.CurseForge, ExternalId = d.ModId.ToString() })
                    .ToList(),
            })
            .ToList();

        return new RemoteAddon
        {
            ProviderName = ProviderNames.CurseForge,
            ExternalId = mod.Id.ToString(),
            Name = mod.Name,
            Author = string.Join(", ", mod.Authors.Select(a => a.Name)),
            Summary = mod.Summary ?? "",
            ThumbnailUrl = mod.Logo?.ThumbnailUrl ?? mod.Logo?.Url ?? "",
            WebsiteUrl = mod.Links?.WebsiteUrl ?? $"https://www.curseforge.com/wow/addons/{mod.Slug}",
            DownloadCount = (long)mod.DownloadCount,
            Files = files,
            KnownFolders = files.FirstOrDefault()?.Folders ?? [],
            UnavailableReason = mod.AllowModDistribution == false
                ? "The author has turned off downloads from third-party apps."
                : "",
        };
    }

    // ---- API DTOs ----
    private sealed class CfResponse<T>
    {
        public T Data { get; set; } = default!;
    }

    private sealed class CfMod
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Slug { get; set; } = "";
        public string? Summary { get; set; }
        public CfLinks? Links { get; set; }
        public double DownloadCount { get; set; }
        public List<CfAuthor> Authors { get; set; } = [];
        public CfLogo? Logo { get; set; }
        public List<CfFile> LatestFiles { get; set; } = [];
        public List<CfFileIndex> LatestFilesIndexes { get; set; } = [];
        public bool? AllowModDistribution { get; set; }
    }

    private sealed class CfLinks
    {
        public string? WebsiteUrl { get; set; }
    }

    private sealed class CfAuthor
    {
        public string Name { get; set; } = "";
    }

    private sealed class CfLogo
    {
        public string? ThumbnailUrl { get; set; }
        public string? Url { get; set; }
    }

    private sealed class CfFile
    {
        public int Id { get; set; }
        public int ModId { get; set; }
        public string DisplayName { get; set; } = "";
        public string FileName { get; set; } = "";
        public int ReleaseType { get; set; }
        public DateTimeOffset FileDate { get; set; }
        public string? DownloadUrl { get; set; }
        public bool? IsAvailable { get; set; }
        public List<CfSortableGameVersion> SortableGameVersions { get; set; } = [];
        public List<CfDependency> Dependencies { get; set; } = [];
        public List<CfModule> Modules { get; set; } = [];
    }

    private sealed class CfSortableGameVersion
    {
        public string GameVersionName { get; set; } = "";
        public int GameVersionTypeId { get; set; }
    }

    private sealed class CfDependency
    {
        public int ModId { get; set; }
        public int RelationType { get; set; }
    }

    private sealed class CfModule
    {
        public string Name { get; set; } = "";
        public long Fingerprint { get; set; }
    }

    private sealed class CfFileIndex
    {
        public int FileId { get; set; }
        public int ReleaseType { get; set; }
        public int? GameVersionTypeId { get; set; }
    }

    private sealed class CfFingerprintResult
    {
        public List<CfFingerprintMatch> ExactMatches { get; set; } = [];
    }

    private sealed class CfFingerprintMatch
    {
        public int Id { get; set; }
        public CfFile File { get; set; } = new();
    }
}

public sealed record FingerprintMatch(string ModId, string FileId, string Version, IReadOnlyList<string> MatchedFolders, IReadOnlyList<string> FileModules);
