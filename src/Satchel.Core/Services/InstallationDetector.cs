using System.Text;
using System.Text.RegularExpressions;
using Satchel.Core.Models;

namespace Satchel.Core.Services;

/// <summary>Finds World of Warcraft clients via the Battle.net agent database and common install locations.</summary>
public static partial class InstallationDetector
{
    public const string BattleNetProductDb = @"C:\ProgramData\Battle.net\Agent\product.db";

    public static IReadOnlyList<WowInstallation> Detect(IEnumerable<string>? extraRoots = null)
    {
        var roots = new List<string>();
        if (extraRoots != null) roots.AddRange(extraRoots);
        roots.AddRange(RootsFromProductDb());
        roots.Add(@"C:\Program Files (x86)\World of Warcraft");
        roots.Add(@"C:\Program Files\World of Warcraft");
        foreach (var drive in SafeDrives())
        {
            roots.Add(Path.Combine(drive, "World of Warcraft"));
            roots.Add(Path.Combine(drive, "Games", "World of Warcraft"));
            roots.Add(Path.Combine(drive, "Program Files (x86)", "World of Warcraft"));
            roots.Add(Path.Combine(drive, "Battle.net", "World of Warcraft"));
        }

        var found = new List<WowInstallation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var install in FromRoot(root))
            {
                if (seen.Add(Normalize(install.Path))) found.Add(install);
            }
        }
        return found.OrderBy(i => WowClients.All.ToList().IndexOf(i.ClientType)).ToList();
    }

    /// <summary>Accepts a WoW root, a flavor folder, or an executable path.</summary>
    public static IEnumerable<WowInstallation> FromRoot(string path)
    {
        if (File.Exists(path)) path = Path.GetDirectoryName(path) ?? path;
        if (!Directory.Exists(path)) yield break;

        var asFlavor = WowClients.FromFolderName(Path.GetFileName(path.TrimEnd('\\', '/')));
        if (asFlavor != WowClientType.None)
        {
            if (LooksLikeClient(path)) yield return new WowInstallation { ClientType = asFlavor, Path = Path.GetFullPath(path) };
            yield break;
        }

        foreach (var type in WowClients.All)
        {
            var flavorPath = Path.Combine(path, WowClients.FolderName(type));
            if (LooksLikeClient(flavorPath)) yield return new WowInstallation { ClientType = type, Path = Path.GetFullPath(flavorPath) };
        }
    }

    public static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant();

    private static bool LooksLikeClient(string flavorPath)
    {
        try
        {
            if (!Directory.Exists(flavorPath)) return false;
            if (Directory.Exists(Path.Combine(flavorPath, "Interface"))) return true;
            return Directory.EnumerateFiles(flavorPath, "Wow*.exe").Any();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IEnumerable<string> SafeDrives()
    {
        try
        {
            return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// product.db is a protobuf; rather than a full schema we pull out the install paths it stores
    /// (e.g. "C:/Program Files (x86)/World of Warcraft") and validate them on disk.
    /// </summary>
    private static IEnumerable<string> RootsFromProductDb()
    {
        try
        {
            if (!File.Exists(BattleNetProductDb)) return [];
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(BattleNetProductDb));
            var results = new List<string>();
            foreach (Match m in PathRegex().Matches(text))
            {
                var p = m.Value.Replace('/', '\\');
                if (!p.Contains("warcraft", StringComparison.OrdinalIgnoreCase)) continue;
                // The string may run into the next protobuf field; try a few trimmed variants.
                var i = p.IndexOf("World of Warcraft", StringComparison.OrdinalIgnoreCase);
                if (i >= 0) results.Add(p[..(i + "World of Warcraft".Length)]);
                for (var cut = 0; cut <= 3 && cut < p.Length; cut++) results.Add(p[..^cut].TrimEnd('\\'));
            }
            return results.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    [GeneratedRegex(@"[A-Za-z]:[\\/][^\x00-\x1f""<>|?*]{2,240}")]
    private static partial Regex PathRegex();
}
