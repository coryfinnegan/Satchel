using Satchel.Core.Models;
using Satchel.Core.Net;
using Satchel.Core.Providers;
using Satchel.Core.Scanning;
using Satchel.Core.Storage;

namespace Satchel.Core.Services;

public sealed record CheckResult(int Checked, int UpdatesAvailable, IReadOnlyList<string> Errors);

public sealed record ScanResult(int Folders, int Managed, int Identified, int Unmanaged, IReadOnlyList<string> Errors);

public sealed class AddonManager(AddonStore store, SettingsStore settings, ProviderRegistry providers)
{
    // One mutation at a time per installation keeps folder swaps from racing each other.
    private readonly Dictionary<string, SemaphoreSlim> _locks = new();
    private FingerprintCache? _fingerprints;

    private FingerprintCache Fingerprints => _fingerprints ??= new FingerprintCache(Path.Combine(AppPaths.CacheDir, "fingerprints.json"));

    public ProviderRegistry Providers => providers;

    /// <summary>Uninstall sends folders to the Recycle Bin (tests turn this off).</summary>
    public bool UseRecycleBin { get; set; } = true;

    public AddonStore Store => store;

    private SemaphoreSlim LockFor(WowInstallation installation)
    {
        lock (_locks)
        {
            if (!_locks.TryGetValue(installation.Id, out var gate)) _locks[installation.Id] = gate = new SemaphoreSlim(1, 1);
            return gate;
        }
    }

    // ------------------------------------------------------------------ scan

    /// <summary>
    /// Reconciles tracked addons with what's on disk, then tries to identify unknown folders
    /// (CurseForge fingerprints, TOC project ids, Tukui folder names).
    /// </summary>
    public async Task<ScanResult> ScanAsync(WowInstallation installation, bool identify, CancellationToken ct)
    {
        var gate = LockFor(installation);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ScanCoreAsync(installation, identify, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ScanResult> ScanCoreAsync(WowInstallation installation, bool identify, CancellationToken ct)
    {
        var errors = new List<string>();
        var existing = store.ForInstallation(installation.Id);

        // A missing AddOns folder (renamed Interface folder, unplugged drive) must not read as "everything was uninstalled".
        if (!Directory.Exists(installation.AddonsPath))
        {
            if (existing.Any(a => a.IsManaged))
            {
                errors.Add($"Couldn't find the AddOns folder for {installation.DisplayName} ({installation.AddonsPath}). Nothing was changed.");
            }
            return new ScanResult(0, existing.Count(a => a.IsManaged), 0, 0, errors);
        }

        var recovered = await Task.Run(() => ArchiveInstaller.RecoverInterruptedInstalls(installation), ct).ConfigureAwait(false);
        if (recovered > 0) errors.Add($"Restored {recovered} addon folder{(recovered == 1 ? "" : "s")} left over from an interrupted update.");

        var folders = await Task.Run(() => AddonFolderScanner.Scan(installation), ct).ConfigureAwait(false);
        var byName = folders.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        // Every directory on disk, including ones whose TOC couldn't be read this time.
        var onDisk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new DirectoryInfo(installation.AddonsPath).EnumerateDirectories()) onDisk.TryAdd(dir.Name, dir.Name);
        var result = new List<Addon>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var addon in existing.Where(a => a.IsManaged))
        {
            addon.Folders = addon.Folders.Where(onDisk.ContainsKey).Select(f => onDisk[f]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (addon.Folders.Count == 0) continue; // removed outside the app
            RefreshFromToc(addon, byName, installation.Group);
            foreach (var f in addon.Folders) claimed.Add(f);
            result.Add(addon);
        }

        var identified = 0;
        if (identify)
        {
            var unclaimed = folders.Where(f => !claimed.Contains(f.Name)).ToList();
            if (unclaimed.Count > 0)
            {
                try
                {
                    var found = await IdentifyAsync(installation, unclaimed, ct).ConfigureAwait(false);
                    foreach (var addon in found)
                    {
                        addon.Folders = addon.Folders.Where(f => !claimed.Contains(f)).ToList();
                        if (addon.Folders.Count == 0) continue;
                        foreach (var f in addon.Folders) claimed.Add(f);

                        // Same addon already tracked (e.g. a folder copied in by hand): adopt the folders.
                        var twin = result.FirstOrDefault(a => a.IsManaged
                            && a.ProviderName.Equals(addon.ProviderName, StringComparison.OrdinalIgnoreCase)
                            && a.ExternalId.Equals(addon.ExternalId, StringComparison.OrdinalIgnoreCase));
                        if (twin != null)
                        {
                            twin.Folders = twin.Folders.Concat(addon.Folders).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                            continue;
                        }

                        RefreshFromToc(addon, byName, installation.Group);
                        result.Add(addon);
                        identified++;
                    }
                }
                catch (Exception ex) when (ex is ProviderException or HttpRequestException or TaskCanceledException)
                {
                    errors.Add(ex.Message);
                    Log.Error("Identify failed", ex);
                }
            }
        }

        // Everything else is shown as an unmanaged local addon. Keep ids stable between scans.
        var oldUnmanaged = new Dictionary<string, Addon>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in existing.Where(a => !a.IsManaged)) oldUnmanaged.TryAdd(a.PrimaryFolder, a); // tolerate duplicates from older data
        var unmanagedCount = 0;
        foreach (var folder in folders.Where(f => !claimed.Contains(f.Name)))
        {
            var addon = oldUnmanaged.GetValueOrDefault(folder.Name) ?? new Addon { InstallationId = installation.Id };
            addon.ProviderName = "";
            addon.ExternalId = "";
            addon.Folders = [folder.Name];
            addon.Name = FirstNonEmpty(folder.Toc?.Title, folder.Name);
            addon.Author = folder.Toc?.Author ?? "";
            addon.Summary = folder.Toc?.Notes ?? "";
            addon.InstalledVersion = folder.Toc?.Version ?? "";
            addon.GameVersion = folder.Toc?.GameVersionFor(installation.Group) ?? "";
            addon.InstalledAt ??= new DateTimeOffset(folder.LastWriteUtc, TimeSpan.Zero);
            addon.LatestFileId = addon.LatestVersion = addon.UnavailableReason = "";
            result.Add(addon);
            unmanagedCount++;
        }

        // Only replace the rows this scan started from: rows added meanwhile (e.g. an import) survive, and a game
        // install removed while we were scanning doesn't get its rows written back.
        if (IsKnownInstallation(installation))
        {
            store.ReplaceRows(installation.Id, existing.Select(a => a.Id), result);
            store.Save();
        }
        return new ScanResult(folders.Count, result.Count - unmanagedCount, identified, unmanagedCount, errors);
    }

