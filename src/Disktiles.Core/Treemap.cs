// Squarified treemap layout (Bruls, Huizing and van Wijk), in f32 viewport
// pixels with f64 area math.
using System.Buffers;
using System.Runtime.InteropServices;

namespace Disktiles.Core;

public record struct Rect(float X, float Y, float W, float H)
{
    public readonly float Right => X + W;
    public readonly float Bottom => Y + H;

    /// Shrink on every side, never past empty, with a fused multiply-add.
    public readonly Rect Inset(float padding) => new(
        X + padding,
        Y + padding,
        MathF.Max(MathF.FusedMultiplyAdd(padding, -2f, W), 0f),
        MathF.Max(MathF.FusedMultiplyAdd(padding, -2f, H), 0f));
}

public enum TileKind : byte { Node, Others }

/// One rectangle of the mosaic. For `Others`, `Count` is how many children it
/// merges and `TailBytes` their bytes (what matched, under a filter).
public sealed record Tile(TileKind Kind, int[] Crumbs, int Count, Rect Rect, uint Depth, Rect? Header, ulong TailBytes = 0);

public sealed record LayoutOptions
{
    public uint MaxDepth { get; init; } = 3;
    public float Padding { get; init; } = 1f;
    public float PaddingOuter { get; init; } = 3f;
    public float MinTile { get; init; } = 5f;
    public int MaxChildren { get; init; } = 96;
    public float Header { get; init; } = 20f;
    public float HeaderInner { get; init; } = 15f;
}

public static class Treemap
{
    /// Lay out everything beneath `root`, found at `rootCrumbs`, inside
    /// `area`. With `filter`, only what it keeps:
    /// its matches, at the size of what matched, inside ancestors sized the
    /// same way.
    public static List<Tile> Layout(NodeRef root, int[] rootCrumbs, Rect area, Metric metric, LayoutOptions options,
        Matches? filter = null)
    {
        var tiles = new List<Tile>();
        var crumbs = new List<int>(rootCrumbs);
        PlaceChildren(root.Tree, in root.Data, area, metric, options, filter, 0, crumbs, tiles);
        return tiles;
    }

    /// Largest first, ties by position, so the sort is stable. A
    /// struct comparer, so the span sort is specialized for it.
    readonly struct ByValue : IComparer<(int Index, double Value)>
    {
        public int Compare((int Index, double Value) left, (int Index, double Value) right)
        {
            var byValue = right.Value.CompareTo(left.Value);
            return byValue != 0 ? byValue : left.Index.CompareTo(right.Index);
        }
    }

    // `filter` is set while the level being placed is inside a filtered
    // region; a match clears it for everything beneath.
    static void PlaceChildren(Tree tree, in Node node, Rect area, Metric metric, LayoutOptions options, Matches? filter,
        uint depth, List<int> crumbs, List<Tile> output)
    {
        var children = tree.Children(in node);
        if (children.Length == 0 || area.W <= 0f || area.H <= 0f)
        {
            return;
        }

        // Rank by importance ourselves: a metric switch must not produce a bad
        // layout. Scratch comes from the pool: this runs once per directory tile.
        var pool = ArrayPool<(int Index, double Value)>.Shared;
        var rankedArray = pool.Rent(children.Length);
        var count = 0;
        for (var index = 0; index < children.Length; index++)
        {
            double value;
            if (filter is null)
            {
                value = children[index].Value(metric);
            }
            else
            {
                crumbs.Add(index);
                var keep = filter.KeepOf(CollectionsMarshal.AsSpan(crumbs));
                crumbs.RemoveAt(crumbs.Count - 1);
                value = keep is { } k ? Matches.Value(k, in children[index], metric) : 0;
            }
            if (value > 0.0)
            {
                rankedArray[count++] = (index, value);
            }
        }
        if (count == 0)
        {
            pool.Return(rankedArray);
            return;
        }
        var ranked = rankedArray.AsSpan(0, count);
        ranked.Sort(new ByValue());

        var kept = Math.Min(count, options.MaxChildren);
        var tailCount = count - kept;
        var slots = kept + (tailCount > 0 ? 1 : 0);
        var valuesArray = ArrayPool<double>.Shared.Rent(slots);
        var sourcesArray = ArrayPool<int>.Shared.Rent(slots);
        var values = valuesArray.AsSpan(0, slots);
        var sources = sourcesArray.AsSpan(0, slots);
        for (var i = 0; i < kept; i++)
        {
            values[i] = ranked[i].Value;
            sources[i] = ranked[i].Index;
        }
        ulong tailBytes = 0;
        if (tailCount > 0)
        {
            var tail = 0.0;
            foreach (var (index, value) in ranked[kept..])
            {
                tail += value;
                tailBytes += Bytes(children, index, filter, crumbs);
            }
            values[kept] = tail;
            sources[kept] = -1;
        }
        pool.Return(rankedArray);

        var rectsArray = ArrayPool<Rect>.Shared.Rent(slots);
        var rects = rectsArray.AsSpan(0, slots);
        Squarify(values, area, rects);
        for (var slot = 0; slot < rects.Length; slot++)
        {
            var rect = rects[slot].Inset(depth == 0 ? options.PaddingOuter : options.Padding);
            if (rect.W < options.MinTile || rect.H < options.MinTile)
            {
                continue;
            }
            var index = sources[slot];
            if (index < 0)
            {
                output.Add(new Tile(TileKind.Others, [.. crumbs], tailCount, rect, depth, null, tailBytes));
                continue;
            }

            ref readonly var child = ref children[index];
            var subdividable = child.IsDir && depth + 1 < options.MaxDepth;
            // A directory about to be subdivided claims a header band for its
            // name; without room for one it stays whole.
            var header = subdividable ? HeaderBand(rect, options, depth) : null;
            crumbs.Add(index);
            output.Add(new Tile(TileKind.Node, [.. crumbs], 0, rect, depth, header));
            if (header is { } band)
            {
                var body = new Rect(rect.X, band.Bottom, rect.W, rect.Bottom - band.Bottom);
                // Beneath a match everything is shown; above one, only matches.
                var inner = filter?.KeepOf(CollectionsMarshal.AsSpan(crumbs)) is { Whole: true } ? null : filter;
                PlaceChildren(tree, in child, body, metric, options, inner, depth + 1, crumbs, output);
            }
            crumbs.RemoveAt(crumbs.Count - 1);
        }
        ArrayPool<Rect>.Shared.Return(rectsArray);
        ArrayPool<int>.Shared.Return(sourcesArray);
        ArrayPool<double>.Shared.Return(valuesArray);

        // A child's bytes as the layout counts it: all of it, or what matched.
        static ulong Bytes(Span<Node> children, int index, Matches? filter, List<int> crumbs)
        {
            if (filter is null)
            {
                return children[index].Bytes;
            }
            crumbs.Add(index);
            var keep = filter.KeepOf(CollectionsMarshal.AsSpan(crumbs));
            crumbs.RemoveAt(crumbs.Count - 1);
            return keep is { } k ? Matches.Value(k, in children[index], Metric.Bytes) : 0;
        }
    }

