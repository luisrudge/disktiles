// The side panel: the selection (what the keys act on, or what the pointer
// is over, against the whole scan), "Worth a look", and the disk under the
// scan, pinned at the bottom. Marking adds its own parts (Marking.cs).
using System.Diagnostics;
using Disktiles.Core;

namespace Disktiles;

/// The panel's first section. It shows whichever changed last: the folder,
/// the selection, or the hovered tile.
sealed record SelectionModel(bool Visible, string Name, string Path, string Size, string Unit, double Share, string Color,
    string OfScan, string Files, string LastWrite, string Kind, string Reclaim, GitModel Git, MarkButtonModel Mark);

/// One "Worth a look" row.
sealed record WorthRow(string Title, string Size, string Reason, double Fraction);

sealed record WorthModel(string Total, List<WorthRow> Rows);

/// The disk under the scan: free, used and total (statvfs).
sealed record DiskModel(string Device, string Free, string Unit, double Used, string UsedText, string Total);

sealed partial class App
{
    /// How many findings the panel lists.
    const int WorthLimit = 6;
    // The panel follows the pointer at most ten times a second. The first
    // change shows at once; a burst collapses into one trailing update with
    // the latest tile.
    const int SelectionEveryMs = 100;
    int[]? _selectionShown, _selectionWanted;
    bool _selectionTimer;
    long _selectionShownAt;
    List<Candidate> _candidates = [];

    void InitSelection()
    {
        State.FolderChanged += () => ShowSelection(State.Crumbs);
        State.SelectionChanged += () =>
        {
            if (State.Selected is { } at)
            {
                ShowSelection(at);
            }
        };
        State.HoverChanged += () =>
        {
            if (State.Hovered is { } at)
            {
                FollowSelection(at);
            }
        };
        State.TreeChanged += () =>
        {
            _candidates = State.Tree is { } tree ? Insights.WorthALook(tree, State.Now, WorthLimit) : [];
            Touch(Part.Panel);
        };
    }

    void FollowSelection(int[] at)
    {
        _selectionWanted = at;
        if (_selectionTimer)
        {
            return;
        }
        var wait = SelectionEveryMs - (int)Stopwatch.GetElapsedTime(_selectionShownAt).TotalMilliseconds;
        if (wait <= 0)
        {
            _selectionShownAt = Stopwatch.GetTimestamp();
            ShowSelection(at);
            return;
        }
        _selectionTimer = true;
        Ui.Every(wait, () =>
        {
            _selectionTimer = false;
            _selectionShownAt = Stopwatch.GetTimestamp();
            ShowSelection(_selectionWanted!);
            return false;
        });
    }

    void ShowSelection(int[] at)
    {
        if (State.Resolve(at) is not { } node)
        {
            return;
        }
        _selectionShown = at;
        GitFollow(at, node);
        Touch(Part.Selection);
    }

    public SelectionModel Selection
    {
        get
        {
            if (_selectionShown is not { } at || State.Tree is not { } tree || State.Resolve(at) is not { } node)
            {
                return new(false, "", "", "", "", 0, "", "", "", "", "", "", new(false, "", false), MarkButton(null));
            }
            var (number, unit) = Look.Split(Look.HumanBytes(node.Bytes));
            var share = tree.Bytes == 0 ? 0 : (double)node.Bytes / tree.Bytes;
            return new(true, node.Name, State.Tilde(State.PathOf(at)), number, unit, share, Look.Accent(node.Category).Qml,
                $"{share * 100:0}%", Look.Grouped(node.Files), Look.Ago(State.Now, node.Modified), Look.CategoryLabel(node.Category),
                node.Reclaim == Reclaim.None ? "" : $"Reclaimable: {Look.ReclaimLabel(node.Reclaim)}", GitLine, MarkButton(at));
        }
    }

    public WorthModel Worth
    {
        get
        {
            if (State.Tree is not { } tree)
            {
                return new("", []);
            }
            ulong total = 0;
            var largest = _candidates.Count > 0 ? _candidates[0].Bytes : 1;
            var rows = new List<WorthRow>();
            foreach (var candidate in _candidates)
            {
                total += candidate.Bytes;
                var (title, reason) = InsightText(tree, candidate);
                rows.Add(new(title, Look.HumanBytes(candidate.Bytes), reason, (double)candidate.Bytes / largest));
            }
            return new(_candidates.Count == 0 ? "nothing big" : Look.HumanBytes(total), rows);
        }
    }

    /// "Worth a look": open the folder holding it, with it selected.
    public void OpenWorth(int index)
    {
        if (index >= 0 && index < _candidates.Count && State.Screen == Screen.Explore)
        {
            var at = _candidates[index].Crumbs;
            State.GoTo(at[..^1]);
            State.Select(at);
        }
    }

    /// A finding's title and reason: the last two parts of the path, and why.
    static (string Title, string Reason) InsightText(NodeRef tree, Candidate candidate)
    {
        var names = new List<string>();
        var node = tree;
        foreach (var index in candidate.Crumbs)
        {
            node = node.Children[index];
            names.Add(node.Name);
        }
        var tail = string.Join('/', names.Skip(Math.Max(0, names.Count - 2)));
        return candidate.Finding switch
        {
            FindingKind.Reclaimable => (tail, Look.ReclaimLabel(candidate.Reason)),
            FindingKind.Worktrees => (tail, $"{candidate.Count} worktree{(candidate.Count == 1 ? "" : "s")} · oldest {candidate.OldestDays} d"),
            _ => ($"{tail} > {Insights.StaleDays} days", $"{candidate.Count} experiment{(candidate.Count == 1 ? "" : "s")} untouched"),
        };
    }

    /// The statvfs numbers: free is what an ordinary user can still write;
    /// used is the total less every free block.
    public DiskModel Disk
    {
        get
        {
            if (State.Tree is null)
            {
                return new("", "", "", 0, "", "");
            }
            try
            {
                var drive = new DriveInfo(State.Root);
                var total = (ulong)drive.TotalSize;
                var used = total - (ulong)drive.TotalFreeSpace;
                var (number, unit) = Look.Split(Look.HumanBytes((ulong)drive.AvailableFreeSpace));
                return new(DeviceFor(State.Root), number, $"{unit} free", total == 0 ? 0 : (double)used / total,
                    $"{Look.HumanBytes(used)} used", $"{Look.HumanBytes(total)} total");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return new("", "?", "", 0, error.Message, "");
            }
        }
    }

    /// The mount source of the longest mount point covering `path`.
    static string DeviceFor(string path)
    {
        try
        {
            Mount? best = null;
            foreach (var mount in Space.ParseMounts(File.ReadAllText("/proc/self/mounts")))
            {
                if (Space.PathStartsWith(path, mount.Point) && (best is null || mount.Point.Length > best.Point.Length))
                {
                    best = mount;
                }
            }
            return best?.Source ?? "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