    private bool IsKnownInstallation(WowInstallation installation)
    {
        try
        {
            return settings.Settings.Installations.Any(i => i.Id == installation.Id);
        }
        catch (InvalidOperationException)
        {
            return true; // list changed under us; err on the side of saving
        }
    }

    private static void RefreshFromToc(Addon addon, Dictionary<string, AddonFolder> byName, WowClientGroup group)
    {
        var toc = byName.GetValueOrDefault(addon.PrimaryFolder)?.Toc;
        if (toc == null) return;
        var gv = toc.GameVersionFor(group);
        if (gv.Length > 0) addon.GameVersion = gv;
        if (string.IsNullOrWhiteSpace(addon.Name)) addon.Name = FirstNonEmpty(toc.Title, addon.PrimaryFolder);
        if (string.IsNullOrWhiteSpace(addon.InstalledVersion)) addon.InstalledVersion = toc.Version;
    }

    private async Task<List<Addon>> IdentifyAsync(WowInstallation installation, List<AddonFolder> unclaimed, CancellationToken ct)
    {
        var found = new List<Addon>();
        var remaining = new Dictionary<string, AddonFolder>(unclaimed.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var curse = providers.Get<CurseForgeProvider>();

        // 1. CurseForge fingerprints: exact file match, so we know the installed file id too.
        if (curse is { IsAvailable: true } && settings.Settings.IdentifyUnknownAddons)
        {
            var prints = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            await Task.Run(() =>
            {
                var computed = new System.Collections.Concurrent.ConcurrentDictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
                Parallel.ForEach(remaining.Values, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount), CancellationToken = ct }, f =>
                {
                    try
                    {
                        computed[f.Name] = CurseFingerprint.FingerprintFolder(f.Path, Fingerprints);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log.Error($"Fingerprint failed for {f.Name}", ex);
                    }
                });
                foreach (var kv in computed) prints[kv.Key] = kv.Value;
            }, ct).ConfigureAwait(false);
            Fingerprints.Save();

            var matches = await curse.MatchFingerprintsAsync(prints, ct).ConfigureAwait(false);
            foreach (var m in matches.OrderByDescending(m => m.MatchedFolders.Count))
            {
                var foldersLeft = m.MatchedFolders.Where(remaining.ContainsKey).ToList();
                if (foldersLeft.Count == 0) continue;
                found.Add(new Addon
                {
                    InstallationId = installation.Id,
                    ProviderName = ProviderNames.CurseForge,
                    ExternalId = m.ModId,
                    InstalledFileId = m.FileId,
                    InstalledVersion = m.Version,
                    Channel = installation.DefaultChannel,
                    AutoUpdate = installation.DefaultAutoUpdate,
                    Folders = ArchiveInstaller.OrderFolders(foldersLeft, installation.AddonsPath, installation.Group),
                });
                foreach (var f in foldersLeft) remaining.Remove(f);
            }
        }

