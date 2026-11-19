using Satchel.Core.Models;
using Satchel.Core.Toc;

namespace Satchel.Core.Scanning;

/// <summary>One directory inside Interface\AddOns.</summary>
public sealed class AddonFolder
{
    public string Name { get; init; } = "";

    public string Path { get; init; } = "";

    public IReadOnlyList<TocFile> Tocs { get; init; } = [];

    /// <summary>The TOC the current client would load.</summary>
    public TocFile? Toc { get; init; }

    public DateTime LastWriteUtc { get; init; }
}

public static class AddonFolderScanner
{
    /// <summary>Lists addon folders (those with at least one .toc) in parallel.</summary>
    public static IReadOnlyList<AddonFolder> Scan(WowInstallation installation)
    {
        var root = installation.AddonsPath;
        if (!Directory.Exists(root)) return [];

        var dirs = new DirectoryInfo(root).GetDirectories()
            .Where(d => !d.Name.StartsWith('.'))
            .ToArray();

        var results = new AddonFolder?[dirs.Length];
        Parallel.For(0, dirs.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) }, i =>
        {
            results[i] = ReadFolder(dirs[i], installation.Group);
        });

        return results.Where(r => r != null).Cast<AddonFolder>()
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static AddonFolder? ReadFolder(DirectoryInfo dir, WowClientGroup group)
    {
        try
        {
            var tocs = new List<TocFile>();
            foreach (var tocPath in Directory.EnumerateFiles(dir.FullName, "*.toc", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    tocs.Add(TocParser.Parse(tocPath));
                }
                catch (IOException) { }
            }

            if (tocs.Count == 0) return null;
            return new AddonFolder
            {
                Name = dir.Name,
                Path = dir.FullName,
                Tocs = tocs,
                Toc = TocParser.SelectForClient(dir.Name, tocs, group),
                LastWriteUtc = dir.LastWriteTimeUtc,
            };
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
