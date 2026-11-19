using System.IO.Compression;
using Satchel.Core.Models;
using Satchel.Core.Storage;

namespace Satchel.Core.Services;

/// <summary>
/// Installs an addon zip into Interface\AddOns. New folders are staged on the same volume, then swapped in
/// with renames; if anything fails part-way the previous folders are put back. If even that fails (or the
/// app dies mid-swap) the previous folders stay in the staging area until <see cref="RecoverInterruptedInstalls"/>.
/// </summary>
public static class ArchiveInstaller
{
    private const string StagingFolderName = ".satchel-staging";
    private const string CompleteMarker = "complete.marker";

    private static readonly string[] FlavorSuffixes =
        ["mainline", "standard", "classic", "vanilla", "bcc", "tbc", "wrath", "wotlkc", "cata", "mists", "forever", "camelot"];

    private static string StagingRoot(WowInstallation installation) =>
        Path.Combine(installation.Path, "Interface", StagingFolderName);

    /// <param name="previousFolders">Folders the addon owned before (removed if the new build drops them).</param>
    /// <param name="protectedFolders">Folders owned by other addons, never removed as "stale".</param>
    /// <returns>The top-level folders now installed.</returns>
    public static IReadOnlyList<string> Install(WowInstallation installation, string zipPath,
        IReadOnlyCollection<string> previousFolders, IReadOnlySet<string> protectedFolders)
    {
        var addonsDir = installation.AddonsPath;
        Directory.CreateDirectory(addonsDir);
        var stagingRoot = Path.Combine(StagingRoot(installation), Guid.NewGuid().ToString("N"));
        var extractDir = Path.Combine(stagingRoot, "new");
        var backupDir = Path.Combine(stagingRoot, "old");
        Directory.CreateDirectory(extractDir);
        Directory.CreateDirectory(backupDir);
        var keepStaging = false;

        try
        {
            try
            {
                // ExtractToDirectory rejects entries that escape the destination (zip-slip).
                ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidOperationException("The download isn't a valid zip archive.", ex);
            }

            var addonDirs = FindAddonFolders(extractDir);
            if (addonDirs.Count == 0) throw new InvalidOperationException("The archive doesn't contain any addon folders.");

            var newNames = addonDirs.Select(d => d.Name).ToList();
            var movedOld = new List<string>();
            var movedNew = new List<string>();
            try
            {
                foreach (var dir in addonDirs)
                {
                    var target = Path.Combine(addonsDir, dir.Name);
                    if (Directory.Exists(target))
                    {
                        Directory.Move(target, Path.Combine(backupDir, dir.Name));
                        movedOld.Add(dir.Name);
                    }
                    Directory.Move(dir.FullName, target);
                    movedNew.Add(dir.Name);
                }

                foreach (var stale in previousFolders.Except(newNames, StringComparer.OrdinalIgnoreCase))
                {
                    if (protectedFolders.Contains(stale) || !IsSafeFolderName(stale)) continue;
                    var path = Path.Combine(addonsDir, stale);
                    if (!Directory.Exists(path)) continue;
                    Directory.Move(path, Path.Combine(backupDir, stale));
                    movedOld.Add(stale);
                }

                // The swap is done; anything left in staging from here on is just garbage.
                MarkComplete(stagingRoot);
            }
            catch (Exception ex)
            {
                if (!Rollback(addonsDir, stagingRoot, backupDir, movedNew, movedOld))
                {
                    keepStaging = true; // the next scan restores these (RecoverInterruptedInstalls)
                    throw new IOException(
                        $"The update failed part-way and the previous version couldn't be put back yet. Your previous files are safe in {backupDir}. Close World of Warcraft and press Rescan to restore them.", ex);
                }
                MarkComplete(stagingRoot);
                if (ex is IOException or UnauthorizedAccessException)
                {
                    throw new IOException("Couldn't replace the addon's files. If World of Warcraft or a file window is using them, close it and try again. Nothing was changed.", ex);
                }
                throw;
            }

            return newNames;
        }
        finally
        {
            if (!keepStaging) CleanUpStaging(stagingRoot);
        }
    }

    private static void MarkComplete(string stagingRoot)
    {
        try
        {
            File.WriteAllText(Path.Combine(stagingRoot, CompleteMarker), DateTimeOffset.Now.ToString("O"));
        }
        catch (Exception ex)
        {
            Log.Error("Could not write staging marker", ex);
        }
    }