        // 2. TOC metadata (X-Curse-Project-ID / X-WoWI-ID). Folders sharing an id are one addon.
        foreach (var provider in providers.All.Where(p => p.IsAvailable))
        {
            var groups = remaining.Values
                .Select(f => (Folder: f, Id: provider.GetTocHint(f)))
                .Where(x => x.Id != null)
                .GroupBy(x => x.Id!)
                .ToList();
            foreach (var g in groups)
            {
                var names = g.Select(x => x.Folder.Name).ToList();
                var ordered = ArchiveInstaller.OrderFolders(names, installation.AddonsPath, installation.Group);
                found.Add(new Addon
                {
                    InstallationId = installation.Id,
                    ProviderName = provider.Name,
                    ExternalId = g.Key,
                    InstalledVersion = remaining[ordered[0]].Toc?.Version ?? "",
                    Channel = installation.DefaultChannel,
                    AutoUpdate = installation.DefaultAutoUpdate,
                    Folders = ordered,
                });
                foreach (var n in names) remaining.Remove(n);
            }
        }

        // 3. Tukui (ElvUI / Tukui) by folder name.
        var tukui = providers.Get<TukuiProvider>();
        if (tukui != null && remaining.Count > 0)
        {
            foreach (var (addon, dirs) in await tukui.MatchFoldersAsync(remaining.Keys.ToList(), ct).ConfigureAwait(false))
            {
                found.Add(new Addon
                {
                    InstallationId = installation.Id,
                    ProviderName = ProviderNames.Tukui,
                    ExternalId = addon.Slug,
                    Name = addon.Name,
                    InstalledVersion = remaining[dirs[0]].Toc?.Version ?? "",
                    Channel = installation.DefaultChannel,
                    AutoUpdate = installation.DefaultAutoUpdate,
                    Folders = dirs,
                });
                foreach (var d in dirs) remaining.Remove(d);
            }
        }

