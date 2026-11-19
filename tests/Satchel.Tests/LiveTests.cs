using System.Diagnostics;
using Satchel.Core.Models;
using Satchel.Core.Providers;
using Satchel.Core.Scanning;
using Satchel.Core.Services;
using Satchel.Core.Storage;
using Xunit.Abstractions;

namespace Satchel.Tests;

/// <summary>Network / real-machine tests. Run with SATCHEL_LIVE=1.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SATCHEL_LIVE") != "1") Skip = "Set SATCHEL_LIVE=1 to run live tests.";
    }
}

public sealed class PlainProtector : ISecretProtector
{
    public string Protect(string plainText) => plainText;
    public string? Unprotect(string protectedText) => protectedText;
}

public class LiveTests(ITestOutputHelper output)
{
    private static WowInstallation Retail(string path) => new() { ClientType = WowClientType.Retail, Path = path };

    [LiveFact]
    public async Task WowInterface_details_and_search()
    {
        using var tmp = new TempDir();
        var p = new WowInterfaceProvider(tmp.Path);
        var inst = Retail(tmp.Path);
        var byId = await p.GetByIdsAsync(["5108"], inst, default);
        var clique = byId["5108"];
        output.WriteLine($"{clique.Name} {clique.Files[0].Version} {clique.Files[0].DownloadUrl} folders={string.Join(",", clique.KnownFolders)}");
        Assert.Equal("Clique", clique.Name);
        Assert.NotEmpty(clique.Files[0].DownloadUrl);

        var sw = Stopwatch.StartNew();
        var results = await p.SearchAsync("clique", inst, default);
        output.WriteLine($"search: {results.Count} results in {sw.ElapsedMilliseconds} ms");
        Assert.Contains(results, r => r.ExternalId == "5108");
        sw.Restart();
        await p.SearchAsync("bags", inst, default);
        output.WriteLine($"cached search in {sw.ElapsedMilliseconds} ms");
    }

    [LiveFact]
    public async Task Tukui_lists_elvui()
    {
        var p = new TukuiProvider();
        var r = await p.GetByIdsAsync(["elvui"], Retail(Path.GetTempPath()), default);
        output.WriteLine($"{r["elvui"].Name} {r["elvui"].Files[0].Version} {string.Join(",", r["elvui"].KnownFolders)}");
        Assert.NotEmpty(r["elvui"].Files[0].DownloadUrl);
    }

    [LiveFact]
    public async Task GitHub_resolves_release_asset_per_flavor()
    {
        var p = new GitHubProvider(() => null);
        var era = new WowInstallation { ClientType = WowClientType.ClassicEra, Path = Path.GetTempPath() };
        var r = await p.GetByUrlAsync(new Uri("https://github.com/Questie/Questie"), era, default);
        Assert.NotNull(r);
        foreach (var f in r!.Files) output.WriteLine($"{f.Channel} {f.Version} {f.FileName}");
        Assert.NotEmpty(r.Files);
    }

    [LiveFact]
    public void Scan_and_fingerprint_real_installs_read_only()
    {
        var found = InstallationDetector.Detect();
        foreach (var inst in found)
        {
            var sw = Stopwatch.StartNew();
            var folders = AddonFolderScanner.Scan(inst);
            var scanMs = sw.ElapsedMilliseconds;
            sw.Restart();
            var prints = folders.AsParallel().Select(f => (f.Name, CurseFingerprint.FingerprintFolder(f.Path))).ToList();
            output.WriteLine($"{inst.DisplayName} [{inst.Path}] folders={folders.Count} scan={scanMs}ms fingerprints={sw.ElapsedMilliseconds}ms");
            foreach (var f in folders.Take(5)) output.WriteLine($"   {f.Name}: '{f.Toc?.Title}' v{f.Toc?.Version} game {f.Toc?.GameVersionFor(inst.Group)} cf={f.Toc?.CurseProjectId}");
        }
        Assert.NotEmpty(found);
    }

