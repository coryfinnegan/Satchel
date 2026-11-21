using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Satchel.Core.Services;

/// <summary>
/// Helpers for addons whose authors only allow downloads from the CurseForge website: Satchel opens the
/// download there and picks the zip up from the user's Downloads folder as soon as the browser finishes it.
/// </summary>
public static class ManualDownload
{
    /// <summary>CurseForge's own download page for a specific file (starts the download in the browser).</summary>
    public static string DownloadPageUrl(string websiteUrl, string fileId)
    {
        if (string.IsNullOrWhiteSpace(websiteUrl)) return "";
        if (string.IsNullOrWhiteSpace(fileId) || !websiteUrl.Contains("curseforge.com/wow/addons/", StringComparison.OrdinalIgnoreCase)) return websiteUrl;
        return websiteUrl.TrimEnd('/') + "/download/" + Uri.EscapeDataString(fileId);
    }

    /// <summary>
    /// Waits until a complete zip named <paramref name="expectedFileName"/> (or a browser duplicate such as
    /// "Name (1).zip") is in <paramref name="folder"/>. Returns null on timeout. Only that file name is looked at.
    /// </summary>
    public static async Task<string?> WaitForZipAsync(string folder, string expectedFileName, TimeSpan timeout, CancellationToken ct, TimeSpan? pollInterval = null)
    {
        var stem = Path.GetFileNameWithoutExtension(expectedFileName);
        if (string.IsNullOrWhiteSpace(stem)) return null;
        var pattern = new Regex("^" + Regex.Escape(stem) + @"( ?\(\d+\))?\.zip$", RegexOptions.IgnoreCase);
        var interval = pollInterval ?? TimeSpan.FromSeconds(1);
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(folder))
            {
                var candidates = Directory.EnumerateFiles(folder, "*.zip")
                    .Where(f => pattern.IsMatch(Path.GetFileName(f)))
                    .OrderByDescending(File.GetLastWriteTimeUtc);
                foreach (var file in candidates)
                {
                    if (IsCompleteZip(file)) return file;
                }
            }
            if (DateTime.UtcNow >= deadline) return null;
            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>True once the browser has finished writing (no writer holds it) and the archive reads cleanly.</summary>
    private static bool IsCompleteZip(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length == 0) return false;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            return zip.Entries.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
