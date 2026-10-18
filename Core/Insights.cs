// "Worth a look": the biggest things
// in a scan that could plausibly go.
using System.Text;

namespace Disktiles.Core;

public enum FindingKind : byte { Reclaimable, Worktrees, StaleExperiments }

/// One line of the list. `Count`/`OldestDays` belong to the worktree and
/// stale-experiment findings, `Reason` to a reclaimable one.
public sealed record Candidate(int[] Crumbs, ulong Bytes, FindingKind Finding, Reclaim Reason = Reclaim.None,
    int Count = 0, long OldestDays = 0);

public static class Insights
{
    const long Day = 86_400;
    public const long StaleDays = 30;
    /// Smaller than this is not worth a line.
    const ulong MinBytes = 64UL * 1024 * 1024;

    /// The `limit` largest findings beneath `root`, largest first.
    public static List<Candidate> WorthALook(NodeRef root, long now, int limit)
    {
        var found = new List<Candidate>();
        var crumbs = new List<int>();
        var tree = root.Tree;
        var children = tree.Children(in root.Data);
        for (var index = 0; index < children.Length; index++)
        {
            crumbs.Add(index);
            Visit(tree, in children[index], crumbs, now, found);
            crumbs.RemoveAt(crumbs.Count - 1);
        }
        found.RemoveAll(static candidate => candidate.Bytes < MinBytes);
        // Stable, so ties keep their order: OrderBy is, List.Sort is not.
        return [.. found.OrderByDescending(static candidate => candidate.Bytes).Take(limit)];
    }

    static void Visit(Tree tree, in Node node, List<int> crumbs, long now, List<Candidate> found)
    {
        // Nothing beneath a small directory can reach MinBytes either.
        if (!node.IsDir || node.Bytes < MinBytes)
        {
            return;
        }
        // Topmost only: everything beneath a reclaimable directory goes with it.
        if (node.Reclaim != Reclaim.None)
        {
            found.Add(new Candidate([.. crumbs], node.Bytes, FindingKind.Reclaimable, node.Reclaim));
            return;
        }
        var name = tree.Name(in node);
        var children = tree.Children(in node);
        var scratch = node.Category == Category.AgentScratch;
        if (scratch && Ascii.EqualsIgnoreCase(name, "worktrees"u8))
        {
            var trees = 0;
            var oldest = long.MaxValue;
            foreach (ref readonly var child in children)
            {
                if (child.IsDir)
                {
                    trees++;
                    if (child.Modified > 0)
                    {
                        oldest = Math.Min(oldest, child.Modified);
                    }
                }
            }
            if (trees > 0)
            {
                if (oldest == long.MaxValue)
                {
                    oldest = now;
                }
                found.Add(new Candidate([.. crumbs], node.Bytes, FindingKind.Worktrees,
                    Count: trees, OldestDays: Math.Max(now - oldest, 0) / Day));
                return;
            }
        }
        var experiments = scratch
            && (Ascii.EqualsIgnoreCase(name, "tries"u8)
                || Ascii.EqualsIgnoreCase(name, "experiments"u8));
        var staleCount = 0;
        ulong staleBytes = 0;
        for (var index = 0; index < children.Length; index++)
        {
            ref readonly var child = ref children[index];
            var isStale = experiments && child.IsDir && child.Modified > 0
                && now - child.Modified > StaleDays * Day;
            if (isStale)
            {
                staleCount++;
                staleBytes += child.Bytes;
                // A stale experiment is judged whole; its caches go with it.
                continue;
            }
            crumbs.Add(index);
            // Thousands of levels deep: on a stack of its own, every thousand.
            if (crumbs.Count % Tree.LevelsPerStack == 0)
            {
                var deep = child;
                Tree.OnFreshStack(() => Visit(tree, in deep, crumbs, now, found));
            }
            else
            {
                Visit(tree, in child, crumbs, now, found);
            }
            crumbs.RemoveAt(crumbs.Count - 1);
        }
        if (staleCount > 0)
        {
            found.Add(new Candidate([.. crumbs], staleBytes, FindingKind.StaleExperiments, Count: staleCount));
        }
    }
}
