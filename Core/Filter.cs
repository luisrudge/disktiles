// Which parts of a tree match a name.
using System.Text;

namespace Disktiles.Core;

/// How much of a node is kept: all of it (`Whole`), or only the `Bytes` and
/// `Files` that matched beneath it.
public readonly record struct Keep(bool Whole, ulong Bytes, ulong Files);

public sealed class Matches(string needle, int[] @base)
{
    /// Lowercased.
    public readonly string Needle = needle;
    /// The needle as names are stored, UTF-8: compared bytewise.
    readonly byte[] _needle = Encoding.UTF8.GetBytes(needle);
    public readonly int[] Base = @base;
    /// Keyed by absolute crumbs. Only matches and their ancestors appear.
    public readonly Dictionary<int[], Keep> Keep = new(CrumbsComparer.Instance);
    public int Count;
    public ulong Bytes;
    public ulong Files;

    /// Whether a UTF-8 `name` contains the needle, ASCII case ignored.
    public bool Hit(ReadOnlySpan<byte> name) => Filter.ContainsIgnoringCase(name, _needle);

    /// How the node at `crumbs` takes part: null when it is filtered out.
    /// Anything outside the searched subtree, and anything beneath a match,
    /// is kept whole.
    public Keep? KeepOf(ReadOnlySpan<int> crumbs)
    {
        if (!crumbs.StartsWith(Base))
        {
            return new Keep(true, 0, 0);
        }
        // By span, so a layout asks about every tile without allocating.
        var lookup = Keep.GetAlternateLookup<ReadOnlySpan<int>>();
        for (var length = Base.Length; length <= crumbs.Length; length++)
        {
            var found = lookup.TryGetValue(crumbs[..length], out var keep);
            if (found && keep.Whole)
            {
                return keep;
            }
            if (found && length == crumbs.Length)
            {
                return keep;
            }
            // Only the base may be absent: it holds the matches but is not
            // recorded, being where the search started.
            if (!found && length > Base.Length)
            {
                return null;
            }
        }
        // The base itself holds the matches.
        return new Keep(false, Bytes, Files);
    }

    /// The value a kept node is laid out by.
    public static ulong Value(Keep keep, in Node node, Metric metric) =>
        keep.Whole ? node.Value(metric) : metric == Metric.Bytes ? keep.Bytes : keep.Files;
}

/// Structural equality for crumbs, with an Fx-style multiplicative hash.
sealed class CrumbsComparer : IEqualityComparer<int[]>, IAlternateEqualityComparer<ReadOnlySpan<int>, int[]>
{
    public static readonly CrumbsComparer Instance = new();
    const ulong K = 0xf1357aea2e62a9c5;

    public bool Equals(int[]? left, int[]? right) => left.AsSpan().SequenceEqual(right);

    public int GetHashCode(int[] crumbs) => GetHashCode(crumbs.AsSpan());

    public bool Equals(ReadOnlySpan<int> left, int[] right) => left.SequenceEqual(right);

    public int[] Create(ReadOnlySpan<int> crumbs) => crumbs.ToArray();

    public int GetHashCode(ReadOnlySpan<int> crumbs)
    {
        var hash = (0UL + (ulong)crumbs.Length) * K;
        foreach (var crumb in crumbs)
        {
            hash = (hash + (ulong)crumb) * K;
        }
        return (int)ulong.RotateLeft(hash, 26);
    }
}

public static class Filter
{
    /// Search `node`, found at absolute `base`, for names containing `needle`
    /// (ASCII case ignored). Null for an empty needle.
    public static Matches? Run(NodeRef node, int[] @base, string needle)
    {
        needle = AsciiLower(needle.Trim());
        if (needle.Length == 0)
        {
            return null;
        }
        var matches = new Matches(needle, @base);
        var crumbs = new List<int>(@base);
        var (bytes, files) = Visit(node.Tree, in node.Data, crumbs, matches);
        matches.Bytes = bytes;
        matches.Files = files;
        return matches;
    }

    static (ulong Bytes, ulong Files) Visit(Tree tree, in Node node, List<int> crumbs, Matches matches)
    {
        ulong totalBytes = 0, totalFiles = 0;
        var children = tree.Children(in node);
        for (var index = 0; index < children.Length; index++)
        {
            ref readonly var child = ref children[index];
            crumbs.Add(index);
            if (matches.Hit(tree.Name(in child)))
            {
                matches.Keep[[.. crumbs]] = new Keep(true, 0, 0);
                matches.Count++;
                totalBytes += child.Bytes;
                totalFiles += child.Files;
            }
            else if (child.Children.Count > 0)
            {
                (ulong, ulong) below;
                // Thousands of levels deep: on a stack of its own, every thousand.
                if (crumbs.Count % Tree.LevelsPerStack == 0)
                {
                    var deep = child;
                    below = default;
                    Tree.OnFreshStack(() => below = Visit(tree, in deep, crumbs, matches));
                }
                else
                {
                    below = Visit(tree, in child, crumbs, matches);
                }
                var (bytes, files) = below;
                if (bytes > 0 || files > 0)
                {
                    matches.Keep[[.. crumbs]] = new Keep(false, bytes, files);
                    totalBytes += bytes;
                    totalFiles += files;
                }
            }
            crumbs.RemoveAt(crumbs.Count - 1);
        }
        return (totalBytes, totalFiles);
    }

    /// Substring search ignoring ASCII case over UTF-8 bytes, without
    /// allocating. The needle's first byte is found in
    /// either case by the vectorized `IndexOfAny`, and only there is the rest
    /// compared: most names never contain it. UTF-8 never puts an ASCII byte
    /// inside a multibyte char, so folding bytes folds exactly ASCII letters.
    internal static bool ContainsIgnoringCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> lowerNeedle)
    {
        if (lowerNeedle.Length == 0)
        {
            return true;
        }
        var first = lowerNeedle[0];
        var firstUpper = first is >= (byte)'a' and <= (byte)'z' ? (byte)(first & ~0x20) : first;
        var rest = lowerNeedle[1..];
        while (haystack.Length >= lowerNeedle.Length)
        {
            var at = haystack[..^rest.Length].IndexOfAny(first, firstUpper);
            if (at < 0)
            {
                return false;
            }
            if (RestMatches(haystack.Slice(at + 1, rest.Length), rest))
            {
                return true;
            }
            haystack = haystack[(at + 1)..];
        }
        return false;
    }

    static bool RestMatches(ReadOnlySpan<byte> candidate, ReadOnlySpan<byte> lowerRest)
    {
        for (var i = 0; i < lowerRest.Length; i++)
        {
            var c = candidate[i];
            if ((c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c | 0x20) : c) != lowerRest[i])
            {
                return false;
            }
        }
        return true;
    }

    static string AsciiLower(string text) => string.Create(text.Length, text, static (span, source) =>
    {
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            span[i] = c is >= 'A' and <= 'Z' ? (char)(c | 0x20) : c;
        }
    });
}
