using System.IO.Compression;
using Satchel.Core.Models;
using Satchel.Core.Net;
using Satchel.Core.Providers;
using Satchel.Core.Services;
using Satchel.Core.Storage;

// Several tests point the static AppPaths at temp folders; keep them from racing each other.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Satchel.Tests;

/// <summary>Regression tests for issues found in review (data loss, races, version matching, key hosts).</summary>
public class RegressionTests
{
    private static WowInstallation MakeInstall(TempDir tmp)
    {
        var path = Path.Combine(tmp.Path, "World of Warcraft", "_retail_");
        Directory.CreateDirectory(Path.Combine(path, "Interface", "AddOns"));
        return new WowInstallation { Id = "inst", ClientType = WowClientType.Retail, Path = path };
    }

    private static string Zip(TempDir tmp, params (string Path, string Content)[] entries)
    {
        var zipPath = Path.Combine(tmp.Path, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (path, content) in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(path).Open());
            w.Write(content);
        }
        return zipPath;
    }

    private static (AddonManager Manager, AddonStore Store, SettingsStore Settings) MakeManager(TempDir tmp, WowInstallation inst)
    {
        AppPaths.DataDir = Path.Combine(tmp.Path, "data");
        AppPaths.LocalDir = Path.Combine(tmp.Path, "local");
        var settings = new SettingsStore(new PlainProtector());
        settings.Settings.Installations.Add(inst);
        var store = new AddonStore();
        var manager = new AddonManager(store, settings, new ProviderRegistry([new TukuiProvider()])) { UseRecycleBin = false };
        return (manager, store, settings);
    }

    [Fact]
    public void Flavor_named_folder_is_not_renamed_onto_another_addon()
    {
        using var tmp = new TempDir();
        var inst = MakeInstall(tmp);
        tmp.Write(@"World of Warcraft\_retail_\Interface\AddOns\Foo\Foo.toc", "## Title: The real Foo");
        tmp.Write(@"World of Warcraft\_retail_\Interface\AddOns\Foo\Core.lua", "-- keep me");

        var zip = Zip(tmp, ("Foo_Classic/Foo_Classic.toc", "## Title: Classic module"), ("Bar_Vanilla/Bar.toc", "## Title: Bar"));
        var folders = ArchiveInstaller.Install(inst, zip, [], new HashSet<string>());

        Assert.Equal(["Bar_Vanilla", "Foo_Classic"], folders.OrderBy(f => f));
        Assert.True(File.Exists(Path.Combine(inst.AddonsPath, "Foo", "Core.lua")));
        Assert.Contains("The real Foo", File.ReadAllText(Path.Combine(inst.AddonsPath, "Foo", "Foo.toc")));
    }

    [Theory]
    [InlineData("Flat_Mainline.toc", null, "Flat_Mainline")]
    [InlineData("Flat_Mainline.toc", "Flat_Vanilla.toc", "Flat")]
    [InlineData("Flat.toc", "Flat_Mainline.toc", "Flat")]
    public void Root_level_tocs_pick_a_loadable_folder_name(string toc1, string? toc2, string expected)
    {
        using var tmp = new TempDir();
        var inst = MakeInstall(tmp);
        var entries = new List<(string, string)> { (toc1, "## Title: x"), ("Core.lua", "x") };
        if (toc2 != null) entries.Add((toc2, "## Title: x"));
        Assert.Equal([expected], ArchiveInstaller.Install(inst, Zip(tmp, [.. entries]), [], new HashSet<string>()));
    }

    [Fact]
    public void Failed_swap_rolls_back_every_folder()
    {
        using var tmp = new TempDir();
        var inst = MakeInstall(tmp);
        tmp.Write(@"World of Warcraft\_retail_\Interface\AddOns\A\A.toc", "## Version: old-a");
        var lockedFile = tmp.Write(@"World of Warcraft\_retail_\Interface\AddOns\B\B.toc", "## Version: old-b");
        var zip = Zip(tmp, ("A/A.toc", "## Version: new-a"), ("B/B.toc", "## Version: new-b"));

        // An open file (like WoW or an editor holding it) makes renaming B fail half-way through the swap.
        using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var ex = Assert.Throws<IOException>(() => ArchiveInstaller.Install(inst, zip, ["A", "B"], new HashSet<string>()));
            Assert.Contains("Nothing was changed", ex.Message);
        }

        Assert.Contains("old-a", File.ReadAllText(Path.Combine(inst.AddonsPath, "A", "A.toc")));
        Assert.Contains("old-b", File.ReadAllText(Path.Combine(inst.AddonsPath, "B", "B.toc")));
        Assert.False(Directory.Exists(Path.Combine(inst.Path, "Interface", ".satchel-staging")));
    }

    [Fact]
    public void Interrupted_install_is_recovered_and_finished_ones_are_cleaned()
    {
        using var tmp = new TempDir();
        var inst = MakeInstall(tmp);
        var staging = Path.Combine(inst.Path, "Interface", ".satchel-staging");
        // Crash mid-swap: Foo was moved to the backup but the new copy never landed.
        tmp.Write(@"World of Warcraft\_retail_\Interface\.satchel-staging\crashed\old\Foo\Foo.toc", "## Version: previous");
        // Crash mid-swap after Bar's new copy landed: the previous copy wins (conservative).
        tmp.Write(@"World of Warcraft\_retail_\Interface\.satchel-staging\crashed\old\Bar\Bar.toc", "## Version: previous");
        tmp.Write(@"World of Warcraft\_retail_\Interface\AddOns\Bar\Bar.toc", "## Version: half-new");
        // A finished install whose cleanup failed: nothing to restore, just delete.
        tmp.Write(@"World of Warcraft\_retail_\Interface\.satchel-staging\done\old\Baz\Baz.toc", "## Version: previous");
        tmp.Write(@"World of Warcraft\_retail_\Interface\.satchel-staging\done\complete.marker", "x");
        tmp.Write(@"World of Warcraft\_retail_\Interface\AddOns\Baz\Baz.toc", "## Version: current");

        var restored = ArchiveInstaller.RecoverInterruptedInstalls(inst);

        Assert.Equal(2, restored);
        Assert.Contains("previous", File.ReadAllText(Path.Combine(inst.AddonsPath, "Foo", "Foo.toc")));
        Assert.Contains("previous", File.ReadAllText(Path.Combine(inst.AddonsPath, "Bar", "Bar.toc")));
        Assert.Contains("current", File.ReadAllText(Path.Combine(inst.AddonsPath, "Baz", "Baz.toc")));
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public async Task Missing_addons_folder_keeps_tracking()
    {
        using var tmp = new TempDir();
        var inst = MakeInstall(tmp);
        var (manager, store, _) = MakeManager(tmp, inst);
        store.Upsert(new Addon { InstallationId = inst.Id, ProviderName = ProviderNames.Tukui, ExternalId = "elvui", Folders = ["ElvUI"], Ignored = true });

        Directory.Delete(Path.Combine(inst.Path, "Interface"), recursive: true); // e.g. the user renamed Interface
        var result = await manager.ScanAsync(inst, identify: false, default);

        Assert.Single(result.Errors);
        var addon = Assert.Single(store.ForInstallation(inst.Id));
        Assert.True(addon.IsManaged);
        Assert.True(addon.Ignored);
    }

    [Fact]
    public async Task Unreadable_toc_does_not_drop_a_tracked_folder()
    {
        using var tmp = new TempDir();
        var inst = MakeInstall(tmp);
        var (manager, store, _) = MakeManager(tmp, inst);
        Directory.CreateDirectory(Path.Combine(inst.AddonsPath, "Foo")); // folder exists, but no readable TOC right now
        store.Upsert(new Addon { InstallationId = inst.Id, ProviderName = ProviderNames.Tukui, ExternalId = "foo", Folders = ["Foo"] });

        await manager.ScanAsync(inst, identify: false, default);

        Assert.Single(store.ForInstallation(inst.Id), a => a.IsManaged);
    }

    [Fact]
    public async Task Duplicate_unmanaged_rows_do_not_break_scans()
    {
        using var tmp = new TempDir();
        var inst = MakeInstall(tmp);
        var (manager, store, _) = MakeManager(tmp, inst);
        tmp.Write(@"World of Warcraft\_retail_\Interface\AddOns\Loose\Loose.toc", "## Title: Loose");
        store.Upsert(new Addon { InstallationId = inst.Id, Folders = ["Loose"] });
        store.Upsert(new Addon { InstallationId = inst.Id, Folders = ["Loose"] });

        var result = await manager.ScanAsync(inst, identify: false, default);

        Assert.Equal(1, result.Unmanaged);
        Assert.Single(store.ForInstallation(inst.Id));
    }

    [Fact]
    public void Replace_rows_keeps_rows_added_during_a_scan()
    {
        var store = new AddonStore();
        var scanned = new Addon { InstallationId = "i", Name = "scanned" };
        store.Upsert(scanned);
        var snapshot = store.ForInstallation("i").Select(a => a.Id).ToList();
        store.Upsert(new Addon { InstallationId = "i", Name = "imported meanwhile" });

        store.ReplaceRows("i", snapshot, [new Addon { InstallationId = "i", Name = "rescanned" }]);

        Assert.Equal(["imported meanwhile", "rescanned"], store.ForInstallation("i").Select(a => a.Name).OrderBy(n => n));
    }

    [Theory]
    [InlineData("1.14.0", "Mapster-1.14.0", true)]
    [InlineData("1.14.0", "Mapster-1.14.0.zip", true)]
    [InlineData("v12.0.44", "12.0.44", true)]
    [InlineData("v2.3", "v2.3.1", false)]
    [InlineData("1.1", "1.10", false)]
    [InlineData("4.0", "14.0", false)]
    [InlineData("", "1.0", false)]
    public void Version_match_respects_number_boundaries(string installed, string latest, bool expected)
    {
        Assert.Equal(expected, AddonManager.VersionsMatch(installed, latest));
    }

    [Theory]
    [InlineData("https://edge.forgecdn.net/files/1/2/x.zip", true)]
    [InlineData("https://api.curseforge.com/v1/mods", true)]
    [InlineData("https://evilforgecdn.net/x.zip", false)]
    [InlineData("https://forgecdn.net.evil.example/x.zip", false)]
    [InlineData("http://edge.forgecdn.net/x.zip", false)]
    public void Curse_key_only_goes_to_curse_hosts(string url, bool expected)
    {
        Assert.Equal(expected, Http.IsCurseForgeHost(new Uri(url)));
    }
}
