using System.Text;
using System.Text.RegularExpressions;

namespace Satchel.Core.Scanning;

/// <summary>
/// CurseForge folder fingerprinting: a whitespace-normalized MurmurHash2 (seed 1) of every file the
/// folder's TOCs and Bindings.xml pull in, combined into a single folder hash.
/// </summary>
public static partial class CurseFingerprint
{
    private static bool IsWhitespace(byte b) => b is 9 or 10 or 13 or 32;

    public static uint Hash(ReadOnlySpan<byte> data)
    {
        const uint m = 0x5bd1e995;
        const int r = 24;

        uint length = 0;
        foreach (var b in data)
        {
            if (!IsWhitespace(b)) length++;
        }

        var h = 1u ^ length;
        uint k = 0;
        var shift = 0;
        foreach (var b in data)
        {
            if (IsWhitespace(b)) continue;
            k |= (uint)b << shift;
            shift += 8;
            if (shift == 32)
            {
                k *= m;
                k ^= k >> r;
                k *= m;
                h *= m;
                h ^= k;
                k = 0;
                shift = 0;
            }
        }

        if (shift > 0)
        {
            h ^= k;
            h *= m;
        }

        h ^= h >> 13;
        h *= m;
        h ^= h >> 15;
        return h;
    }

    /// <summary>Computes the fingerprint of one addon folder (a direct child of Interface\AddOns).</summary>
    public static uint FingerprintFolder(string folderPath) => FingerprintFolder(folderPath, cache: null);

    public static uint FingerprintFolder(string folderPath, FingerprintCache? cache)
    {
        folderPath = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar);
        var infos = new DirectoryInfo(folderPath).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        var allFiles = infos.Select(f => f.FullName).ToList();

        // Hashing reads every included file; skip it when nothing on disk changed since last time.
        var stamp = new FolderStamp(infos.Count, infos.Sum(f => f.Length), infos.Count == 0 ? 0 : infos.Max(f => f.LastWriteTimeUtc.Ticks));
        if (cache != null && cache.TryGet(folderPath, stamp, out var cached)) return cached;
        var result = ComputeFingerprint(folderPath, allFiles);
        cache?.Set(folderPath, stamp, result);
        return result;
    }

    private static uint ComputeFingerprint(string folderPath, List<string> allFiles)
    {
        var fileMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in allFiles) fileMap.TryAdd(f.ToLowerInvariant(), f);

        var parentDir = (Path.GetDirectoryName(folderPath) ?? "") + Path.DirectorySeparatorChar;
        var parentLower = parentDir.ToLowerInvariant();

        var matching = new List<string>();
        var tocs = new List<string>();
        foreach (var file in allFiles)
        {
            var lower = file.ToLowerInvariant();
            var relative = lower.StartsWith(parentLower, StringComparison.Ordinal) ? lower[parentLower.Length..] : lower;
            if (TocFileRegex().IsMatch(relative)) tocs.Add(file);
            else if (BindingsXmlRegex().IsMatch(relative)) matching.Add(file);
        }

        foreach (var toc in tocs) ProcessIncludeFile(matching, toc, fileMap, depth: 0);

        var hashes = new List<uint>(matching.Count);
        foreach (var file in matching)
        {
            try
            {
                hashes.Add(Hash(File.ReadAllBytes(file)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        hashes.Sort();
        var concat = new StringBuilder(hashes.Count * 10);
        foreach (var h in hashes) concat.Append(h);
        return Hash(Encoding.ASCII.GetBytes(concat.ToString()));
    }

    private static void ProcessIncludeFile(List<string> matching, string file, Dictionary<string, string> fileMap, int depth)
    {
        if (depth > 64) return;
        if (!fileMap.TryGetValue(file.ToLowerInvariant(), out var nativePath)) return;
        if (matching.Contains(nativePath)) return;
        matching.Add(nativePath);

        // Extension checks are case-sensitive on purpose, matching the reference scanner.
        var ext = Path.GetExtension(nativePath);
        if (ext != ".xml" && ext != ".toc") return;

        string text;
        try
        {
            text = File.ReadAllText(nativePath, Encoding.UTF8);
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        text = ext == ".xml" ? XmlCommentsRegex().Replace(text, "") : TocCommentsRegex().Replace(text, "");
        var includeRegex = ext == ".xml" ? XmlIncludeRegex() : TocIncludeRegex();
        var dir = Path.GetDirectoryName(nativePath) ?? "";

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            var m = includeRegex.Match(line);
            if (!m.Success || m.Groups.Count < 2) continue;
            var include = m.Groups[1].Value.Trim();
            if (include.Any(c => c < 32 || c == '|')) break;
            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(dir, include.Replace('/', '\\')));
            }
            catch (Exception) { continue; }
            ProcessIncludeFile(matching, full, fileMap, depth + 1);
        }
    }

    [GeneratedRegex(@"^([^/]+)[\\/]\1([-|_](mainline|bcc|tbc|classic|vanilla|wrath|wotlkc|cata|mists|forever|camelot))?\.toc$", RegexOptions.IgnoreCase)]
    private static partial Regex TocFileRegex();

    [GeneratedRegex(@"^[^/\\]+[/\\]Bindings\.xml$", RegexOptions.IgnoreCase)]
    private static partial Regex BindingsXmlRegex();

    [GeneratedRegex(@"\s*#.*$", RegexOptions.Multiline)]
    private static partial Regex TocCommentsRegex();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlCommentsRegex();

    [GeneratedRegex(@"^\s*((?:(?<!\.\.).)+\.(?:xml|lua))\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TocIncludeRegex();

    [GeneratedRegex(@"<(?:Include|Script)\s+file=[""']((?:(?<!\.\.).)+)[""']\s*/>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex XmlIncludeRegex();
}