    /// The band a directory keeps for its name, or null when the tile is too
    /// small to leave its children a usable area below it.
    static Rect? HeaderBand(Rect rect, LayoutOptions options, uint depth)
    {
        var height = depth == 0 ? options.Header : options.HeaderInner;
        var body = rect.H - height;
        if (rect.W < 44f || body < options.MinTile * 3f)
        {
            return null;
        }
        return new Rect(rect.X, rect.Y, rect.W, height);
    }

    /// Split `area` into one rectangle per value, proportional to it, written
    /// in the order of `values` into `rects`; a zero value gets an empty one.
    public static void Squarify(ReadOnlySpan<double> values, Rect area, Span<Rect> rects)
    {
        rects.Clear();
        var total = 0.0;
        foreach (var value in values)
        {
            if (value > 0.0)
            {
                total += value;
            }
        }
        if (total <= 0.0 || area.W <= 0f || area.H <= 0f)
        {
            return;
        }

        var orderArray = ArrayPool<(int Index, double Value)>.Shared.Rent(values.Length);
        var positive = 0;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] > 0.0)
            {
                orderArray[positive++] = (i, values[i]);
            }
        }
        var order = orderArray.AsSpan(0, positive);
        // Stable descending by value: ties keep their position.
        order.Sort(new ByValue());

        var scale = (double)area.W * area.H / total;
        var areasArray = ArrayPool<double>.Shared.Rent(positive);
        var areas = areasArray.AsSpan(0, positive);
        for (var i = 0; i < areas.Length; i++)
        {
            areas[i] = order[i].Value * scale;
        }

        var free = area;
        var start = 0;
        while (start < areas.Length)
        {
            var side = (double)MathF.Min(free.W, free.H);
            var end = start + 1;
            var rowSum = areas[start];
            var rowWorst = WorstRatio(areas[start..end], rowSum, side);

            while (end < areas.Length)
            {
                var candidateSum = rowSum + areas[end];
                var candidateWorst = WorstRatio(areas[start..(end + 1)], candidateSum, side);
                if (candidateWorst > rowWorst)
                {
                    break;
                }
                rowSum = candidateSum;
                rowWorst = candidateWorst;
                end++;
            }

            if (free.W >= free.H)
            {
                // A vertical strip on the left; tiles stack top to bottom.
                var stripW = MathF.Min((float)(rowSum / free.H), free.W);
                var y = free.Y;
                for (var index = start; index < end; index++)
                {
                    var height = stripW > 0f ? (float)(areas[index] / stripW) : 0f;
                    height = MathF.Max(MathF.Min(height, free.Bottom - y), 0f);
                    rects[order[index].Index] = new Rect(free.X, y, stripW, height);
                    y += height;
                }
                free.X += stripW;
                free.W -= stripW;
            }
            else
            {
                // A horizontal strip along the top; tiles run left to right.
                var stripH = MathF.Min((float)(rowSum / free.W), free.H);
                var x = free.X;
                for (var index = start; index < end; index++)
                {
                    var width = stripH > 0f ? (float)(areas[index] / stripH) : 0f;
                    width = MathF.Max(MathF.Min(width, free.Right - x), 0f);
                    rects[order[index].Index] = new Rect(x, free.Y, width, stripH);
                    x += width;
                }
                free.Y += stripH;
                free.H -= stripH;
            }
            start = end;
        }
        ArrayPool<double>.Shared.Return(areasArray);
        ArrayPool<(int Index, double Value)>.Shared.Return(orderArray);
    }

    /// Worst (largest) aspect ratio in a row of `areas` laid along `side`.
    static double WorstRatio(ReadOnlySpan<double> areas, double rowSum, double side)
    {
        if (rowSum <= 0.0 || side <= 0.0)
        {
            return double.PositiveInfinity;
        }
        var thickness = rowSum / side;
        var worst = 0.0;
        foreach (var area in areas)
        {
            if (area <= 0.0 || thickness <= 0.0)
            {
                continue;
            }
            var other = area / thickness;
            worst = Math.Max(worst, Math.Max(thickness / other, other / thickness));
        }
        return worst;
    }
}
