using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Satchel.Core.Models;
using Satchel.Core.Net;
using Satchel.Core.Scanning;

namespace Satchel.Core.Providers;

/// <summary>WoWInterface via the public MMOUI v3 API. No key needed.</summary>
public sealed partial class WowInterfaceProvider(string cacheDirectory) : IAddonProvider
{
    private const string ApiBase = "https://api.mmoui.com/v3/game/WOW";
    private static readonly TimeSpan ListTtl = TimeSpan.FromHours(6);

    private readonly SemaphoreSlim _listLock = new(1, 1);
    private List<WowiListItem>? _list;
    private DateTime _listLoadedUtc;

    public string Name => ProviderNames.WowInterface;

    public bool IsAvailable => true;

    public string UnavailableMessage => "";

    public bool SupportsSearch => true;

    public async Task<IReadOnlyList<RemoteAddon>> SearchAsync(string query, WowInstallation installation, CancellationToken ct)
    {
        var list = await GetListAsync(ct).ConfigureAwait(false);
        var q = query.Trim();
        return list
            .Where(i => IsCompatible(i, installation.Group))
            .Where(i => q.Length == 0
                        || i.UIName.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || i.UIAuthorName.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || (i.UIDir ?? []).Any(d => d.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(i => q.Length > 0 && i.UIName.Equals(q, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(i => i.UIDownloadMonthly)
            .Take(50)
            .Select(ToRemote)
            .ToList();
    }

    public Task<IReadOnlyList<RemoteAddon>> GetPopularAsync(WowInstallation installation, CancellationToken ct) =>
        SearchAsync("", installation, ct);

    public async Task<IReadOnlyDictionary<string, RemoteAddon>> GetByIdsAsync(IReadOnlyCollection<string> externalIds, WowInstallation installation, CancellationToken ct)
    {
        var ids = externalIds.Where(id => long.TryParse(id, out _)).Distinct().ToList();
        var result = new Dictionary<string, RemoteAddon>();
        if (ids.Count == 0) return result;

        Dictionary<string, WowiListItem>? listById = null;
        if (_list != null) listById = _list.ToDictionary(i => i.UID);

        foreach (var chunk in ids.Chunk(40))
        {
            var details = await Http.GetJsonAsync<List<WowiDetails>>($"{ApiBase}/filedetails/{string.Join(',', chunk)}.json", ct).ConfigureAwait(false);
            foreach (var d in details)
            {
                var listItem = listById?.GetValueOrDefault(d.UID);
                result[d.UID] = new RemoteAddon
                {
                    ProviderName = Name,
                    ExternalId = d.UID,
                    Name = d.UIName,
                    Author = d.UIAuthorName,
                    Summary = Summarize(d.UIDescription),
                    ThumbnailUrl = listItem?.UIIMG_Thumbs?.FirstOrDefault() ?? "",
                    WebsiteUrl = $"https://www.wowinterface.com/downloads/info{d.UID}",
                    DownloadCount = listItem?.UIDownloadTotal ?? d.UIHitCount,
                    KnownFolders = d.UIDir ?? listItem?.UIDir ?? [],
                    Files =
                    [
                        new RemoteFile
                        {
                            FileId = d.UIDate.ToString(),
                            Version = d.UIVersion,
                            FileName = d.UIFileName,
                            DownloadUrl = d.UIDownload,
                            ReleasedAt = DateTimeOffset.FromUnixTimeMilliseconds(d.UIDate),
                            Channel = AddonChannel.Stable,
                            Folders = d.UIDir ?? listItem?.UIDir ?? [],
                        },
                    ],
                };
            }
        }
        return result;
    }

    public bool CanHandleUrl(Uri url) => url.Host.EndsWith("wowinterface.com", StringComparison.OrdinalIgnoreCase);

    public async Task<RemoteAddon?> GetByUrlAsync(Uri url, WowInstallation installation, CancellationToken ct)
    {
        var m = UrlIdRegex().Match(url.AbsolutePath + url.Query);
        if (!m.Success) return null;
        var id = m.Groups[1].Value;
        await GetListAsync(ct).ConfigureAwait(false);
        var found = await GetByIdsAsync([id], installation, ct).ConfigureAwait(false);
        return found.GetValueOrDefault(id);
    }

    public string? GetTocHint(AddonFolder folder)
    {
        var id = folder.Toc?.WowInterfaceId;
        if (string.IsNullOrWhiteSpace(id)) id = folder.Tocs.Select(t => t.WowInterfaceId).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        return !string.IsNullOrWhiteSpace(id) && long.TryParse(id, out var n) && n > 0 ? n.ToString() : null;
    }

    public Task<HttpRequestMessage> CreateDownloadRequestAsync(RemoteAddon addon, RemoteFile file, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(file.DownloadUrl)) throw new ProviderException($"WoWInterface has no download for {addon.Name}.");
        return Task.FromResult(new HttpRequestMessage(HttpMethod.Get, file.DownloadUrl));
    }

    private static bool IsCompatible(WowiListItem item, WowClientGroup group)
    {
        if (item.UICompatibility == null || item.UICompatibility.Count == 0) return true;
        foreach (var c in item.UICompatibility)
        {
            if (!Version.TryParse(c.Version, out var v)) continue;
            var ok = group switch
            {
                WowClientGroup.Retail => v.Major >= 10,
                WowClientGroup.Mists => v.Major == 5,
                WowClientGroup.Cata => v.Major == 4,
                WowClientGroup.Wotlk => v.Major == 3,
                WowClientGroup.BurningCrusade => v.Major == 2,
                WowClientGroup.Classic => v.Major == 1 && v.Minor < 60,
                WowClientGroup.Forever => v.Major == 1,
                _ => true,
            };
            if (ok) return true;
        }
        return false;
    }

    private RemoteAddon ToRemote(WowiListItem i) => new()
    {
        ProviderName = Name,
        ExternalId = i.UID,
        Name = i.UIName,
        Author = i.UIAuthorName,
        ThumbnailUrl = i.UIIMG_Thumbs?.FirstOrDefault() ?? "",
        WebsiteUrl = i.UIFileInfoURL,
        DownloadCount = i.UIDownloadTotal,
        KnownFolders = i.UIDir ?? [],
        Files =
        [
            new RemoteFile
            {
                FileId = i.UIDate.ToString(),
                Version = i.UIVersion,
                ReleasedAt = DateTimeOffset.FromUnixTimeMilliseconds(i.UIDate),
                Channel = AddonChannel.Stable,
                Folders = i.UIDir ?? [],
            },
        ],
    };

    private static string Summarize(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return "";
        var text = BbCodeRegex().Replace(description, "");
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        while (text.Contains("  ")) text = text.Replace("  ", " ");
        return text.Length > 240 ? text[..240].TrimEnd() + "…" : text;
    }

    /// <summary>Loads the full file list (≈6 MB), cached on disk for a few hours.</summary>
    private async Task<List<WowiListItem>> GetListAsync(CancellationToken ct)
    {
        if (_list != null && DateTime.UtcNow - _listLoadedUtc < ListTtl) return _list;
        await _listLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_list != null && DateTime.UtcNow - _listLoadedUtc < ListTtl) return _list;
            var cachePath = Path.Combine(cacheDirectory, "wowinterface-filelist.json");
            var info = new FileInfo(cachePath);
            if (!info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc > ListTtl)
            {
                Directory.CreateDirectory(cacheDirectory);
                var tmp = cachePath + ".tmp";
                await Http.DownloadToFileAsync(new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/filelist.json"), tmp, null, ct).ConfigureAwait(false);
                File.Move(tmp, cachePath, overwrite: true);
            }

            await using var stream = File.OpenRead(cachePath);
            _list = await JsonSerializer.DeserializeAsync<List<WowiListItem>>(stream, Http.Json, ct).ConfigureAwait(false) ?? [];
            _listLoadedUtc = DateTime.UtcNow;
            return _list;
        }
        finally
        {
            _listLock.Release();
        }
    }

    [GeneratedRegex(@"(?:info|download|fileinfo\.php\?id=|getfile\.php\?id=)(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlIdRegex();

    [GeneratedRegex(@"\[/?[a-zA-Z*]+(=[^\]]*)?\]")]
    private static partial Regex BbCodeRegex();

    private sealed class WowiListItem
    {
        public string UID { get; set; } = "";
        public string UIVersion { get; set; } = "";
        public long UIDate { get; set; }
        public string UIName { get; set; } = "";
        public string UIAuthorName { get; set; } = "";
        public string UIFileInfoURL { get; set; } = "";
        public long UIDownloadTotal { get; set; }
        public long UIDownloadMonthly { get; set; }
        public List<WowiCompat>? UICompatibility { get; set; }
        public List<string>? UIDir { get; set; }
        [JsonPropertyName("UIIMG_Thumbs")]
        public List<string>? UIIMG_Thumbs { get; set; }
    }

    private sealed class WowiCompat
    {
        public string Version { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private sealed class WowiDetails
    {
        public string UID { get; set; } = "";
        public string UIName { get; set; } = "";
        public string UIVersion { get; set; } = "";
        public long UIDate { get; set; }
        public string UIFileName { get; set; } = "";
        public string UIDownload { get; set; } = "";
        public string UIAuthorName { get; set; } = "";
        public string? UIDescription { get; set; }
        public long UIHitCount { get; set; }
        public List<string>? UIDir { get; set; }
    }
}
