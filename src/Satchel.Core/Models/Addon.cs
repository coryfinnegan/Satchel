using System.Text.Json.Serialization;

namespace Satchel.Core.Models;

public enum AddonChannel
{
    Stable = 0,
    Beta = 1,
    Alpha = 2,
}

public enum AddonStatus
{
    UpToDate,
    UpdateAvailable,
    Ignored,
    Unmanaged,
    Unavailable,
}

public sealed class WowInstallation
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public WowClientType ClientType { get; set; }

    /// <summary>The flavor folder, e.g. C:\Program Files (x86)\World of Warcraft\_retail_</summary>
    public string Path { get; set; } = "";

    /// <summary>Optional user label; empty means use the client's display name.</summary>
    public string Label { get; set; } = "";

    public AddonChannel DefaultChannel { get; set; } = AddonChannel.Stable;

    public bool DefaultAutoUpdate { get; set; }

    [JsonIgnore]
    public string AddonsPath => System.IO.Path.Combine(Path, "Interface", "AddOns");

    [JsonIgnore]
    public WowClientGroup Group => WowClients.Group(ClientType);

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? WowClients.DisplayName(ClientType) : Label;

    public override string ToString() => DisplayName;
}

public sealed class AddonDependency
{
    public string ProviderName { get; set; } = "";

    public string ExternalId { get; set; } = "";
}

/// <summary>An addon tracked in one installation. Unmanaged addons have no provider.</summary>
public sealed class Addon
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string InstallationId { get; set; } = "";

    public string Name { get; set; } = "";

    public string Author { get; set; } = "";

    public string Summary { get; set; } = "";

    public string ThumbnailUrl { get; set; } = "";

    public string WebsiteUrl { get; set; } = "";

    /// <summary>Provider name (see <see cref="Providers.ProviderNames"/>); empty when unmanaged.</summary>
    public string ProviderName { get; set; } = "";

    public string ExternalId { get; set; } = "";

    public AddonChannel Channel { get; set; } = AddonChannel.Stable;

    public bool AutoUpdate { get; set; }

    public bool Ignored { get; set; }

    public string InstalledVersion { get; set; } = "";

    public string InstalledFileId { get; set; } = "";

    public DateTimeOffset? InstalledAt { get; set; }

    /// <summary>Game version from the installed TOC, e.g. 12.0.5.</summary>
    public string GameVersion { get; set; } = "";

    public List<string> Folders { get; set; } = [];

    public string LatestVersion { get; set; } = "";

    public string LatestFileId { get; set; } = "";

    /// <summary>File name of the latest build (e.g. CheapUpgrades-0.18.3.zip), used to spot manual downloads.</summary>
    public string LatestFileName { get; set; } = "";

    public DateTimeOffset? LatestReleasedAt { get; set; }

    public AddonChannel? LatestChannel { get; set; }

    public long DownloadCount { get; set; }

    /// <summary>Set when the provider could not supply a build for this client (or the addon was removed).</summary>
    public string UnavailableReason { get; set; } = "";

    /// <summary>The author only allows downloads from the provider's website; updates are fetched by hand.</summary>
    public bool ManualDownloadOnly { get; set; }

    public DateTimeOffset? LastCheckedAt { get; set; }

    [JsonIgnore]
    public bool IsManaged => !string.IsNullOrEmpty(ProviderName) && !string.IsNullOrEmpty(ExternalId);

    [JsonIgnore]
    public string PrimaryFolder => Folders.Count > 0 ? Folders[0] : "";

    [JsonIgnore]
    public AddonStatus Status
    {
        get
        {
            if (!IsManaged) return AddonStatus.Unmanaged;
            if (Ignored) return AddonStatus.Ignored;
            if (!string.IsNullOrEmpty(UnavailableReason)) return AddonStatus.Unavailable;
            if (HasUpdate) return AddonStatus.UpdateAvailable;
            return AddonStatus.UpToDate;
        }
    }

    [JsonIgnore]
    public bool HasUpdate
    {
        get
        {
            if (!IsManaged || string.IsNullOrEmpty(LatestFileId) && string.IsNullOrEmpty(LatestVersion)) return false;
            if (!string.IsNullOrEmpty(InstalledFileId) && !string.IsNullOrEmpty(LatestFileId))
            {
                return !string.Equals(InstalledFileId, LatestFileId, StringComparison.Ordinal);
            }
            return !string.IsNullOrEmpty(LatestVersion)
                   && !string.Equals(Normalize(InstalledVersion), Normalize(LatestVersion), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Normalize(string v) => v.Trim().TrimStart('v', 'V');
}
