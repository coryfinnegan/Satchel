using System.Diagnostics;
using System.IO.Compression;
using Satchel.Core.Models;
using Satchel.Core.Providers;
using Satchel.Core.Scanning;
using Satchel.Core.Services;
using Satchel.Core.Storage;
using Xunit.Abstractions;

namespace Satchel.Tests;

public class InstallFromFileTests
{
    private static WowInstallation MakeInstall(TempDir tmp)
    {
        var path = Path.Combine(tmp.Path, "World of Warcraft", "_retail_");
        Directory.CreateDirectory(Path.Combine(path, "Interface", "AddOns"));
        return new WowInstallation { Id = "inst", ClientType = WowClientType.Retail, Path = path };
    }

    private static string Zip(TempDir tmp, params (string Path, string Content)[] entries)
    {
        var zipPath = Path.Combine(tmp.Path, "downloaded.zip");
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (path, content) in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(path).Open());
            w.Write(content);
        }
        return zipPath;
    }

    [Fact]
    public async Task Installs_a_downloaded_zip_identifies_it_and_keeps_the_users_file()
    {
        using var tmp = new TempDir();
        var inst = MakeInstall(tmp);
        var settings = new SettingsStore(new PlainProtector());
        settings.Settings.Installations.Add(inst);
        var store = new AddonStore();
        var manager = new AddonManager(store, settings, new ProviderRegistry([new WowInterfaceProvider(tmp.Path)])) { UseRecycleBin = false };

        // An older copy is already tracked (ignored by the user) under the same source id.
        Directory.CreateDirectory(Path.Combine(inst.AddonsPath, "Clique"));
        File.WriteAllText(Path.Combine(inst.AddonsPath, "Clique", "Clique.toc"), "## Version: old\n## X-WoWI-ID: 5108");
        store.Upsert(new Addon { InstallationId = inst.Id, ProviderName = ProviderNames.WowInterface, ExternalId = "5108", Folders = ["Clique"], Ignored = true });

        var zip = Zip(tmp, ("Clique/Clique.toc", "## Title: Clique\n## Version: v9.9\n## X-WoWI-ID: 5108"), ("Clique/Clique.lua", "-- new"));
        var row = await manager.InstallFromFileAsync(inst, zip, default);

        Assert.NotNull(row);
        Assert.True(row!.IsManaged);
        Assert.Equal("5108", row.ExternalId);
        Assert.Equal("v9.9", row.InstalledVersion);   // re-read from the new files
        Assert.True(row.Ignored);                      // user's choice carried over
        Assert.Single(store.ForInstallation(inst.Id));
        Assert.True(File.Exists(zip));                 // never delete the user's download
        Assert.True(File.Exists(Path.Combine(inst.AddonsPath, "Clique", "Clique.lua")));
    }

    [Fact]
    public void Manual_download_addons_still_report_updates()
    {
        var a = new Addon
        {
            ProviderName = ProviderNames.CurseForge, ExternalId = "1", ManualDownloadOnly = true,
            InstalledFileId = "10", LatestFileId = "11",
        };
        Assert.Equal(AddonStatus.UpdateAvailable, a.Status);
    }
}

/// <summary>Live CurseForge checks. Run with SATCHEL_LIVE=1 and SATCHEL_CF_KEY set to your key.</summary>
public class CurseForgeLiveTests(ITestOutputHelper output)
{
    private static string? Key => Environment.GetEnvironmentVariable("SATCHEL_CF_KEY");

    private static bool Enabled => Environment.GetEnvironmentVariable("SATCHEL_LIVE") == "1" && !string.IsNullOrWhiteSpace(Key);

    [Fact]
    public async Task Author_opt_out_is_reported_as_manual_download_not_unavailable()
    {
        if (!Enabled) return;
        var cf = new CurseForgeProvider(() => Key);
        var forever = new WowInstallation { ClientType = WowClientType.Forever, Path = Path.GetTempPath() };
        var remote = (await cf.GetByIdsAsync(["1796167"], forever, default))["1796167"]; // Cheap Upgrades
        output.WriteLine($"{remote.Name}: manual={remote.ManualDownloadOnly} unavailable='{remote.UnavailableReason}' files={remote.Files.Count}");
        Assert.True(remote.ManualDownloadOnly);
        Assert.Equal("", remote.UnavailableReason);
        Assert.NotEmpty(remote.Files);
    }

    [Fact]
    public async Task Real_addon_folders_are_recognized_by_fingerprint()
    {
        if (!Enabled) return;
        var install = InstallationDetector.Detect().FirstOrDefault(i => i.ClientType == WowClientType.Retail);
        if (install == null) return;

        var folders = AddonFolderScanner.Scan(install);
        var sw = Stopwatch.StartNew();
        var prints = folders.AsParallel().ToDictionary(f => f.Name, f => CurseFingerprint.FingerprintFolder(f.Path), StringComparer.OrdinalIgnoreCase);
        var hashMs = sw.ElapsedMilliseconds;
        var matches = await new CurseForgeProvider(() => Key).MatchFingerprintsAsync(prints, default);

        var matched = matches.SelectMany(m => m.MatchedFolders).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        output.WriteLine($"{folders.Count} folders hashed in {hashMs} ms; {matched} matched exactly across {matches.Count} CurseForge files");
        foreach (var m in matches.Take(8)) output.WriteLine($"  mod {m.ModId} file {m.FileId} {m.Version}: {string.Join(", ", m.MatchedFolders)}");
        Assert.True(matched >= folders.Count / 2, "most folders installed from CurseForge should match exactly");
    }
}
