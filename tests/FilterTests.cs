// Checks of the name filter and the filtered treemap layout: throws on the
// first check that fails, prints "ok" otherwise.
// Ported unchanged from the GTK app's tests/FilterTests.
using Disktiles.Core;
using static Disktiles.Core.Draft;

static class FilterTests
{
    public static void Run()
    {
        var root = Tree.Of(Dir("root",
            Dir("src", Dir("App", File("main.rs", 10)), File("notes", 5)),
            Dir("apps", File("x", 100), Dir("apple", File("y", 7))),
            File("readme", 1)));
        Tree.Aggregate(root, Metric.Bytes);

        // Matches are kept whole, and their ancestors by what matched.
        var found = Filter.Run(root, [], "APP")!;
        Check(found.Count == 2, "src/App and apps; apple is inside apps");
        Check(found.Bytes == 10 + 107, "matched bytes");
        Check(found.KeepOf(CrumbsOf(root, "apps")) == new Keep(true, 0, 0), "apps whole");
        Check(found.KeepOf(CrumbsOf(root, "apps", "x")) == new Keep(true, 0, 0), "inside a match");
        Check(found.KeepOf(CrumbsOf(root, "src")) == new Keep(false, 10, 1), "src partial");
        Check(found.KeepOf(CrumbsOf(root, "src", "notes")) is null, "notes out");
        Check(found.KeepOf(CrumbsOf(root, "readme")) is null, "readme out");

        // A search below the root leaves the rest alone.
        var src = CrumbsOf(root, "src");
        var below = Filter.Run(root.Children[src[0]], src, "main")!;
        Check(below.KeepOf(CrumbsOf(root, "apps")) == new Keep(true, 0, 0), "outside the base");
        Check(below.KeepOf(CrumbsOf(root, "src", "notes")) is null, "notes out below");
        Check(below.KeepOf(src) == new Keep(false, 10, 1), "the base holds the matches");

        Check(Filter.Run(root, [], "  ") is null, "an empty needle filters nothing");
        Check(Filter.Run(root, [], "zzz") is { Count: 0, Keep.Count: 0 }, "no matches");

        // A filtered layout shows only the matches, at their size.
        var tree = Tree.Of(Dir("root", Dir("src", File("big_test.rs", 300), File("other.rs", 700)),
            File("unit_test.txt", 100), File("huge.iso", 5000)));
        Tree.Aggregate(tree, Metric.Bytes);
        var matches = Filter.Run(tree, [], "test")!;
        var options = new LayoutOptions { Padding = 0, PaddingOuter = 0 };
        var tiles = Treemap.Layout(tree, [], new Rect(0, 0, 400, 400), Metric.Bytes, options, matches);
        var names = tiles.Where(t => t.Kind == TileKind.Node).Select(t => Resolve(tree, t.Crumbs).Name).ToList();
        Check(names.Contains("big_test.rs") && names.Contains("unit_test.txt"), "matches drawn");
        Check(!names.Contains("huge.iso") && !names.Contains("other.rs"), "the rest left out");
        var ratio = AreaOf("src") / AreaOf("unit_test.txt");
        Check(ratio > 2.9 && ratio < 3.1, $"src is sized by its 300 matched bytes, not 1000: ratio {ratio:0.00}");

        // The merged tail ("+N more") carries the bytes of what it merges, whatever
        // the layout ranks by, and only what matched under a filter.
        var many = Tree.Of(Dir("root", File("a", 100), File("b", 50), File("c", 10), File("d_test", 5)));
        Tree.Aggregate(many, Metric.Bytes);
        var two = new LayoutOptions { Padding = 0, PaddingOuter = 0, MinTile = 0, MaxChildren = 2 };
        foreach (var metric in new[] { Metric.Bytes, Metric.Files })
        {
            var tail = Treemap.Layout(many, [], new Rect(0, 0, 400, 400), metric, two).Single(t => t.Kind == TileKind.Others);
            Check(tail.Count == 2 && tail.TailBytes == 15, $"tail bytes by {metric}: {tail.TailBytes}");
        }
        var tested = Treemap.Layout(many, [], new Rect(0, 0, 400, 400), Metric.Bytes,
            two with { MaxChildren = 1 }, Filter.Run(many, [], "test"));
        Check(tested.All(t => t.Kind == TileKind.Node), "one match: nothing to merge");

        // Filter folds ASCII letters only, bytewise over UTF-8: every needle char
        // against names holding it in either case, and non-ASCII never folded.
        var cased = Tree.Of(Dir("root", File("big_TEST.rs", 1), File("other.rs", 1), File("Ünï.txt", 1), File("so", 1)));
        Tree.Aggregate(cased, Metric.Bytes);
        Check(Filter.Run(cased, [], "test")!.Count == 1 && Filter.Run(cased, [], "TeSt")!.Count == 1, "ASCII case folded");
        Check(Filter.Run(cased, [], "Ünï")!.Count == 1 && Filter.Run(cased, [], "ünï")!.Count == 0, "non-ASCII exact");
        Check(Filter.Run(cased, [], "ſo")!.Count == 0 && Filter.Run(cased, [], "s")!.Count == 3, "no fold to ASCII; any position");
        Check(Filter.Run(cased, [], ".RS")!.Count == 2 && Filter.Run(cased, [], "rs.")!.Count == 0, "ends and overruns");
        // Every byte against every lowercased ASCII needle byte, first and later in the needle.
        for (var c = 0; c < 256; c++)
        {
            for (var n = 0; n < 128; n++)
            {
                if (n is >= 'A' and <= 'Z')
                {
                    continue;
                }
                var expected = (c is >= 'A' and <= 'Z' ? c | 0x20 : c) == n;
                Check(Filter.ContainsIgnoringCase([(byte)c, (byte)'x'], [(byte)n, (byte)'x']) == expected
                    && Filter.ContainsIgnoringCase([(byte)'x', (byte)c], [(byte)'x', (byte)n]) == expected, $"byte {c:X2} against '{(char)n}'");
            }
        }
        Console.WriteLine("ok    filter: matches, filtered layout, tail bytes, case folding");
            return;

        double AreaOf(string name) => tiles
            .Where(t => t.Kind == TileKind.Node && Resolve(tree, t.Crumbs).Name == name)
            .Select(t => (double)t.Rect.W * t.Rect.H).First();

        static NodeRef Resolve(NodeRef node, int[] crumbs) => crumbs.Aggregate(node, (at, index) => at.Children[index]);

        static int[] CrumbsOf(NodeRef node, params string[] names)
        {
            var crumbs = new List<int>();
            foreach (var name in names)
            {
                var index = node.Children.IndexOf(name);
                crumbs.Add(index);
                node = node.Children[index];
            }
            return [.. crumbs];
        }

        static void Check(bool ok, string what)
        {
            if (!ok)
            {
                throw new InvalidOperationException("failed: " + what);
            }
        }

    }
}
