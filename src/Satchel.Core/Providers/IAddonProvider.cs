using Satchel.Core.Models;
using Satchel.Core.Scanning;

namespace Satchel.Core.Providers;

public static class ProviderNames
{
    public const string CurseForge = "CurseForge";
    public const string WowInterface = "WoWInterface";
    public const string GitHub = "GitHub";
    public const string Tukui = "Tukui";
}

public interface IAddonProvider
{
    string Name { get; }

    /// <summary>False when the provider needs configuration (e.g. an API key).</summary>
    bool IsAvailable { get; }

    /// <summary>Explains why the provider is unavailable.</summary>
    string UnavailableMessage { get; }

    bool SupportsSearch { get; }

    Task<IReadOnlyList<RemoteAddon>> SearchAsync(string query, WowInstallation installation, CancellationToken ct);

    Task<IReadOnlyList<RemoteAddon>> GetPopularAsync(WowInstallation installation, CancellationToken ct);

    /// <summary>Batch lookup used for update checks. Missing ids are omitted from the result.</summary>
    Task<IReadOnlyDictionary<string, RemoteAddon>> GetByIdsAsync(IReadOnlyCollection<string> externalIds, WowInstallation installation, CancellationToken ct);

    bool CanHandleUrl(Uri url);

    Task<RemoteAddon?> GetByUrlAsync(Uri url, WowInstallation installation, CancellationToken ct);

    /// <summary>The external id named by a folder's TOC metadata, if this provider has one.</summary>
    string? GetTocHint(AddonFolder folder);

    Task<HttpRequestMessage> CreateDownloadRequestAsync(RemoteAddon addon, RemoteFile file, CancellationToken ct);
}

public sealed class ProviderRegistry(IEnumerable<IAddonProvider> providers)
{
    public IReadOnlyList<IAddonProvider> All { get; } = providers.ToList();

    public IAddonProvider? Get(string? name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public T? Get<T>() where T : class, IAddonProvider => All.OfType<T>().FirstOrDefault();
}
