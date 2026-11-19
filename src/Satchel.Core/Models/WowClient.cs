namespace Satchel.Core.Models;

/// <summary>Game client flavors. Numeric values match WowUp so its data imports cleanly.</summary>
public enum WowClientType
{
    Retail = 0,
    Classic = 1,
    RetailPtr = 2,
    ClassicPtr = 3,
    Beta = 4,
    ClassicBeta = 5,
    ClassicEra = 6,
    ClassicEraPtr = 7,
    RetailXPtr = 8,
    Anniversary = 9,
    Forever = 10,
    None = 11,
}

/// <summary>Clients that share the same addon builds.</summary>
public enum WowClientGroup
{
    Retail,
    Classic,
    BurningCrusade,
    Wotlk,
    Cata,
    Mists,
    Forever,
}

public static class WowClients
{
    public static readonly IReadOnlyList<WowClientType> All =
    [
        WowClientType.Retail, WowClientType.RetailPtr, WowClientType.RetailXPtr, WowClientType.Beta,
        WowClientType.Classic, WowClientType.ClassicPtr, WowClientType.ClassicBeta,
        WowClientType.ClassicEra, WowClientType.ClassicEraPtr, WowClientType.Anniversary, WowClientType.Forever,
    ];

    public static string FolderName(WowClientType type) => type switch
    {
        WowClientType.Retail => "_retail_",
        WowClientType.ClassicEra => "_classic_era_",
        WowClientType.Classic => "_classic_",
        WowClientType.RetailPtr => "_ptr_",
        WowClientType.RetailXPtr => "_xptr_",
        WowClientType.ClassicPtr => "_classic_ptr_",
        WowClientType.Beta => "_beta_",
        WowClientType.ClassicBeta => "_classic_beta_",
        WowClientType.ClassicEraPtr => "_classic_era_ptr_",
        WowClientType.Anniversary => "_anniversary_",
        WowClientType.Forever => "_forever_",
        _ => "",
    };

    public static WowClientType FromFolderName(string? folderName)
    {
        if (string.IsNullOrEmpty(folderName)) return WowClientType.None;
        foreach (var t in All)
        {
            if (string.Equals(FolderName(t), folderName, StringComparison.OrdinalIgnoreCase)) return t;
        }
        return WowClientType.None;
    }

    public static string DisplayName(WowClientType type) => type switch
    {
        WowClientType.Retail => "Retail",
        WowClientType.RetailPtr => "Retail PTR",
        WowClientType.RetailXPtr => "Retail XPTR",
        WowClientType.Beta => "Beta",
        WowClientType.Classic => "Classic",
        WowClientType.ClassicPtr => "Classic PTR",
        WowClientType.ClassicBeta => "Classic Beta",
        WowClientType.ClassicEra => "Classic Era",
        WowClientType.ClassicEraPtr => "Classic Era PTR",
        WowClientType.Anniversary => "Anniversary",
        WowClientType.Forever => "Forever",
        _ => "Unknown",
    };

    public static WowClientGroup Group(WowClientType type) => type switch
    {
        WowClientType.Retail or WowClientType.RetailPtr or WowClientType.Beta or WowClientType.RetailXPtr => WowClientGroup.Retail,
        WowClientType.ClassicEra or WowClientType.ClassicEraPtr => WowClientGroup.Classic,
        WowClientType.Anniversary => WowClientGroup.BurningCrusade,
        WowClientType.Classic or WowClientType.ClassicPtr => WowClientGroup.Mists,
        WowClientType.Forever or WowClientType.ClassicBeta => WowClientGroup.Forever,
        _ => WowClientGroup.Retail,
    };

    /// <summary>CurseForge "gameVersionTypeId" for a client group.</summary>
    public static int CurseGameVersionTypeId(WowClientGroup group) => group switch
    {
        WowClientGroup.Retail => 517,
        WowClientGroup.Classic => 67408,
        WowClientGroup.BurningCrusade => 73246,
        WowClientGroup.Wotlk => 73713,
        WowClientGroup.Cata => 77522,
        WowClientGroup.Mists => 79434,
        WowClientGroup.Forever => 88568,
        _ => 517,
    };

    /// <summary>TOC file suffixes (e.g. Addon_Mainline.toc) preferred by a client group, most specific first.</summary>
    public static IReadOnlyList<string> TocSuffixes(WowClientGroup group) => group switch
    {
        WowClientGroup.Retail => ["standard", "mainline"],
        WowClientGroup.Forever => ["forever", "camelot", "mainline"],
        WowClientGroup.Classic => ["vanilla", "classic"],
        WowClientGroup.BurningCrusade => ["tbc", "bcc", "classic"],
        WowClientGroup.Wotlk => ["wrath", "wotlkc", "classic"],
        WowClientGroup.Cata => ["cata", "classic"],
        WowClientGroup.Mists => ["mists", "classic"],
        _ => [],
    };

    /// <summary>Flavor names used by the BigWigs packager in release.json and in GitHub asset names.</summary>
    public static IReadOnlyList<string> PackagerFlavors(WowClientGroup group) => group switch
    {
        WowClientGroup.Retail => ["mainline", "retail"],
        WowClientGroup.Classic => ["classic", "vanilla"],
        WowClientGroup.BurningCrusade => ["bcc", "tbc", "bc"],
        WowClientGroup.Wotlk => ["wrath", "wotlkc"],
        WowClientGroup.Cata => ["cata"],
        WowClientGroup.Mists => ["mists"],
        WowClientGroup.Forever => ["forever", "camelot"],
        _ => [],
    };

    /// <summary>Every flavor suffix any packager uses, so retail can pick the "unsuffixed" asset.</summary>
    public static readonly IReadOnlyList<string> AllPackagerFlavorSuffixes =
        ["classic", "vanilla", "bcc", "tbc", "bc", "wrath", "wotlkc", "cata", "mists", "forever", "camelot", "mainline", "retail"];

    /// <summary>Converts a TOC interface number (e.g. 120005) to a version string (12.0.5).</summary>
    public static string InterfaceToGameVersion(string? interfaceValue)
    {
        if (string.IsNullOrWhiteSpace(interfaceValue)) return "";
        var first = interfaceValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => int.TryParse(v, out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();
        if (first <= 0) return "";
        var major = first / 10000;
        var minor = first / 100 % 100;
        var patch = first % 100;
        return $"{major}.{minor}.{patch}";
    }
}
