using System.IO.Compression;
using System.Text;
using Satchel.Core.Models;
using Satchel.Core.Providers;
using Satchel.Core.Scanning;
using Satchel.Core.Services;
using Satchel.Core.Storage;
using Satchel.Core.Toc;

namespace Satchel.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "satchel-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Write(string relative, string content)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { }
    }
}

public class FingerprintTests
{
    // Expected values come from an independent, chunked MurmurHash2 (seed 1) reference over whitespace-stripped bytes.
    [Theory]
    [InlineData("", 1540447798u)]
    [InlineData("a", 626045324u)]
    [InlineData("abc", 1621425345u)]
    [InlineData("abcd", 3376380438u)]
    [InlineData("Hello World\r\n", 1756117720u)]
    [InlineData("local x = 1\n\treturn x\n", 4123519695u)]
    [InlineData("123456789", 2788329418u)]
    public void Hash_matches_reference(string input, uint expected)
    {
        Assert.Equal(expected, CurseFingerprint.Hash(Encoding.ASCII.GetBytes(input)));
    }

    [Fact]
    public void Hash_matches_reference_for_binary()
    {
        var data = Enumerable.Range(0, 1024).Select(i => (byte)(i % 256)).ToArray();
        Assert.Equal(3164413637u, CurseFingerprint.Hash(data));
    }

    [Fact]
    public void Folder_fingerprint_follows_toc_and_xml_includes_only()
    {
        using var tmp = new TempDir();
        tmp.Write(@"AddOns\Foo\Foo.toc", "## Interface: 120005\n## Title: Foo\n# comment\nCore.lua\nUI\\Frames.xml\n");
        tmp.Write(@"AddOns\Foo\Core.lua", "print('core')");
        tmp.Write(@"AddOns\Foo\UI\Frames.xml", "<Ui><!-- <Script file=\"Ignored.lua\"/> -->\n<Script file=\"Frames.lua\"/>\n</Ui>");
        tmp.Write(@"AddOns\Foo\UI\Frames.lua", "-- frames");
        tmp.Write(@"AddOns\Foo\UI\Ignored.lua", "-- not referenced");
        tmp.Write(@"AddOns\Foo\README.md", "docs");

        var folder = System.IO.Path.Combine(tmp.Path, "AddOns", "Foo");
        var print1 = CurseFingerprint.FingerprintFolder(folder);

        // Unreferenced files don't change the fingerprint...
        tmp.Write(@"AddOns\Foo\README.md", "changed docs");
        tmp.Write(@"AddOns\Foo\UI\Ignored.lua", "-- changed");
        Assert.Equal(print1, CurseFingerprint.FingerprintFolder(folder));

        // ...nor do whitespace-only edits, but real edits to included files do.
        tmp.Write(@"AddOns\Foo\Core.lua", "print( 'core' )\r\n");
        Assert.Equal(print1, CurseFingerprint.FingerprintFolder(folder));
        tmp.Write(@"AddOns\Foo\UI\Frames.lua", "-- frames v2");
        Assert.NotEqual(print1, CurseFingerprint.FingerprintFolder(folder));
    }

    [Fact]
    public void Folder_fingerprint_is_combination_of_sorted_file_hashes()
    {
        using var tmp = new TempDir();
        var toc = "## Title: Bar\nBar.lua\n";
        var lua = "return 1";
        tmp.Write(@"AddOns\Bar\Bar.toc", toc);
        tmp.Write(@"AddOns\Bar\Bar.lua", lua);
        var hashes = new[] { CurseFingerprint.Hash(Encoding.UTF8.GetBytes(toc)), CurseFingerprint.Hash(Encoding.UTF8.GetBytes(lua)) }.OrderBy(h => h);
        var expected = CurseFingerprint.Hash(Encoding.ASCII.GetBytes(string.Concat(hashes)));
        Assert.Equal(expected, CurseFingerprint.FingerprintFolder(System.IO.Path.Combine(tmp.Path, "AddOns", "Bar")));
    }
}

public class TocTests
{
    [Fact]
    public void Strips_color_and_texture_codes()
    {
        Assert.Equal("Deadly Boss Mods Core", TocParser.StripFormatting("|cffffe00aDeadly Boss Mods|r |T123:0|tCore"));
        Assert.Equal("Plain", TocParser.StripFormatting("Plain"));
    }

    [Fact]
    public void Selects_flavor_toc_then_base()
    {
        using var tmp = new TempDir();
        tmp.Write(@"Foo\Foo.toc", "## Interface: 11507\n## Title: Base");
        tmp.Write(@"Foo\Foo_Mainline.toc", "## Interface: 120005\n## Title: Retail");
        tmp.Write(@"Foo\Foo-Mists.toc", "## Interface: 50503\n## Title: Mists");
        var tocs = Directory.GetFiles(System.IO.Path.Combine(tmp.Path, "Foo"), "*.toc").Select(TocParser.Parse).ToList();

        Assert.Equal("Retail", TocParser.SelectForClient("Foo", tocs, WowClientGroup.Retail)!.Title);
        Assert.Equal("Mists", TocParser.SelectForClient("Foo", tocs, WowClientGroup.Mists)!.Title);
        Assert.Equal("Base", TocParser.SelectForClient("Foo", tocs, WowClientGroup.Classic)!.Title);
    }

