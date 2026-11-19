using System.Text.RegularExpressions;
using Satchel.Core.Models;

namespace Satchel.Core.Toc;

public sealed class TocFile
{
    public string FilePath { get; init; } = "";

    /// <summary>File name without extension, e.g. "Details_Mainline".</summary>
    public string BaseName { get; init; } = "";

    public IReadOnlyDictionary<string, string> Fields { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string Interface => Get("Interface");

    /// <summary>Display title; empty when the TOC only has a packager placeholder like "@localization(...)@".</summary>
    public string Title
    {
        get
        {
            var t = TocParser.StripFormatting(Get("Title"));
            return t.StartsWith('@') && t.EndsWith('@') ? "" : t;
        }
    }

    public string Version => Get("Version");

    public string Author => TocParser.StripFormatting(Get("Author"));

    public string Notes => TocParser.StripFormatting(Get("Notes"));

    public string CurseProjectId => Get("X-Curse-Project-ID");

    public string WowInterfaceId => Get("X-WoWI-ID");

    public string WagoId => Get("X-Wago-ID");

    public string TukuiProjectId => Get("X-Tukui-ProjectID");

    public string Website => Get("X-Website");

    public bool LoadOnDemand => Get("LoadOnDemand") == "1";

    public IReadOnlyList<string> Dependencies
    {
        get
        {
            var raw = string.Join(',', new[] { Get("Dependencies"), Get("RequiredDeps"), Get("Dep") }.Where(s => s.Length > 0));
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }

    public string Get(string key) => Fields.TryGetValue(key, out var v) ? v : "";

    /// <summary>Picks the interface number that belongs to this client group, as a version string.</summary>
    public string GameVersionFor(WowClientGroup group)
    {
        var values = Interface.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => int.TryParse(v, out var n) ? n : 0)
            .Where(n => n > 0)
            .ToList();
        if (values.Count == 0) return "";
        var (min, max) = group switch
        {
            WowClientGroup.Retail => (100000, int.MaxValue),
            WowClientGroup.Mists => (50000, 59999),
            WowClientGroup.Cata => (40000, 49999),
            WowClientGroup.Wotlk => (30000, 39999),
            WowClientGroup.BurningCrusade => (20000, 29999),
            WowClientGroup.Forever => (16000, 19999),
            WowClientGroup.Classic => (10000, 15999),
            _ => (0, int.MaxValue),
        };
        var match = values.Where(v => v >= min && v <= max).DefaultIfEmpty(values.Max()).Max();
        return WowClients.InterfaceToGameVersion(match.ToString());
    }
}

public static partial class TocParser
{
    public static TocFile Parse(string path)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        string? line;
        var lines = 0;
        while ((line = reader.ReadLine()) != null && lines++ < 400)
        {
            if (!line.StartsWith("##", StringComparison.Ordinal)) continue;
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[2..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Length > 0 && !fields.ContainsKey(key)) fields[key] = value;
        }

        return new TocFile
        {
            FilePath = path,
            BaseName = Path.GetFileNameWithoutExtension(path),
            Fields = fields,
        };
    }

    /// <summary>Removes WoW UI escape codes (colors, textures, atlases) from a TOC string.</summary>
    public static string StripFormatting(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains('|')) return value.Trim();
        var s = TextureRegex().Replace(value, "");
        s = ColorNameRegex().Replace(s, "");
        s = ColorRegex().Replace(s, "");
        s = s.Replace("|r", "", StringComparison.OrdinalIgnoreCase);
        return WhitespaceRegex().Replace(s, " ").Trim();
    }

    /// <summary>
    /// Chooses the TOC a client would load from a folder: Folder_Suffix.toc / Folder-Suffix.toc for the
    /// client's suffixes in priority order, then Folder.toc, then any TOC.
    /// </summary>
    public static TocFile? SelectForClient(string folderName, IReadOnlyList<TocFile> tocs, WowClientGroup group)
    {
        if (tocs.Count == 0) return null;
        foreach (var suffix in WowClients.TocSuffixes(group))
        {
            var match = tocs.FirstOrDefault(t =>
                string.Equals(t.BaseName, $"{folderName}_{suffix}", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.BaseName, $"{folderName}-{suffix}", StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return tocs.FirstOrDefault(t => string.Equals(t.BaseName, folderName, StringComparison.OrdinalIgnoreCase))
               ?? tocs[0];
    }

    [GeneratedRegex(@"\|[TA][^|]*\|[ta]")]
    private static partial Regex TextureRegex();

    [GeneratedRegex(@"\|c[nN][A-Za-z0-9_]*:")]
    private static partial Regex ColorNameRegex();

    [GeneratedRegex(@"\|c[0-9a-fA-F]{8}")]
    private static partial Regex ColorRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
