// The review, running and done screens: c opens the list of everything marked;
// m trash, p permanent, ! unmark all, Enter commits, Esc goes back; s saves
// the list, a copies a prompt for a coding agent. Moving to the trash
// commits at once; a permanent deletion always asks first, in a dialog that
// names what goes and how much comes back. When the removal is done the tree
// is scanned again and the free space actually gained is shown.
using Disktiles.Core;

namespace Disktiles;

/// One marked path on the review screen.
sealed record ReviewRow(string Name, string Path, string Tilde, bool IsDir, bool Covered, string Blocked, double Fraction, string Size);

/// A label and its value, in a totals list.
sealed record Line(string Label, string Value);

/// The space meter: free now → after, and the used share after.
sealed record MeterModel(string Text, double Used);

sealed record ReviewModel(string Subtitle, string CommitHint, string Available, List<ReviewRow> Rows, string More,
    string Mode, bool TrashAvailable, string Explanation, List<Line> Totals, MeterModel? Volume, List<string> Kept,
    NoticeModel? Notice, bool CanExport, string CommitLabel, bool Destructive, bool CanCommit);

/// The permanent-delete confirmation.
sealed record DialogModel(bool Visible, string Title, string Body);

sealed record LogRow(bool Ok, string Name, string Error);

sealed record RunningModel(string Subtitle, string Progress, double Fraction, bool Danger, List<LogRow> Log);

/// A big number on the done screen; `Style` "", success, warning or danger.
sealed record StatModel(string Label, string Value, string Style);

sealed record DoneModel(List<StatModel> Stats, MeterModel? Volume, bool Rescanning, List<LogRow> Failures);

sealed partial class App
{
    /// Marked rows the review lists; the rest are counted.
    const int ListLimit = 1200;

    // What the permanent-delete dialog lists: exactly what Confirm runs.
    Plan? _asked;
    RemovalHandle? _run;
    RemovalMode _runMode;
    int _total, _failed;
    ulong _removed, _bytes;
    readonly List<(string Path, string? Error)> _log = [];
    SpaceInfo? _baseline, _space;
    bool _rescanning;

    void InitReview()
    {
        var on = Screen.Review;
        Keys.Bind("Escape", "Review", "back", () => SetScreen(Screen.Explore), on: on);
        Keys.Bind("Return", "Review", "move to trash, or delete…", Commit, on: on);
        Keys.Bind("m", "Review", "trash", () => SetRemovalMode(RemovalMode.Trash), on: on);
        Keys.Bind("p", "Review", "permanent", () => SetRemovalMode(RemovalMode.Permanent), on: on);
        Keys.Bind("exclam", "Review", "unmark all", UnmarkAll, on: on);
        Keys.Bind("s", "Review", "save list", SaveList, on: on);
        Keys.Bind("a", "Review", "copy as prompt", CopyPrompt, on: on);
        Keys.Bind("Escape", "Removing", "stop after the current item", () => _run?.Cancel(), on: Screen.Running);
        Keys.Bind("Return Escape", "Done", "continue", () => SetScreen(Screen.Explore), on: Screen.Done);
    }

    public void SetScreen(Screen screen)
    {
        if (screen == Screen.Review)
        {
            // The mount table may have landed since the last plan.
            Replan();
            _space = SpaceInfo.Of(State.Root);
        }
        if (screen != Screen.Review)
        {
            CloseDialog();
        }
        Unhover();
        State.SetScreen(screen);
        Touch(Part.Screen | Part.Review | Part.Footer);
        Log($"screen {screen}");
    }

    public void OpenReview()
    {
        if (Marks.Count == 0)
        {
            Say("mark something first: space marks the selected tile", Tone.Warning);
            return;
        }
        SetScreen(Screen.Review);
    }

    // ── review ──────────────────────────────────────────────────────────