    [LiveFact]
    public async Task End_to_end_install_update_uninstall_in_sandbox()
    {
        using var tmp = new TempDir();
        AppPaths.DataDir = Path.Combine(tmp.Path, "data");
        AppPaths.LocalDir = Path.Combine(tmp.Path, "local");
        var wow = Path.Combine(tmp.Path, "World of Warcraft", "_retail_");
        Directory.CreateDirectory(Path.Combine(wow, "Interface", "AddOns"));
        var inst = Retail(wow);

        var settings = new SettingsStore(new PlainProtector());
        settings.Settings.Installations.Add(inst);
        var store = new AddonStore();
        var registry = new ProviderRegistry([
            new CurseForgeProvider(() => null), new WowInterfaceProvider(AppPaths.CacheDir), new GitHubProvider(() => null), new TukuiProvider(),
        ]);
        var manager = new AddonManager(store, settings, registry) { UseRecycleBin = false };

        var wowi = registry.Get(ProviderNames.WowInterface)!;
        var remote = (await wowi.GetByIdsAsync(["5108"], inst, default))["5108"];
        var sw = Stopwatch.StartNew();
        var addon = await manager.InstallAsync(inst, remote, new Progress<double>(), default);
        output.WriteLine($"installed {addon.Name} {addon.InstalledVersion} folders={string.Join(",", addon.Folders)} in {sw.ElapsedMilliseconds}ms game={addon.GameVersion}");
        Assert.True(Directory.Exists(Path.Combine(inst.AddonsPath, addon.PrimaryFolder)));

        // A rescan keeps it managed and identifies nothing new.
        var scan = await manager.ScanAsync(inst, identify: true, default);
        output.WriteLine($"scan: {scan}");
        Assert.Equal(1, scan.Managed);

        var check = await manager.CheckForUpdatesAsync(inst, default);
        output.WriteLine($"check: {check.Checked} checked, {check.UpdatesAvailable} updates, errors: {string.Join("; ", check.Errors)}");
        Assert.Equal(0, check.UpdatesAvailable);

        // Drop a TOC-tagged folder in by hand and make sure the scan picks it up by X-WoWI-ID.
        var manual = Path.Combine(inst.AddonsPath, "Manual");
        Directory.CreateDirectory(manual);
        File.WriteAllText(Path.Combine(manual, "Manual.toc"), "## Title: Manual\n## X-WoWI-ID: 5108\n");
        scan = await manager.ScanAsync(inst, identify: true, default);
        output.WriteLine($"scan2: {scan}");

        await manager.UninstallAsync(inst, store.ForInstallation(inst.Id).First(a => a.ExternalId == "5108" && a.Folders.Contains(addon.PrimaryFolder)), default);
        Assert.False(Directory.Exists(Path.Combine(inst.AddonsPath, addon.PrimaryFolder)));

        // ElvUI from Tukui, multi-folder.
        var tukui = registry.Get(ProviderNames.Tukui)!;
        var elv = (await tukui.GetByIdsAsync(["elvui"], inst, default))["elvui"];
        sw.Restart();
        var elvAddon = await manager.InstallAsync(inst, elv, null, default);
        output.WriteLine($"installed {elvAddon.Name} {elvAddon.InstalledVersion} folders={string.Join(",", elvAddon.Folders)} in {sw.ElapsedMilliseconds}ms");
        Assert.Contains("ElvUI", elvAddon.Folders);
        Assert.Equal("ElvUI", elvAddon.PrimaryFolder);
    }

    [LiveFact]
    public void WowUp_import_from_this_machine()
    {
        var result = WowUpImporter.Import();
        Assert.NotNull(result);
        output.WriteLine($"source: {result!.Source}");
        foreach (var i in result.Installations) output.WriteLine($"install {i.Id} {i.DisplayName} {i.Path}");
        foreach (var g in result.Addons.GroupBy(a => a.ProviderName)) output.WriteLine($"{(g.Key == "" ? "(unmanaged)" : g.Key)}: {g.Count()}");
        output.WriteLine($"updates pending per WowUp data: {result.Addons.Count(a => a.HasUpdate)}");
    }
}