    [Theory]
    [InlineData("120005", WowClientGroup.Retail, "12.0.5")]
    [InlineData("11509, 20506, 16001", WowClientGroup.Forever, "1.60.1")]
    [InlineData("11509, 20506, 16001", WowClientGroup.Classic, "1.15.9")]
    [InlineData("11509, 20506, 16001", WowClientGroup.BurningCrusade, "2.5.6")]
    [InlineData("120105, 50504", WowClientGroup.Mists, "5.5.4")]
    public void Game_version_picks_the_clients_interface(string iface, WowClientGroup group, string expected)
    {
        var toc = new TocFile { Fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Interface"] = iface } };
        Assert.Equal(expected, toc.GameVersionFor(group));
    }
}

public class InstallerTests
{
    private static string MakeZip(TempDir tmp, string name, params (string Path, string Content)[] entries)
    {
        var zipPath = System.IO.Path.Combine(tmp.Path, name);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (path, content) in entries)
        {
            var e = zip.CreateEntry(path);
            using var w = new StreamWriter(e.Open());
            w.Write(content);
        }
        return zipPath;
    }

    private static WowInstallation MakeInstall(TempDir tmp)
    {
        var path = System.IO.Path.Combine(tmp.Path, "World of Warcraft", "_retail_");
        Directory.CreateDirectory(System.IO.Path.Combine(path, "Interface", "AddOns"));
        return new WowInstallation { ClientType = WowClientType.Retail, Path = path };
    }

    [Fact]
    public void Installs_updates_and_removes_stale_folders()
    {
        using var tmp = new TempDir();
        var install = MakeInstall(tmp);
        var v1 = MakeZip(tmp, "v1.zip", ("Foo/Foo.toc", "## Version: 1"), ("Foo_Extra/Foo_Extra.toc", "## Version: 1"));
        var folders = ArchiveInstaller.Install(install, v1, [], new HashSet<string>());
        Assert.Equal(["Foo", "Foo_Extra"], folders.OrderBy(f => f));

        var v2 = MakeZip(tmp, "v2.zip", ("Foo/Foo.toc", "## Version: 2"));
        folders = ArchiveInstaller.Install(install, v2, ["Foo", "Foo_Extra"], new HashSet<string>());
        Assert.Equal(["Foo"], folders);
        Assert.False(Directory.Exists(System.IO.Path.Combine(install.AddonsPath, "Foo_Extra")));
        Assert.Contains("Version: 2", File.ReadAllText(System.IO.Path.Combine(install.AddonsPath, "Foo", "Foo.toc")));
        Assert.False(Directory.Exists(System.IO.Path.Combine(install.Path, "Interface", ".satchel-staging")));
    }

    [Fact]
    public void Protected_folders_are_never_removed_as_stale()
    {
        using var tmp = new TempDir();
        var install = MakeInstall(tmp);
        Directory.CreateDirectory(System.IO.Path.Combine(install.AddonsPath, "SharedLib"));
        var v2 = MakeZip(tmp, "v2.zip", ("Foo/Foo.toc", "## Version: 2"));
        ArchiveInstaller.Install(install, v2, ["Foo", "SharedLib"], new HashSet<string>(["SharedLib"], StringComparer.OrdinalIgnoreCase));
        Assert.True(Directory.Exists(System.IO.Path.Combine(install.AddonsPath, "SharedLib")));
    }

    [Fact]
    public void Unwraps_wrapper_folder_and_renames_to_toc()
    {
        using var tmp = new TempDir();
        var install = MakeInstall(tmp);
        var zip = MakeZip(tmp, "gh.zip", ("Repo-main/Questie-v12/Questie.toc", "## Title: Questie"), ("Repo-main/README.md", "x"));
        var folders = ArchiveInstaller.Install(install, zip, [], new HashSet<string>());
        Assert.Equal(["Questie"], folders);
        Assert.True(File.Exists(System.IO.Path.Combine(install.AddonsPath, "Questie", "Questie.toc")));
    }

    [Fact]
    public void Root_level_toc_becomes_a_folder()
    {
        using var tmp = new TempDir();
        var install = MakeInstall(tmp);
        var zip = MakeZip(tmp, "flat.zip", ("Flat.toc", "## Title: Flat"), ("Flat_Mainline.toc", "## Title: Flat"), ("Flat.lua", "x"));
        var folders = ArchiveInstaller.Install(install, zip, [], new HashSet<string>());
        Assert.Equal(["Flat"], folders);
        Assert.True(File.Exists(System.IO.Path.Combine(install.AddonsPath, "Flat", "Flat.lua")));
    }

    [Fact]
    public void Bad_archive_leaves_existing_install_untouched()
    {
        using var tmp = new TempDir();
        var install = MakeInstall(tmp);
        tmp.Write(@"World of Warcraft\_retail_\Interface\AddOns\Foo\Foo.toc", "## Version: 1");
        var notZip = tmp.Write("bad.zip", "this is not a zip");
        Assert.Throws<InvalidOperationException>(() => ArchiveInstaller.Install(install, notZip, ["Foo"], new HashSet<string>()));
        Assert.True(File.Exists(System.IO.Path.Combine(install.AddonsPath, "Foo", "Foo.toc")));
    }
}