    private static void CleanUpStaging(string stagingRoot)
    {
        TryDelete(stagingRoot);
        var stagingParent = Path.GetDirectoryName(stagingRoot)!;
        try
        {
            if (Directory.Exists(stagingParent) && !Directory.EnumerateFileSystemEntries(stagingParent).Any()) Directory.Delete(stagingParent);
        }
        catch { }
    }

    /// <summary>Undoes a partial swap. Returns false if any previous folder couldn't be put back.</summary>
    private static bool Rollback(string addonsDir, string stagingRoot, string backupDir, List<string> movedNew, List<string> movedOld)
    {
        var ok = true;
        var rejected = Path.Combine(stagingRoot, "rejected");
        foreach (var name in movedNew)
        {
            var path = Path.Combine(addonsDir, name);
            if (!Directory.Exists(path)) continue;
            try
            {
                // A rename is all-or-nothing; deleting can leave a half-empty folder behind.
                Directory.CreateDirectory(rejected);
                Directory.Move(path, Path.Combine(rejected, name));
            }
            catch (Exception)
            {
                TryDelete(path);
            }
        }

        foreach (var name in movedOld)
        {
            var target = Path.Combine(addonsDir, name);
            try
            {
                if (Directory.Exists(target))
                {
                    ok = false; // the new copy couldn't be cleared out of the way
                    continue;
                }
                Directory.Move(Path.Combine(backupDir, name), target);
            }
            catch (Exception ex)
            {
                Log.Error($"Rollback failed for {name}", ex);
                ok = false;
            }
        }
        return ok;
    }

    /// <summary>
    /// Puts back previous folders left behind by an install that crashed or couldn't roll back, then clears
    /// finished staging folders. Call with the installation's lock held (no install in progress).
    /// </summary>
    public static int RecoverInterruptedInstalls(WowInstallation installation)
    {
        var root = StagingRoot(installation);
        if (!Directory.Exists(root)) return 0;
        var restored = 0;
        foreach (var staging in Directory.GetDirectories(root))
        {
            var finished = File.Exists(Path.Combine(staging, CompleteMarker));
            var backupDir = Path.Combine(staging, "old");
            var allRestored = true;
            if (!finished && Directory.Exists(backupDir))
            {
                foreach (var old in Directory.GetDirectories(backupDir))
                {
                    var name = Path.GetFileName(old);
                    var target = Path.Combine(installation.AddonsPath, name);
                    try
                    {
                        if (Directory.Exists(target))
                        {
                            var rejected = Path.Combine(staging, "rejected");
                            Directory.CreateDirectory(rejected);
                            Directory.Move(target, Path.Combine(rejected, $"{name}-{Guid.NewGuid():N}"));
                        }
                        Directory.Move(old, target);
                        restored++;
                        Log.Info($"Restored {name} from an interrupted install");
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Could not restore {name} from {old}", ex);
                        allRestored = false;
                    }
                }
            }
            if (finished || allRestored) CleanUpStaging(staging);
        }
        return restored;
    }

    /// <summary>
    /// Finds addon folders in an extracted archive, unwrapping a single wrapper directory
    /// (e.g. "Repo-1.2.3/Addon/...") and renaming version-stamped folders to their addon name
    /// (e.g. "Questie-v12.1" → "Questie"). A folder whose name matches one of its TOCs is never renamed.
    /// </summary>
    public static List<DirectoryInfo> FindAddonFolders(string extractDir)
    {
        var root = new DirectoryInfo(extractDir);

        // Files directly at the root with a TOC: the archive *is* the addon folder.
        var rootTocs = root.GetFiles("*.toc").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (rootTocs.Count > 0)
        {
            var name = RootFolderName(rootTocs);
            if (!IsSafeFolderName(name)) throw new InvalidOperationException("The archive's addon name isn't a valid folder name.");
            var wrapped = Path.Combine(Path.GetDirectoryName(extractDir)!, "wrapped");
            Directory.CreateDirectory(wrapped);
            var dest = Path.Combine(wrapped, name);
            Directory.Move(extractDir, dest);
            Directory.CreateDirectory(extractDir);
            Directory.Move(dest, Path.Combine(extractDir, name));
            Directory.Delete(wrapped, recursive: true);
            return [new DirectoryInfo(Path.Combine(extractDir, name))];
        }

        var dirs = root.GetDirectories().Where(d => !d.Name.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase)).ToList();
        if (dirs.Count == 1 && dirs[0].GetFiles("*.toc").Length == 0 && dirs[0].GetDirectories().Any(d => d.GetFiles("*.toc").Length > 0))
        {
            root = dirs[0];
            dirs = root.GetDirectories().ToList();
        }

