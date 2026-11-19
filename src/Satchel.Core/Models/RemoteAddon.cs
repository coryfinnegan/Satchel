namespace Satchel.Core.Models;

/// <summary>An addon as described by a provider (search result, update check, URL lookup).</summary>
public sealed class RemoteAddon
{
    public string ProviderName { get; init; } = "";

    public string ExternalId { get; init; } = "";

    public string Name { get; init; } = "";

    public string Author { get; init; } = "";

    public string Summary { get; init; } = "";

    public string ThumbnailUrl { get; init; } = "";

    public string WebsiteUrl { get; init; } = "";

    public long DownloadCount { get; init; }

    /// <summary>Builds usable by the requested installation, newest first.</summary>
    public List<RemoteFile> Files { get; init; } = [];

    /// <summary>Folder names the provider says this addon installs (may be empty if unknown).</summary>
    public List<string> KnownFolders { get; init; } = [];

    /// <summary>Non-empty when the provider can't serve downloads (e.g. author opted out of third-party distribution).</summary>
    public string UnavailableReason { get; init; } = "";

    /// <summary>Picks the newest build allowed by the channel (Stable &lt; Beta &lt; Alpha).</summary>
    public RemoteFile? PickFile(AddonChannel channel) =>
        Files.Where(f => f.Channel <= channel)
            .OrderByDescending(f => f.ReleasedAt)
            .FirstOrDefault();
}

public sealed class RemoteFile
{
    public string FileId { get; init; } = "";

    public string Version { get; init; } = "";

    public string FileName { get; init; } = "";

    /// <summary>May be empty when it must be resolved at download time.</summary>
    public string DownloadUrl { get; init; } = "";

    public DateTimeOffset ReleasedAt { get; init; }

    public AddonChannel Channel { get; init; }

    public string GameVersion { get; init; } = "";

    /// <summary>Top-level folders in the archive, if the provider reports them.</summary>
    public List<string> Folders { get; init; } = [];

    /// <summary>Required dependencies.</summary>
    public List<AddonDependency> Dependencies { get; init; } = [];
}