    public ReviewModel Review
    {
        get
        {
            var plan = Plan;
            var items = Marks.Items;
            var trash = Trash.IsAvailable();
            var covered = plan.Covered.Select(target => target.Path).ToHashSet();
            var blocked = plan.Blocked.ToDictionary(item => item.Path, item => item.Reason);
            var rootBytes = Math.Max(State.Tree?.Bytes ?? 0, 1);
            var rows = items.Take(ListLimit).Select(item => new ReviewRow(Path.GetFileName(item.Path), item.Path, State.Tilde(item.Path),
                item.IsDir, covered.Contains(Removal.Normalize(item.Path)), blocked.GetValueOrDefault(item.Path) ?? "",
                Math.Min(1, (double)item.Bytes / rootBytes), Look.HumanBytes(item.Bytes))).ToList();
            var totals = new List<Line>
            {
                new("Marked", $"{Marks.Count}"),
                new("Acted on", $"{plan.Targets.Count}"),
                new("Nested, go with a parent", $"{plan.Covered.Count}"),
                new("Kept back", $"{plan.Blocked.Count}"),
                new("Space freed", Look.HumanBytes(plan.Bytes)),
            };
            if (NotCounted(plan) is { } leftOut)
            {
                totals.Add(new("Not counted here", leftOut));
            }
            var count = plan.Targets.Count;
            var noun = count == 1 ? "item" : "items";
            return new ReviewModel(
                $"{items.Count} marked · {Look.HumanBytes(plan.Bytes)} to free",
                RemovalMode == RemovalMode.Trash ? "move to trash" : "delete…",
                $"{(_space is { } space ? Look.HumanBytes(space.Available) : "?")} available",
                rows,
                items.Count > ListLimit ? $"{items.Count - ListLimit} more are marked and will be removed too. Unmark them in the treemap." : "",
                RemovalMode.ToString(),
                trash,
                RemovalMode == RemovalMode.Trash
                    ? $"Recoverable from the trash until it is emptied. Uses {Trash.Label()}."
                    : "Deleted at once, like rm -rf. Nothing is recoverable.",
                totals,
                _space is { } volume ? Meter(volume, plan.Reclaim) : null,
                [.. plan.Blocked.Take(6).Select(item => $"{Path.GetFileName(item.Path)}: {item.Reason}")],
                NoticeLine,
                !plan.IsEmpty,
                RemovalMode == RemovalMode.Trash ? $"Move {count} {noun} to trash" : $"Delete {count} {noun}…",
                RemovalMode == RemovalMode.Permanent,
                !plan.IsEmpty && (RemovalMode == RemovalMode.Permanent || trash));
        }
    }

    /// The space meter: free now, free after `reclaiming`, and the used share.
    static MeterModel Meter(SpaceInfo volume, ulong reclaiming)
    {
        var after = volume.AfterRemoving(reclaiming);
        return new(reclaiming > 0
                ? $"{Look.HumanBytes(volume.Available)} free → {Look.HumanBytes(after.Available)} free"
                : $"{Look.HumanBytes(volume.Available)} free of {Look.HumanBytes(volume.Total)}",
            volume.Total == 0 ? 0 : (double)after.Used / volume.Total);
    }

    public DialogModel Dialog => _asked is { } plan
        ? new(true, plan.Targets is [var only]
                ? $"Delete “{Path.GetFileName(only.Path)}” permanently?"
                : $"Delete {plan.Targets.Count} items permanently?",
            $"This frees {Look.HumanBytes(plan.Bytes)}. Deleted files can’t be recovered; move them to the trash if you might need them again.")
        : new(false, "", "");

    /// Enter, or the commit button: the trash at once, permanent after asking.
    public void Commit()
    {
        Replan();
        var plan = Plan;
        if (plan.IsEmpty)
        {
            return;
        }
        if (RemovalMode == RemovalMode.Trash)
        {
            Begin(plan);
            return;
        }
        _asked = plan;
        // The confirmation is modal: Enter deletes, Escape cancels, and
        // nothing else gets through while it is open.
        Keys.Grab(DialogKeys);
        Touch(Part.Review);
    }

    bool DialogKeys(Key key)
    {
        if (key.Name == "Return")
        {
            Confirm();
        }
        else if (key.Name == "Escape")
        {
            CloseDialog();
        }
        return true;
    }

    public void CloseDialog()
    {
        if (_asked is not null)
        {
            _asked = null;
            Keys.Release(DialogKeys);
            Touch(Part.Review);
        }
    }

    public void Confirm()
    {
        var plan = _asked;
        CloseDialog();
        if (plan is not null)
        {
            Begin(plan);
        }
    }

    /// `s`: the list handed on instead of acted on. QML asks where (the
    /// desktop's save dialog) and answers with SaveListTo; --test-save-to
    /// answers at once.
    public void SaveList()
    {
        if (Plan.IsEmpty)
        {
            Say("nothing to save", Tone.Warning);
            return;
        }
        if (Hook("save-to") is { } path)
        {
            SaveListTo(path);
            return;
        }
        Emit(new Effect("saveFile", "disktiles-delete-list.txt", State.Home));
    }

    public void SaveListTo(string path)
    {
        var plan = Plan;
        if (path.Length == 0 || plan.IsEmpty)
        {
            return;
        }
        var list = Export.DeleteList(plan.Targets);
        var count = plan.Targets.Count;
        try
        {
            File.WriteAllText(path, list);
            Say($"saved {count} {(count == 1 ? "path" : "paths")} to {path}", Tone.Success);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Say($"could not save {path}: {error.Message}", Tone.Error);
        }
    }