        var result = new List<DirectoryInfo>();
        foreach (var dir in dirs)
        {
            var tocs = dir.GetFiles("*.toc");
            if (tocs.Length == 0) continue;
            var current = dir;
            var renameTo = VersionedFolderTarget(dir.Name, tocs);
            if (renameTo != null && !dirs.Any(d => d.Name.Equals(renameTo, StringComparison.OrdinalIgnoreCase)))
            {
                var renamed = Path.Combine(dir.Parent!.FullName, renameTo);
                dir.MoveTo(renamed);
                current = new DirectoryInfo(renamed);
            }
            result.Add(current);
        }
        return result;
    }

    /// <summary>
    /// The addon name a version-stamped folder should become, or null to leave it alone.
    /// Only "Name-1.2.3"-style folders holding Name.toc / Name_Flavor.toc qualify.
    /// </summary>
    private static string? VersionedFolderTarget(string folderName, FileInfo[] tocs)
    {
        var rawNames = tocs.Select(t => Path.GetFileNameWithoutExtension(t.Name)).ToList();
        if (rawNames.Any(n => n.Equals(folderName, StringComparison.OrdinalIgnoreCase))) return null;
        var bases = rawNames.Select(StripFlavor).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (bases.Any(b => b.Equals(folderName, StringComparison.OrdinalIgnoreCase))) return null;
        if (bases.Count != 1 || !IsSafeFolderName(bases[0])) return null;

        var name = bases[0];
        if (folderName.Length <= name.Length + 1 || !folderName.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return null;
        if (folderName[name.Length] is not ('-' or '_' or ' ' or '.')) return null;
        // "Foo_Classic" holding Foo.toc is a flavor build of Foo, not a versioned copy: leave it.
        var rest = folderName[(name.Length + 1)..];
        if (FlavorSuffixes.Contains(rest, StringComparer.OrdinalIgnoreCase)) return null;
        return name;
    }

    private static string RootFolderName(List<FileInfo> tocs)
    {
        var raw = tocs.Select(t => Path.GetFileNameWithoutExtension(t.Name)).ToList();
        var plain = raw.FirstOrDefault(n => StripFlavor(n).Equals(n, StringComparison.OrdinalIgnoreCase));
        if (plain != null) return plain;
        // Several flavor TOCs agreeing on a name (Foo_Mainline + Foo_Vanilla) name the addon; otherwise keep the TOC's own name.
        var bases = raw.Select(StripFlavor).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return raw.Count > 1 && bases.Count == 1 ? bases[0] : raw[0];
    }

    /// <summary>"Details_Mainline.toc" → "Details".</summary>
    public static string TocBaseName(string tocFileName) => StripFlavor(Path.GetFileNameWithoutExtension(tocFileName));

    private static string StripFlavor(string name)
    {
        foreach (var suffix in FlavorSuffixes)
        {
            foreach (var sep in new[] { "_", "-" })
            {
                var s = sep + suffix;
                if (name.EndsWith(s, StringComparison.OrdinalIgnoreCase) && name.Length > s.Length) return name[..^s.Length];
            }
        }
        return name;
    }

    public static bool IsSafeFolderName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && name is not "." and not ".."
        && !name.Contains("..", StringComparison.Ordinal);

    /// <summary>Removes folders, sending them to the Recycle Bin on Windows unless told otherwise.</summary>
    public static void DeleteFolders(WowInstallation installation, IEnumerable<string> folders, bool useRecycleBin = true)
    {
        foreach (var folder in folders)
        {
            if (!IsSafeFolderName(folder)) continue;
            var path = Path.Combine(installation.AddonsPath, folder);
            if (!Directory.Exists(path)) continue;
            if (useRecycleBin && OperatingSystem.IsWindows())
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            else
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not clean up {path}", ex);
        }
    }

    /// <summary>Selects the most likely "main" folder: one that doesn't depend on its siblings, shortest name.</summary>
    public static List<string> OrderFolders(IEnumerable<string> folders, string addonsDir, WowClientGroup group, string? preferredName = null)
    {
        var list = folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count <= 1) return list;
        var set = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);

        int Score(string folder)
        {
            if (preferredName != null && folder.Equals(preferredName, StringComparison.OrdinalIgnoreCase)) return -1000;
            var info = Scanning.AddonFolderScanner.ReadFolder(new DirectoryInfo(Path.Combine(addonsDir, folder)), group);
            var deps = info?.Toc?.Dependencies.Count(set.Contains) ?? 0;
            var lod = info?.Toc?.LoadOnDemand == true ? 1 : 0;
            return deps * 100 + lod * 50 + folder.Length;
        }

        return list.OrderBy(Score).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
