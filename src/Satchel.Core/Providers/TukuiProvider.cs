using System.Text.Json.Serialization;
using Satchel.Core.Models;
using Satchel.Core.Net;
using Satchel.Core.Scanning;

namespace Satchel.Core.Providers;

/// <summary>Tukui.org (ElvUI and Tukui). No key needed.</summary>
public sealed class TukuiProvider : IAddonProvider
{
    private const string ListUrl = "https://api.tukui.org/v1/addons";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private List<TukuiAddon>? _cache;
    private DateTime _cachedAt;

    public string Name => ProviderNames.Tukui;

    public bool IsAvailable => true;

    public string UnavailableMessage => "";

    public bool SupportsSearch => true;

    public async Task<IReadOnlyList<TukuiAddon>> GetAllAsync(CancellationToken ct)
    {
        if (_cache != null && DateTime.UtcNow - _cachedAt < Ttl) return _cache;
        _cache = await Http.GetJsonAsync<List<TukuiAddon>>(ListUrl, ct).ConfigureAwait(false);
        _cachedAt = DateTime.UtcNow;
        return _cache;
    }

    public async Task<IReadOnlyList<RemoteAddon>> SearchAsync(string query, WowInstallation installation, CancellationToken ct)
    {
        var all = await GetAllAsync(ct).ConfigureAwait(false);
        return all.Where(a => query.Length == 0 || a.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(ToRemote).ToList();
    }

    public Task<IReadOnlyList<RemoteAddon>> GetPopularAsync(WowInstallation installation, CancellationToken ct) =>
        SearchAsync("", installation, ct);

    public async Task<IReadOnlyDictionary<string, RemoteAddon>> GetByIdsAsync(IReadOnlyCollection<string> externalIds, WowInstallation installation, CancellationToken ct)
    {
        var all = await GetAllAsync(ct).ConfigureAwait(false);
        return all.Where(a => externalIds.Contains(a.Slug, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(a => a.Slug, ToRemote, StringComparer.OrdinalIgnoreCase);
    }

    public bool CanHandleUrl(Uri url) => url.Host.EndsWith("tukui.org", StringComparison.OrdinalIgnoreCase);

    public async Task<RemoteAddon?> GetByUrlAsync(Uri url, WowInstallation installation, CancellationToken ct)
    {
        var all = await GetAllAsync(ct).ConfigureAwait(false);
        var slug = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        var match = all.FirstOrDefault(a => a.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase));
        return match == null ? null : ToRemote(match);
    }

    public string? GetTocHint(AddonFolder folder) => null;

    /// <summary>Matches unclaimed folders by the addon's primary directory (e.g. "ElvUI").</summary>
    public async Task<IReadOnlyList<(TukuiAddon Addon, List<string> Folders)>> MatchFoldersAsync(IReadOnlyCollection<string> folderNames, CancellationToken ct)
    {
        var all = await GetAllAsync(ct).ConfigureAwait(false);
        var set = new HashSet<string>(folderNames, StringComparer.OrdinalIgnoreCase);
        var results = new List<(TukuiAddon, List<string>)>();
        foreach (var addon in all)
        {
            var dirs = addon.Directories ?? [];
            if (dirs.Count == 0 || !set.Contains(dirs[0])) continue;
            results.Add((addon, dirs.Where(set.Contains).ToList()));
        }
        return results;
    }

    public Task<HttpRequestMessage> CreateDownloadRequestAsync(RemoteAddon addon, RemoteFile file, CancellationToken ct) =>
        Task.FromResult(new HttpRequestMessage(HttpMethod.Get, file.DownloadUrl));

    public RemoteAddon ToRemote(TukuiAddon a) => new()
    {
        ProviderName = Name,
        ExternalId = a.Slug,
        Name = a.Name,
        Author = a.Author ?? "",
        Summary = a.SmallDesc ?? "",
        ThumbnailUrl = a.LogoSquareUrl ?? a.LogoUrl ?? "",
        WebsiteUrl = a.WebUrl ?? "https://tukui.org",
        KnownFolders = a.Directories ?? [],
        Files =
        [
            new RemoteFile
            {
                FileId = a.Version,
                Version = a.Version,
                DownloadUrl = a.Url,
                ReleasedAt = DateTimeOffset.TryParse(a.LastUpdate, out var d) ? d : DateTimeOffset.UtcNow,
                Channel = AddonChannel.Stable,
                Folders = a.Directories ?? [],
                GameVersion = a.Patch?.FirstOrDefault() ?? "",
            },
        ],
    };

    public sealed class TukuiAddon
    {
        public long Id { get; set; }
        public string Slug { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Author { get; set; }
        public string Url { get; set; } = "";
        public string Version { get; set; } = "";
        [JsonPropertyName("last_update")] public string? LastUpdate { get; set; }
        [JsonPropertyName("web_url")] public string? WebUrl { get; set; }
        [JsonPropertyName("small_desc")] public string? SmallDesc { get; set; }
        [JsonPropertyName("logo_url")] public string? LogoUrl { get; set; }
        [JsonPropertyName("logo_square_url")] public string? LogoSquareUrl { get; set; }
        public List<string>? Directories { get; set; }
        public List<string>? Patch { get; set; }
    }
}
