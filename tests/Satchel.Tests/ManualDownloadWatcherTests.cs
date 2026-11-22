using System.IO.Compression;
using Satchel.Core.Services;

namespace Satchel.Tests;

public class ManualDownloadWatcherTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(50);

    private static void WriteZip(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry("CheapUpgrades/CheapUpgrades.toc").Open());
        w.Write("## Title: Cheap Upgrades");
    }

    [Fact]
    public async Task Finds_the_download_including_browser_duplicate_names()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "Something-else.zip"), "not ours");
        File.WriteAllText(Path.Combine(tmp.Path, "CheapUpgrades-0.18.3.zip.crdownload"), "in progress");

        var wait = ManualDownload.WaitForZipAsync(tmp.Path, "CheapUpgrades-0.18.3.zip", TimeSpan.FromSeconds(10), default, Fast);
        await Task.Delay(200);
        Assert.False(wait.IsCompleted);

        WriteZip(Path.Combine(tmp.Path, "CheapUpgrades-0.18.3 (1).zip"));
        var found = await wait;
        Assert.Equal("CheapUpgrades-0.18.3 (1).zip", Path.GetFileName(found));
    }

    [Fact]
    public async Task Waits_until_the_browser_finishes_writing()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "Addon-1.0.zip");
        WriteZip(path);

        var browser = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read); // still downloading
        var wait = ManualDownload.WaitForZipAsync(tmp.Path, "Addon-1.0.zip", TimeSpan.FromSeconds(10), default, Fast);
        await Task.Delay(300);
        Assert.False(wait.IsCompleted);

        browser.Dispose(); // download finished
        Assert.Equal(path, await wait);
    }

    [Fact]
    public async Task Ignores_incomplete_archives_and_times_out()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "Addon-1.0.zip"), ""); // Firefox-style empty placeholder
        var found = await ManualDownload.WaitForZipAsync(tmp.Path, "Addon-1.0.zip", TimeSpan.FromMilliseconds(300), default, Fast);
        Assert.Null(found);
    }

    [Theory]
    [InlineData("https://www.curseforge.com/wow/addons/cheap-upgrades", "9736680", "https://www.curseforge.com/wow/addons/cheap-upgrades/download/9736680")]
    [InlineData("https://www.curseforge.com/wow/addons/cheap-upgrades/", "9736680", "https://www.curseforge.com/wow/addons/cheap-upgrades/download/9736680")]
    [InlineData("https://www.curseforge.com/wow/addons/cheap-upgrades", "", "https://www.curseforge.com/wow/addons/cheap-upgrades")]
    [InlineData("https://www.wowinterface.com/downloads/info5108", "1", "https://www.wowinterface.com/downloads/info5108")]
    public void Builds_the_curseforge_download_page(string website, string fileId, string expected)
    {
        Assert.Equal(expected, ManualDownload.DownloadPageUrl(website, fileId));
    }
}