public class ImportTests
{
    [Fact]
    public void Imports_wowup_installations_and_addons()
    {
        using var tmp = new TempDir();
        var wow = System.IO.Path.Combine(tmp.Path, "World of Warcraft", "_retail_");
        Directory.CreateDirectory(wow);
        var prefs = $$"""
        { "wow_installations": [ { "id": "inst-1", "clientType": 0, "label": "{defaultName}", "location": {{System.Text.Json.JsonSerializer.Serialize(System.IO.Path.Combine(wow, "Wow.exe"))}} } ] }
        """;
        tmp.Write(@"WowUpCf\preferences.json", prefs);
        tmp.Write(@"WowUpCf\addons.json", """
        {
          "a": { "name": "Mapster", "installationId": "inst-1", "providerName": "Curse", "externalId": "14376",
                 "installedFolders": "Mapster", "installedVersion": "1.13.1", "installedExternalReleaseId": "7727980",
                 "externalLatestReleaseId": "9047762", "latestVersion": "1.14.0", "channelType": 0, "isIgnored": false },
          "b": { "name": "Questie", "installationId": "inst-1", "providerName": "WowUpHub", "externalId": "47671",
                 "installedFolders": "Questie,QuestieTrace", "downloadUrl": "https://github.com/Questie/Questie/releases/download/v1/Questie.zip" },
          "c": { "name": "Ghost", "installationId": "missing", "providerName": "Curse", "externalId": "1", "installedFolders": "Ghost" }
        }
        """);

        var result = WowUpImporter.Import([System.IO.Path.Combine(tmp.Path, "WowUpCf")]);
        Assert.NotNull(result);
        var inst = Assert.Single(result!.Installations);
        Assert.Equal(WowClientType.Retail, inst.ClientType);
        Assert.Equal("", inst.Label);
        Assert.Equal(2, result.Addons.Count);

        var mapster = result.Addons.Single(a => a.Name == "Mapster");
        Assert.Equal(ProviderNames.CurseForge, mapster.ProviderName);
        Assert.Equal("7727980", mapster.InstalledFileId);
        Assert.True(mapster.HasUpdate);

        var questie = result.Addons.Single(a => a.Name == "Questie");
        Assert.Equal(ProviderNames.GitHub, questie.ProviderName);
        Assert.Equal("Questie/Questie", questie.ExternalId);
        Assert.Equal(["Questie", "QuestieTrace"], questie.Folders);
    }

    [Fact]
    public void Detects_flavor_folders_under_a_root()
    {
        using var tmp = new TempDir();
        var root = System.IO.Path.Combine(tmp.Path, "World of Warcraft");
        Directory.CreateDirectory(System.IO.Path.Combine(root, "_retail_", "Interface"));
        Directory.CreateDirectory(System.IO.Path.Combine(root, "_classic_era_", "Interface"));
        Directory.CreateDirectory(System.IO.Path.Combine(root, "_notwow_"));
        var found = InstallationDetector.FromRoot(root).Select(i => i.ClientType).OrderBy(t => t).ToList();
        Assert.Equal([WowClientType.Retail, WowClientType.ClassicEra], found);
    }
}

public class StatusTests
{
    [Fact]
    public void Status_prefers_file_ids_then_versions()
    {
        var a = new Addon { ProviderName = "CurseForge", ExternalId = "1", InstalledFileId = "10", LatestFileId = "10", InstalledVersion = "x", LatestVersion = "y" };
        Assert.Equal(AddonStatus.UpToDate, a.Status);
        a.LatestFileId = "11";
        Assert.Equal(AddonStatus.UpdateAvailable, a.Status);
        a.Ignored = true;
        Assert.Equal(AddonStatus.Ignored, a.Status);

        var b = new Addon { ProviderName = "WoWInterface", ExternalId = "5", InstalledVersion = "v1.2", LatestVersion = "1.2" };
        Assert.Equal(AddonStatus.UpToDate, b.Status);
        Assert.Equal(AddonStatus.Unmanaged, new Addon().Status);
    }

    [Fact]
    public void Pick_file_respects_channel()
    {
        var now = DateTimeOffset.UtcNow;
        var r = new RemoteAddon
        {
            Files =
            [
                new RemoteFile { FileId = "alpha", Channel = AddonChannel.Alpha, ReleasedAt = now },
                new RemoteFile { FileId = "beta", Channel = AddonChannel.Beta, ReleasedAt = now.AddDays(-1) },
                new RemoteFile { FileId = "stable", Channel = AddonChannel.Stable, ReleasedAt = now.AddDays(-2) },
            ],
        };
        Assert.Equal("stable", r.PickFile(AddonChannel.Stable)!.FileId);
        Assert.Equal("beta", r.PickFile(AddonChannel.Beta)!.FileId);
        Assert.Equal("alpha", r.PickFile(AddonChannel.Alpha)!.FileId);
    }
}