        return found;
    }

    // ------------------------------------------------------------------ updates

    public async Task<CheckResult> CheckForUpdatesAsync(WowInstallation installation, CancellationToken ct)
    {
        var addons = store.ForInstallation(installation.Id).Where(a => a.IsManaged).ToList();
        var errors = new List<string>();
        var checkedCount = 0;

        var tasks = addons.GroupBy(a => a.ProviderName, StringComparer.OrdinalIgnoreCase).Select(async group =>
        {
            var provider = providers.Get(group.Key);
            if (provider == null || !provider.IsAvailable) return;
            try
            {
                var remote = await provider.GetByIdsAsync(group.Select(a => a.ExternalId).ToList(), installation, ct).ConfigureAwait(false);
                foreach (var addon in group)
                {
                    if (remote.TryGetValue(addon.ExternalId, out var r)) ApplyRemote(addon, r);
                    else addon.UnavailableReason = $"Not found on {provider.Name}.";
                    addon.LastCheckedAt = DateTimeOffset.Now;
                    Interlocked.Increment(ref checkedCount);
                }
            }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                if (ct.IsCancellationRequested) throw;
                lock (errors) errors.Add($"{provider.Name}: {ex.Message}");
                Log.Error($"Update check failed for {provider.Name}", ex);
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);

        store.Save();
        var updates = store.ForInstallation(installation.Id).Count(a => a.Status == AddonStatus.UpdateAvailable);
        return new CheckResult(checkedCount, updates, errors);
    }

    private static void ApplyRemote(Addon addon, RemoteAddon remote)
    {
        if (remote.Name.Length > 0) addon.Name = remote.Name;
        if (remote.Author.Length > 0) addon.Author = remote.Author;
        if (remote.Summary.Length > 0) addon.Summary = remote.Summary;
        if (remote.ThumbnailUrl.Length > 0) addon.ThumbnailUrl = remote.ThumbnailUrl;
        if (remote.WebsiteUrl.Length > 0) addon.WebsiteUrl = remote.WebsiteUrl;
        if (remote.DownloadCount > 0) addon.DownloadCount = remote.DownloadCount;

        var file = remote.PickFile(addon.Channel);
        if (file == null)
        {
            addon.UnavailableReason = remote.UnavailableReason.Length > 0 ? remote.UnavailableReason : "No release for this game version.";
            return;
        }

        addon.UnavailableReason = remote.UnavailableReason;
        addon.LatestFileId = file.FileId;
        addon.LatestVersion = file.Version;
        addon.LatestReleasedAt = file.ReleasedAt;
        addon.LatestChannel = file.Channel;

        // Imported/TOC-identified addons may lack a file id; if the version string matches, adopt the id.
        if (string.IsNullOrEmpty(addon.InstalledFileId) && VersionsMatch(addon.InstalledVersion, file.Version))
        {
            addon.InstalledFileId = file.FileId;
        }
    }

    internal static bool VersionsMatch(string installed, string latest)
    {
        if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(latest)) return false;
        static string N(string v) => v.Trim().TrimStart('v', 'V');
        if (string.Equals(N(installed), N(latest), StringComparison.OrdinalIgnoreCase)) return true;
        // "1.14.0" matches "Mapster-1.14.0" but not "1.14.0.1" or "11.14.0" (no digit or ".digit" touching either end).
        var core = N(installed);
        if (core.Length < 3) return false;
        var pattern = $@"(?<!\d)(?<!\d\.){System.Text.RegularExpressions.Regex.Escape(core)}(?!\d)(?!\.\d)";
        return System.Text.RegularExpressions.Regex.IsMatch(latest, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    // ------------------------------------------------------------------ install / update / remove

    public Task<Addon> UpdateAsync(WowInstallation installation, Addon addon, IProgress<double>? progress, CancellationToken ct) =>
        InstallInternalAsync(installation, addon.ProviderName, addon.ExternalId, null, addon, progress, ct, depth: 0, forceReinstall: false);

    public Task<Addon> ReinstallAsync(WowInstallation installation, Addon addon, IProgress<double>? progress, CancellationToken ct) =>
        InstallInternalAsync(installation, addon.ProviderName, addon.ExternalId, null, addon, progress, ct, depth: 0, forceReinstall: true);

    public Task<Addon> InstallAsync(WowInstallation installation, RemoteAddon remote, IProgress<double>? progress, CancellationToken ct)
    {
        var existing = FindTracked(installation, remote.ProviderName, remote.ExternalId);
        return InstallInternalAsync(installation, remote.ProviderName, remote.ExternalId, null, existing, progress, ct, depth: 0, forceReinstall: true);
    }

    public Addon? FindTracked(WowInstallation installation, string provider, string externalId) =>
        store.ForInstallation(installation.Id).FirstOrDefault(a =>
            a.IsManaged && a.ProviderName.Equals(provider, StringComparison.OrdinalIgnoreCase) &&
            a.ExternalId.Equals(externalId, StringComparison.OrdinalIgnoreCase));

    private async Task<Addon> InstallInternalAsync(WowInstallation installation, string providerName, string externalId,
        RemoteAddon? remote, Addon? existing, IProgress<double>? progress, CancellationToken ct, int depth, bool forceReinstall)
    {
        var provider = providers.Get(providerName) ?? throw new ProviderException($"Unknown source: {providerName}");
        if (!provider.IsAvailable) throw new ProviderException(provider.UnavailableMessage);

        // Always re-resolve: search results are lightweight and update data may be stale.
        var lookup = await provider.GetByIdsAsync([externalId], installation, ct).ConfigureAwait(false);
        remote = lookup.GetValueOrDefault(externalId) ?? remote
                 ?? throw new ProviderException($"Couldn't find this addon on {provider.Name}.");

        var channel = existing?.Channel ?? installation.DefaultChannel;
        var file = remote.PickFile(channel) ?? remote.PickFile(AddonChannel.Alpha)
                   ?? throw new ProviderException($"{remote.Name} has no release for {WowClients.DisplayName(installation.ClientType)}.");

        if (existing != null && !forceReinstall && existing.InstalledFileId == file.FileId && existing.InstalledFileId.Length > 0)
        {
            ApplyRemote(existing, remote);
            return existing;
        }

        var request = await provider.CreateDownloadRequestAsync(remote, file, ct).ConfigureAwait(false);
        Directory.CreateDirectory(AppPaths.DownloadsDir);
        var zip = Path.Combine(AppPaths.DownloadsDir, $"{Guid.NewGuid():N}.zip");
        var downloadProgress = progress == null ? null : new Progress<double>(p => progress.Report(p * 0.85));

        var gate = LockFor(installation);
        try
        {
            await Http.DownloadToFileAsync(request, zip, downloadProgress, ct).ConfigureAwait(false);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            Addon addon;
            try
            {
                var others = store.ForInstallation(installation.Id)
                    .Where(a => a.IsManaged && a.Id != existing?.Id)
                    .SelectMany(a => a.Folders)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var previous = existing?.Folders ?? [];
                var installed = await Task.Run(() => ArchiveInstaller.Install(installation, zip, previous, others), ct).ConfigureAwait(false);
                progress?.Report(0.95);

                addon = existing ?? new Addon
                {
                    InstallationId = installation.Id,
                    Channel = installation.DefaultChannel,
                    AutoUpdate = installation.DefaultAutoUpdate,
                };
                addon.ProviderName = provider.Name;
                addon.ExternalId = remote.ExternalId;
                addon.InstalledVersion = file.Version;
                addon.InstalledFileId = file.FileId;
                addon.InstalledAt = DateTimeOffset.Now;
                addon.Folders = ArchiveInstaller.OrderFolders(installed, installation.AddonsPath, installation.Group, remote.KnownFolders.FirstOrDefault());
                ApplyRemote(addon, remote);
                addon.LatestFileId = file.FileId;
                addon.LatestVersion = file.Version;
                addon.LatestReleasedAt = file.ReleasedAt;

                var toc = AddonFolderScanner.ReadFolder(new DirectoryInfo(Path.Combine(installation.AddonsPath, addon.PrimaryFolder)), installation.Group)?.Toc;
                if (toc != null) addon.GameVersion = toc.GameVersionFor(installation.Group);

                // Unmanaged entries for folders this addon now owns are superseded.
                var owned = addon.Folders.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var stale in store.ForInstallation(installation.Id).Where(a => !a.IsManaged && a.Folders.Any(owned.Contains)))
                {
                    store.Remove(stale);
                }
                store.Upsert(addon);
                store.Save();
            }
            finally
            {
                gate.Release();
            }

            if (settings.Settings.InstallRequiredDependencies && depth < 4)
            {
                foreach (var dep in file.Dependencies)
                {
                    if (FindTracked(installation, dep.ProviderName, dep.ExternalId) != null) continue;
                    try
                    {
                        await InstallInternalAsync(installation, dep.ProviderName, dep.ExternalId, null, null, null, ct, depth + 1, forceReinstall: false).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is ProviderException or HttpRequestException or InvalidOperationException)
                    {
                        Log.Error($"Dependency {dep.ExternalId} of {addon.Name} failed", ex);
                    }
                }
            }

            progress?.Report(1);
            Log.Info($"Installed {addon.Name} {addon.InstalledVersion} into {installation.DisplayName}");
            return addon;
        }
        finally
        {
            try
            {
                File.Delete(zip);
            }
            catch { }
        }
    }

    public async Task UninstallAsync(WowInstallation installation, Addon addon, CancellationToken ct)
    {
        var gate = LockFor(installation);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var others = store.ForInstallation(installation.Id)
                .Where(a => a.IsManaged && a.Id != addon.Id)
                .SelectMany(a => a.Folders)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var toDelete = addon.Folders.Where(f => !others.Contains(f)).ToList();
            await Task.Run(() => ArchiveInstaller.DeleteFolders(installation, toDelete, UseRecycleBin), ct).ConfigureAwait(false);
            store.Remove(addon);
            store.Save();
            Log.Info($"Removed {addon.Name} from {installation.DisplayName}");
        }
        finally
        {
            gate.Release();
        }
    }

    public void Save(Addon addon)
    {
        store.Upsert(addon);
        store.Save();
    }

    private static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
}