    /// `a`: a prompt for a coding agent, to the clipboard (QML's), or with
    /// --test-clipboard-to to a file, so a headless run never takes over the
    /// clipboard of whoever is at the machine.
    public void CopyPrompt()
    {
        var plan = Plan;
        if (plan.IsEmpty)
        {
            Say("nothing to copy", Tone.Warning);
            return;
        }
        var prompt = Export.AgentPrompt(plan.Targets, MarkRoot, SpaceInfo.Of(State.Root));
        if (Hook("clipboard-to") is { } file)
        {
            try
            {
                File.WriteAllText(file, prompt);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Say($"could not write {file}: {error.Message}", Tone.Error);
                return;
            }
        }
        else
        {
            Emit(new Effect("copy", prompt));
        }
        var count = plan.Targets.Count;
        Say($"copied a prompt for your agent: {count} {(count == 1 ? "path" : "paths")}, {Look.HumanBytes(plan.Bytes)}", Tone.Success);
    }

    // ── running ─────────────────────────────────────────────────────────

    /// Run `plan`, the one the user saw: the worker may still drop a target
    /// that fails its checks again, but never adds one.
    void Begin(Plan plan)
    {
        if (plan.IsEmpty)
        {
            Say("nothing is marked", Tone.Warning);
            return;
        }
        // Free space before, for the honest "what did it actually free".
        _baseline = SpaceInfo.Of(State.Root);
        (_total, _removed, _bytes, _failed, _runMode) = (plan.Targets.Count, 0, 0, 0, RemovalMode);
        _log.Clear();
        SetScreen(Screen.Running);
        var run = _run = Removal.Spawn(plan, _runMode, _trashDir is null ? null : Trash, _trashDir);
        Ui.Every(60, () => Poll(run));
    }

    /// Drain the worker's events; false once it is done.
    bool Poll(RemovalHandle run)
    {
        var finished = false;
        while (run.Poll(out var next))
        {
            switch (next)
            {
                case RemovalEvent.Start start:
                    _total = start.Total;
                    break;
                case RemovalEvent.Item item:
                    if (item.Error is null)
                    {
                        _removed++;
                        _bytes += item.Bytes;
                    }
                    else
                    {
                        _failed++;
                    }
                    _log.Add((item.Path, item.Error));
                    Console.Error.WriteLine($"removal: {item.Path} -> {item.Error ?? "removed"}");
                    break;
                case RemovalEvent.Done done:
                    (_removed, _bytes, _failed) = (done.Removed, done.Bytes, done.Failed);
                    finished = true;
                    break;
            }
        }
        Touch(Part.Review);
        if (!finished)
        {
            return true;
        }
        _run = null;
        UnmarkAll();
        _space = SpaceInfo.Of(State.Root);
        SetScreen(Screen.Done);
        // The tree on screen still holds what was just removed: scan again.
        _rescanning = true;
        var root = ScanRoot;
        Start(root, tree =>
        {
            _rescanning = false;
            _space = SpaceInfo.Of(root);
            Touch(Part.Review);
            Log($"rescanned {tree?.Bytes} bytes; removed {_removed}, failed {_failed}, claimed {_bytes}, volume freed {(long?)_space?.Available - (long?)_baseline?.Available}");
        });
        return false;
    }

    public RunningModel RunningScreen
    {
        get
        {
            var done = (int)_removed + _failed;
            return new($"{done} of {_total} done", $"{_removed} removed · {Look.HumanBytes(_bytes)} to free",
                _total == 0 ? 0 : (double)done / _total, _runMode == RemovalMode.Permanent,
                [.. _log.AsEnumerable().Reverse().Select(entry => new LogRow(entry.Error is null, Path.GetFileName(entry.Path), entry.Error ?? ""))]);
        }
    }

    public DoneModel DoneScreen
    {
        get
        {
            var stats = new List<StatModel>
            {
                new("removed", $"{_removed} {(_removed == 1 ? "item" : "items")}", "success"),
                new("bytes claimed", Look.HumanBytes(_bytes), ""),
                new("failed", $"{_failed}", _failed > 0 ? "danger" : ""),
            };
            // Measured, not summed: free space now against before the removal.
            if (_baseline is { } before && _space is { } after)
            {
                var delta = (long)after.Available - (long)before.Available;
                stats.Add(new("volume freed", (delta >= 0 ? "+" : "-") + Look.HumanBytes((ulong)Math.Abs(delta)), delta >= 0 ? "success" : "warning"));
            }
            return new(stats, _space is { } volume ? Meter(volume, 0) : null, _rescanning,
                [.. _log.Where(entry => entry.Error is not null).Take(40).Select(entry => new LogRow(false, Path.GetFileName(entry.Path), entry.Error!))]);
        }
    }

    public void StopRemoval() => _run?.Cancel();
}
