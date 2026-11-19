using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Satchel.Core.Models;
using Satchel.Core.Net;
using Satchel.Core.Scanning;

namespace Satchel.Core.Providers;

/// <summary>GitHub Releases. Install by repository URL. An optional token raises the rate limit.</summary>
public sealed partial class GitHubProvider(Func<string?> token) : IAddonProvider
{
    private const string ApiBase = "https://api.github.com";

    public string Name => ProviderNames.GitHub;

    public bool IsAvailable => true;

    public string UnavailableMessage => "";

    public bool SupportsSearch => false;

    public Task<IReadOnlyList<RemoteAddon>> SearchAsync(string query, WowInstallation installation, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RemoteAddon>>([]);

    public Task<IReadOnlyList<RemoteAddon>> GetPopularAsync(WowInstallation installation, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RemoteAddon>>([]);

    private HttpRequestMessage Api(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        var t = token();
        if (!string.IsNullOrWhiteSpace(t)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t.Trim());
        return request;
    }

    public async Task<IReadOnlyDictionary<string, RemoteAddon>> GetByIdsAsync(IReadOnlyCollection<string> externalIds, WowInstallation installation, CancellationToken ct)
    {
        var result = new Dictionary<string, RemoteAddon>(StringComparer.OrdinalIgnoreCase);
        using var gate = new SemaphoreSlim(4);
        var tasks = externalIds.Distinct(StringComparer.OrdinalIgnoreCase).Select(async id =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var remote = await GetRepoAsync(id, installation, includeRepoInfo: false, ct).ConfigureAwait(false);
                if (remote != null)
                {
                    lock (result) result[id] = remote;
                }
            }
            catch (ProviderException) when (externalIds.Count > 1)
            {
                // One bad repo shouldn't fail the whole check.
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return result;
    }

    public bool CanHandleUrl(Uri url) => url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || url.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase);

    public Task<RemoteAddon?> GetByUrlAsync(Uri url, WowInstallation installation, CancellationToken ct)
    {
        var id = RepoFromUrl(url);
        return id == null ? Task.FromResult<RemoteAddon?>(null) : GetRepoAsync(id, installation, includeRepoInfo: true, ct);
    }

    public static string? RepoFromUrl(Uri url)
    {
        var parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        return $"{parts[0]}/{repo}";
    }

    public string? GetTocHint(AddonFolder folder) => null;

    public Task<HttpRequestMessage> CreateDownloadRequestAsync(RemoteAddon addon, RemoteFile file, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, file.DownloadUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        return Task.FromResult(request);
    }

    private async Task<RemoteAddon?> GetRepoAsync(string id, WowInstallation installation, bool includeRepoInfo, CancellationToken ct)
    {
        var parts = id.Split('/');
        if (parts.Length != 2) return null;
        var owner = parts[0];
        var repoName = parts[1];

        GhRepo? repo = null;
        if (includeRepoInfo)
        {
            repo = await Http.GetJsonAsync<GhRepo>(Api($"/repos/{owner}/{repoName}"), ct).ConfigureAwait(false);
        }

        var releases = await Http.GetJsonAsync<List<GhRelease>>(Api($"/repos/{owner}/{repoName}/releases?per_page=15"), ct).ConfigureAwait(false);
        var files = new List<RemoteFile>();
        foreach (var release in releases.Where(r => !r.Draft).OrderByDescending(r => r.PublishedAt))
        {
            var channel = release.Prerelease ? AddonChannel.Beta : AddonChannel.Stable;
            if (files.Any(f => f.Channel == channel)) continue; // newest per channel is enough
            var asset = await PickAssetAsync(release, installation.Group, ct).ConfigureAwait(false);
            if (asset == null) continue;
            files.Add(new RemoteFile
            {
                FileId = asset.Id.ToString(),
                Version = string.IsNullOrWhiteSpace(release.TagName) ? release.Name ?? asset.Name : release.TagName,
                FileName = asset.Name,
                DownloadUrl = asset.BrowserDownloadUrl,
                ReleasedAt = release.PublishedAt ?? asset.UpdatedAt ?? DateTimeOffset.UtcNow,
                Channel = channel,
            });
            if (files.Count >= 2) break;
        }

        return new RemoteAddon
        {
            ProviderName = Name,
            ExternalId = $"{owner}/{repoName}",
            Name = repo?.Name ?? repoName,
            Author = repo?.Owner?.Login ?? owner,
            Summary = repo?.Description ?? "",
            ThumbnailUrl = repo?.Owner?.AvatarUrl ?? $"https://avatars.githubusercontent.com/{owner}",
            WebsiteUrl = repo?.HtmlUrl ?? $"https://github.com/{owner}/{repoName}",
            DownloadCount = 0,
            Files = files,
            UnavailableReason = files.Count == 0 ? "No release with a .zip for this game version." : "",
        };
    }

    private async Task<GhAsset?> PickAssetAsync(GhRelease release, WowClientGroup group, CancellationToken ct)
    {
        var zips = release.Assets
            .Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .Where(a => !a.Name.Contains("nolib", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (zips.Count == 0) return null;

        // Packager metadata (BigWigs packager) lists the flavor of each zip precisely.
        var meta = release.Assets.FirstOrDefault(a => a.Name.Equals("release.json", StringComparison.OrdinalIgnoreCase));
        if (meta != null)
        {
            try
            {
                var json = await Http.GetJsonAsync<GhReleaseJson>(meta.BrowserDownloadUrl, ct).ConfigureAwait(false);
                var flavors = WowClients.PackagerFlavors(group);
                var entry = json.Releases.FirstOrDefault(r => !r.Nolib && r.Metadata.Any(m => flavors.Contains(m.Flavor, StringComparer.OrdinalIgnoreCase)))
                            ?? (group == WowClientGroup.Forever
                                ? json.Releases.FirstOrDefault(r => !r.Nolib && r.Metadata.Any(m => m.Flavor.Equals("mainline", StringComparison.OrdinalIgnoreCase)))
                                : null);
                if (entry != null)
                {
                    var match = zips.FirstOrDefault(a => a.Name.Equals(entry.Filename, StringComparison.OrdinalIgnoreCase));
                    if (match != null) return match;
                }
                else if (json.Releases.Count > 0)
                {
                    return null; // metadata says there's no build for this flavor
                }
            }
            catch (ProviderException) { }
            catch (System.Text.Json.JsonException) { }
        }

        if (zips.Count == 1) return zips[0];

        var wanted = WowClients.PackagerFlavors(group);
        var flavored = zips.FirstOrDefault(a => wanted.Any(f => HasFlavorSuffix(a.Name, f)));
        if (flavored != null) return flavored;

        // Retail (and fallbacks): the zip without any flavor suffix.
        return zips.FirstOrDefault(a => !WowClients.AllPackagerFlavorSuffixes.Any(f => HasFlavorSuffix(a.Name, f)))
               ?? zips[0];
    }

    private static bool HasFlavorSuffix(string fileName, string flavor) =>
        Regex.IsMatch(fileName, $@"[-_.]{Regex.Escape(flavor)}\.zip$", RegexOptions.IgnoreCase);

    private sealed class GhRepo
    {
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
        public GhOwner? Owner { get; set; }
    }

    private sealed class GhOwner
    {
        public string Login { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("avatar_url")]
        public string? AvatarUrl { get; set; }
    }

    private sealed class GhRelease
    {
        [System.Text.Json.Serialization.JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";
        public string? Name { get; set; }
        public bool Draft { get; set; }
        public bool Prerelease { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }
        public List<GhAsset> Assets { get; set; } = [];
    }

    private sealed class GhAsset
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("updated_at")]
        public DateTimeOffset? UpdatedAt { get; set; }
    }

    private sealed class GhReleaseJson
    {
        public List<GhReleaseJsonEntry> Releases { get; set; } = [];
    }

    private sealed class GhReleaseJsonEntry
    {
        public string Filename { get; set; } = "";
        public bool Nolib { get; set; }
        public List<GhReleaseJsonMeta> Metadata { get; set; } = [];
    }

    private sealed class GhReleaseJsonMeta
    {
        public string Flavor { get; set; } = "";
    }
}
