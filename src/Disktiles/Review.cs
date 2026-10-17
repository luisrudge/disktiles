// The review, running and done screens: c opens the list of everything marked;
// m trash, p permanent, ! unmark all, Enter commits, Esc goes back; s saves
// the list, a copies a prompt for a coding agent. Moving to the trash
// commits at once; a permanent deletion always asks first, in a dialog that
// names what goes and how much comes back. When the removal is done the tree
// is scanned again and the free space actually gained is shown.
using Disktiles.Core;
using static Disktiles.Look;
using static Disktiles.MarkedPanel;
using static Disktiles.Sidebar;

namespace Disktiles;

static class Review
{
    /// Marked rows the review lists; the rest are counted.
    const int ListLimit = 1200;

    public static void Install(App app, Marker marker) => _ = new Screens(app, marker);

    sealed class Screens
    {
        readonly App _app;
        readonly Marker _marker;
        readonly AppState _state;

        // Review.
        readonly Gtk.Label _subtitle = Text("", "dim");
        readonly Gtk.ListBox _list = Gtk.ListBox.New();
        readonly Gtk.Box _summary = Gtk.Box.New(Gtk.Orientation.Vertical, 18);
        readonly Gtk.Label _commitHint = Gtk.Label.New(""), _available = Text("", "dim");
        readonly Gtk.Box _dialog = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        readonly Gtk.Label _dialogTitle = Text("", "dialog-title"), _dialogBody = Text("", "dim");
        readonly Func<Key, bool> _dialogKeys;
        // What the permanent-delete dialog lists: exactly what Confirm runs.
        Plan? _asked;

        // Running and done.
        RemovalHandle? _run;
        RemovalMode _runMode;
        int _total, _failed;
        ulong _removed, _bytes;
        readonly List<(string Path, string? Error)> _log = [];
        SpaceInfo? _baseline, _space;
        bool _rescanning;
        readonly Gtk.Label _runSubtitle = Text("", "dim"), _runProgress = Text("", "value");
        readonly Gtk.ProgressBar _runBar = Gtk.ProgressBar.New();
        readonly Gtk.ListBox _runLog = Gtk.ListBox.New();
        readonly Gtk.Box _doneBody = Gtk.Box.New(Gtk.Orientation.Vertical, 12);

        public Screens(App app, Marker marker)
        {
            (_app, _marker, _state) = (app, marker, app.State);
            app.AddCss(Css());
            app.AddScreen(Screen.Review, ReviewPage());
            app.AddScreen(Screen.Running, RunningPage());
            app.AddScreen(Screen.Done, DonePage());

            var on = Screen.Review;
            app.Keys.Bind("Escape", "Review", "back", () => _state.SetScreen(Screen.Explore), on: on);
            app.Keys.Bind("Return", "Review", "move to trash, or delete…", Commit, on: on);
            app.Keys.Bind("m", "Review", "trash", () => marker.SetMode(RemovalMode.Trash), on: on);
            app.Keys.Bind("p", "Review", "permanent", () => marker.SetMode(RemovalMode.Permanent), on: on);
            app.Keys.Bind("exclam", "Review", "unmark all", marker.Clear, on: on);
            app.Keys.Bind("s", "Review", "save list", SaveList, on: on);
            app.Keys.Bind("a", "Review", "copy as prompt", CopyPrompt, on: on);
            app.Keys.Bind("Escape", "Removing", "stop after the current item", () => _run?.Cancel(), on: Screen.Running);
            app.Keys.Bind("Return Escape", "Done", "continue", () => _state.SetScreen(Screen.Explore), on: Screen.Done);

            // The confirmation is modal: Enter deletes, Escape cancels, and
            // nothing else gets through while it is open.
            _dialogKeys = key =>
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
            };

            _state.ScreenChanged += () =>
            {
                if (_state.Screen == Screen.Review)
                {
                    // The mount table may have landed since the last plan,
                    // and the targets' contents are walked for placeholders.
                    marker.Replan(walk: true);
                    Refresh();
                }
            };
            marker.Changed += RefreshIfShown;
            marker.ModeChanged += RefreshIfShown;
            // A scan landing or the mode changing under the dialog changes
            // what would run: close it, so the user confirms what is shown.
            marker.Changed += CloseDialogIfOpen;
            marker.ModeChanged += CloseDialogIfOpen;
            marker.NoticeChanged += RefreshIfShown;
        }

        void RefreshIfShown()
        {
            if (_state.Screen == Screen.Review)
            {
                Refresh();
            }
        }

        // ── review ──────────────────────────────────────────────────────

        Gtk.Overlay ReviewPage()
        {
            _list.SelectionMode = Gtk.SelectionMode.None;
            _list.AddCssClass("marked-list");
            var scroll = Gtk.ScrolledWindow.New();
            scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
            scroll.Hexpand = scroll.Vexpand = true;
            scroll.Child = _list;
            scroll.AddCssClass("inset");
            _summary.AddCssClass("summary");
            _summary.SetSizeRequest(380, -1);
            var body = Gtk.Box.New(Gtk.Orientation.Horizontal, 18);
            body.MarginStart = body.MarginEnd = body.MarginTop = body.MarginBottom = 18;
            body.Vexpand = true;
            body.Append(scroll);
            body.Append(_summary);

            var footer = Hints(("enter", _commitHint), ("m", Gtk.Label.New("trash")), ("p", Gtk.Label.New("permanent")),
                ("!", Gtk.Label.New("unmark all")), ("s", Gtk.Label.New("save list")), ("a", Gtk.Label.New("copy as prompt")),
                ("esc", Gtk.Label.New("back")));
            _available.Hexpand = true;
            _available.Xalign = 1;
            footer.Append(_available);

            var page = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
            page.Append(ScreenHeader("Review", _subtitle));
            page.Append(body);
            page.Append(footer);

            // The dialog over the page, on a backdrop that takes the clicks.
            var card = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
            card.AddCssClass("dialog");
            // Centred in the window; a vertical box needs Vexpand for that.
            card.Halign = card.Valign = Gtk.Align.Center;
            card.Vexpand = true;
            card.SetSizeRequest(440, -1);
            _dialogBody.Wrap = true;
            _dialogBody.MaxWidthChars = 52;
            _dialogBody.Xalign = 0;
            var cancel = Gtk.Button.NewWithLabel("Cancel");
            cancel.OnClicked += (_, _) => CloseDialog();
            var delete = Gtk.Button.NewWithLabel("Delete");
            delete.AddCssClass("destructive-action");
            delete.OnClicked += (_, _) => Confirm();
            var actions = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            actions.Halign = Gtk.Align.End;
            actions.Append(cancel);
            actions.Append(delete);
            card.Append(_dialogTitle);
            card.Append(_dialogBody);
            card.Append(actions);
            _dialog.AddCssClass("backdrop");
            _dialog.Append(card);
            _dialog.Vexpand = true;
            _dialog.Visible = false;
            var overlay = Gtk.Overlay.New();
            overlay.Child = page;
            overlay.AddOverlay(_dialog);
            return overlay;
        }

        void Refresh()
        {
            var plan = _marker.Plan;
            var items = _marker.Marks.Items;
            _subtitle.SetText($"{items.Count} marked · {HumanBytes(plan.Bytes)} to free");
            _commitHint.SetText(_marker.Mode == RemovalMode.Trash ? "move to trash" : "delete…");
            _space = SpaceInfo.Of(_state.Root);
            _available.SetText($"{(_space is { } space ? HumanBytes(space.Available) : "?")} available");

            _list.RemoveAll();
            if (items.Count == 0)
            {
                var none = Text("Nothing is marked. Go back and mark what should go.", "dim");
                none.MarginStart = none.MarginTop = none.MarginBottom = 24;
                _list.Append(none);
            }
            var covered = plan.Covered.Select(target => target.Path).ToHashSet();
            var blocked = plan.Blocked.ToDictionary(item => item.Path, item => item.Reason);
            var rootBytes = Math.Max(_state.Tree?.Bytes ?? 0, 1);
            foreach (var item in items.Take(ListLimit))
            {
                _list.Append(MarkRow(item, covered.Contains(Removal.Normalize(item.Path)), blocked.GetValueOrDefault(item.Path), rootBytes));
            }
            if (items.Count > ListLimit)
            {
                _list.Append(Text($"{items.Count - ListLimit} more are marked and will be removed too. Unmark them in the treemap.", "dim"));
            }
            Summary(plan);
        }

        /// One marked path; fixed lanes, so sizes line up down the list.
        Gtk.Box MarkRow(Target item, bool covered, string? blocked, ulong rootBytes)
        {
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
            row.AddCssClass("mark-row");
            var icon = Text(item.IsDir ? "▸" : "·", "dim");
            icon.SetSizeRequest(14, -1);
            var name = Text(Path.GetFileName(item.Path), blocked is not null || covered ? "row-dim" : "row-title", Pango.EllipsizeMode.End);
            var names = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
            names.Hexpand = true;
            names.Append(name);
            names.Append(Text(_state.Tilde(item.Path), "dim", Pango.EllipsizeMode.Middle));
            row.Append(icon);
            row.Append(names);
            if (covered)
            {
                row.Append(Chip("Inside a marked directory", "secondary"));
            }
            if (blocked is not null)
            {
                var chip = Chip(blocked, "warning");
                chip.MaxWidthChars = 40;
                chip.TooltipText = blocked;
                row.Append(chip);
            }
            var share = Gtk.ProgressBar.New();
            share.Fraction = Math.Min(1, (double)item.Bytes / rootBytes);
            share.Valign = Gtk.Align.Center;
            share.SetSizeRequest(80, -1);
            var size = Text(HumanBytes(item.Bytes), "value");
            size.Xalign = 1;
            size.SetSizeRequest(90, -1);
            var unmark = Gtk.Button.NewWithLabel("Unmark");
            unmark.FocusOnClick = false;
            unmark.Valign = Gtk.Align.Center;
            var path = item.Path;
            unmark.OnClicked += (_, _) => _marker.Unmark(path);
            row.Append(share);
            row.Append(size);
            row.Append(unmark);
            return row;
        }

        void Summary(Plan plan)
        {
            Clear(_summary);
            var trash = _marker.Backend.IsAvailable();

            // One either-or choice, the reversible option first.
            var toTrash = Gtk.ToggleButton.NewWithLabel("Move to trash");
            var permanent = Gtk.ToggleButton.NewWithLabel("Delete permanently");
            permanent.SetGroup(toTrash);
            toTrash.Sensitive = trash;
            (_marker.Mode == RemovalMode.Trash ? toTrash : permanent).Active = true;
            toTrash.OnToggled += (button, _) =>
            {
                if (button.Active)
                {
                    _marker.SetMode(RemovalMode.Trash);
                }
            };
            permanent.OnToggled += (button, _) =>
            {
                if (button.Active)
                {
                    _marker.SetMode(RemovalMode.Permanent);
                }
            };
            var mode = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
            mode.AddCssClass("linked");
            toTrash.Hexpand = permanent.Hexpand = true;
            mode.Append(toTrash);
            mode.Append(permanent);
            var explanation = Text(_marker.Mode == RemovalMode.Trash
                ? $"Recoverable from the trash until it is emptied. Uses {_marker.Backend.Label()}."
                : "Deleted at once, like rm -rf. Nothing is recoverable.", "dim");
            explanation.Wrap = true;
            explanation.MaxWidthChars = 1;
            _summary.Append(Group("What happens", mode, explanation));

            var totals = Gtk.Grid.New();
            totals.RowSpacing = 4;
            var row = 0;
            void Line(string label, string value)
            {
                var name = Text(label, "dim");
                name.Hexpand = true;
                var number = Text(value, "value");
                number.Xalign = 1;
                totals.Attach(name, 0, row, 1, 1);
                totals.Attach(number, 1, row++, 1, 1);
            }
            Line("Marked", $"{_marker.Marks.Count}");
            Line("Acted on", $"{plan.Targets.Count}");
            Line("Nested, go with a parent", $"{plan.Covered.Count}");
            Line("Kept back", $"{plan.Blocked.Count}");
            Line("Space freed", HumanBytes(plan.Bytes));
            if (NotCounted(plan) is { } leftOut)
            {
                Line("Not counted here", leftOut);
            }
            _summary.Append(Group("Totals", totals));

            if (_space is { } volume)
            {
                _summary.Append(Group("Volume", SpaceMeter(volume, plan.Reclaim)));
            }
            if (plan.Blocked.Count > 0)
            {
                var kept = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
                foreach (var item in plan.Blocked.Take(6))
                {
                    var reason = Text($"{Path.GetFileName(item.Path)}: {item.Reason}", "warning");
                    reason.Wrap = true;
                    reason.MaxWidthChars = 1;
                    reason.Hexpand = true;
                    kept.Append(reason);
                }
                _summary.Append(Group("Kept back", kept));
            }

            var spacer = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
            spacer.Vexpand = true;
            _summary.Append(spacer);
            if (_marker.Notice is var (text, status))
            {
                var notice = Text(text, "notice");
                notice.AddCssClass($"notice-{status.ToString().ToLowerInvariant()}");
                notice.Wrap = true;
                notice.MaxWidthChars = 1;
                notice.Hexpand = true;
                _summary.Append(notice);
            }

            // The list handed on instead of acted on.
            var save = Gtk.Button.NewWithLabel("Save list…");
            save.Sensitive = !plan.IsEmpty;
            save.OnClicked += (_, _) => SaveList();
            var copy = Gtk.Button.NewWithLabel("Copy as prompt");
            copy.Sensitive = !plan.IsEmpty;
            copy.OnClicked += (_, _) => CopyPrompt();
            _summary.Append(Buttons(save, copy));

            // The one commitment: the trash is the primary action; a permanent
            // deletion is the danger action and asks first, as its ellipsis says.
            var count = plan.Targets.Count;
            var noun = count == 1 ? "item" : "items";
            var back = Gtk.Button.NewWithLabel("Back");
            back.OnClicked += (_, _) => _state.SetScreen(Screen.Explore);
            var commit = Gtk.Button.NewWithLabel(_marker.Mode == RemovalMode.Trash ? $"Move {count} {noun} to trash" : $"Delete {count} {noun}…");
            commit.AddCssClass(_marker.Mode == RemovalMode.Trash ? "suggested-action" : "destructive-action");
            commit.Sensitive = !plan.IsEmpty && (_marker.Mode == RemovalMode.Permanent || trash);
            commit.OnClicked += (_, _) => Commit();
            _summary.Append(Buttons(back, commit));
        }

        /// Enter, or the commit button: the trash at once, permanent after asking.
        void Commit()
        {
            _marker.Replan();
            var plan = _marker.Plan;
            if (plan.IsEmpty)
            {
                return;
            }
            if (_marker.Mode == RemovalMode.Trash)
            {
                Begin(plan);
                return;
            }
            _asked = plan;
            _dialogTitle.SetText(plan.Targets is [var only]
                ? $"Delete “{Path.GetFileName(only.Path)}” permanently?"
                : $"Delete {plan.Targets.Count} items permanently?");
            _dialogBody.SetText($"This frees {HumanBytes(plan.Bytes)}. Deleted files can’t be recovered; move them to the trash if you might need them again.");
            _dialog.Visible = true;
            _app.Keys.Grab(_dialogKeys);
        }

        void CloseDialog()
        {
            _asked = null;
            _dialog.Visible = false;
            _app.Keys.Release(_dialogKeys);
        }

        void CloseDialogIfOpen()
        {
            if (_dialog.Visible)
            {
                CloseDialog();
            }
        }

        void Confirm()
        {
            var plan = _asked;
            CloseDialog();
            if (plan is not null)
            {
                Begin(plan);
            }
        }

        async void SaveList()
        {
            var plan = _marker.Plan;
            if (plan.IsEmpty)
            {
                _marker.Say("nothing to save", Status.Warning);
                return;
            }
            var list = Export.DeleteList(plan.Targets);
            var count = plan.Targets.Count;
            // DISKTILES_SAVE_TO: the test hook's file, in place of the dialog.
            var path = TestHooks.Env("DISKTILES_SAVE_TO");
            if (path is null)
            {
                // GTK's file dialog goes through the desktop portal, the same
                // chooser as the rest of Omarchy.
                var dialog = Gtk.FileDialog.New();
                dialog.SetInitialName("disktiles-delete-list.txt");
                dialog.SetInitialFolder(Gio.FileHelper.NewForPath(_state.Home));
                try
                {
                    path = (await dialog.SaveAsync(_app.Window))?.GetPath();
                }
                catch (GLib.GException)
                {
                    return;  // Dismissed.
                }
            }
            if (path is null)
            {
                return;
            }
            try
            {
                await File.WriteAllTextAsync(path, list);
                _marker.Say($"saved {count} {(count == 1 ? "path" : "paths")} to {path}", Status.Success);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _marker.Say($"could not save {path}: {error.Message}", Status.Error);
            }
        }

        void CopyPrompt()
        {
            var plan = _marker.Plan;
            if (plan.IsEmpty)
            {
                _marker.Say("nothing to copy", Status.Warning);
                return;
            }
            var prompt = Export.AgentPrompt(plan.Targets, _marker.Root, SpaceInfo.Of(_state.Root));
            // DISKTILES_CLIPBOARD_TO: the test hook's file, so a headless run
            // never takes over the clipboard of whoever is at the machine.
            if (TestHooks.Env("DISKTILES_CLIPBOARD_TO") is { } file)
            {
                try
                {
                    File.WriteAllText(file, prompt);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    _marker.Say($"could not write {file}: {error.Message}", Status.Error);
                    return;
                }
            }
            else
            {
                _app.Window.GetClipboard().SetText(prompt);
            }
            var count = plan.Targets.Count;
            _marker.Say($"copied a prompt for your agent: {count} {(count == 1 ? "path" : "paths")}, {HumanBytes(plan.Bytes)}", Status.Success);
        }

        // ── running ─────────────────────────────────────────────────────

        Gtk.Box RunningPage()
        {
            _runBar.Valign = Gtk.Align.Center;
            _runLog.SelectionMode = Gtk.SelectionMode.None;
            var scroll = Gtk.ScrolledWindow.New();
            scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
            scroll.Vexpand = true;
            scroll.Child = _runLog;
            scroll.AddCssClass("inset");
            var body = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
            body.MarginStart = body.MarginEnd = body.MarginTop = body.MarginBottom = 18;
            body.Vexpand = true;
            body.Append(Caps("Progress"));
            body.Append(_runProgress);
            body.Append(_runBar);
            body.Append(scroll);
            var page = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
            page.Append(ScreenHeader("Removing", _runSubtitle));
            page.Append(body);
            page.Append(Hints(("esc", Gtk.Label.New("stop after the current item"))));
            return page;
        }

        /// Run `plan`, the one the user saw: the worker may still drop a target
        /// that fails its checks again, but never adds one.
        void Begin(Plan plan)
        {
            if (plan.IsEmpty)
            {
                _marker.Say("nothing is marked", Status.Warning);
                return;
            }
            // Free space before, for the honest "what did it actually free".
            _baseline = SpaceInfo.Of(_state.Root);
            (_total, _removed, _bytes, _failed, _runMode) = (plan.Targets.Count, 0, 0, 0, _marker.Mode);
            _log.Clear();
            _runLog.RemoveAll();
            _runBar.RemoveCssClass("danger");
            if (_runMode == RemovalMode.Permanent)
            {
                _runBar.AddCssClass("danger");
            }
            UpdateRunning();
            _state.SetScreen(Screen.Running);
            var run = _run = Removal.Spawn(plan, _runMode);
            GLib.Functions.TimeoutAdd(GLib.Constants.PRIORITY_DEFAULT, 60, () => Poll(run));
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
                        _runLog.Prepend(LogRow(item.Path, item.Error));
                        if (TestHooks.Trace || TestHooks.Env("DISKTILES_KEYS") is not null)
                        {
                            Console.Error.WriteLine($"removal: {item.Path} -> {item.Error ?? "removed"}");
                        }
                        break;
                    case RemovalEvent.Done done:
                        (_removed, _bytes, _failed) = (done.Removed, done.Bytes, done.Failed);
                        finished = true;
                        break;
                }
            }
            UpdateRunning();
            if (!finished)
            {
                return true;
            }
            _run = null;
            _marker.Clear();
            _space = SpaceInfo.Of(_state.Root);
            _state.SetScreen(Screen.Done);
            // The tree on screen still holds what was just removed: scan again.
            Rescan();
            return false;
        }

        void UpdateRunning()
        {
            var done = (int)_removed + _failed;
            _runSubtitle.SetText($"{done} of {_total} done");
            _runProgress.SetText($"{_removed} removed · {HumanBytes(_bytes)} to free");
            _runBar.Fraction = _total == 0 ? 0 : (double)done / _total;
        }

        static Gtk.Box LogRow(string path, string? error)
        {
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            row.AddCssClass("log-row");
            row.Append(Text(error is null ? "✓" : "✗", error is null ? "success" : "danger"));
            row.Append(Text(Path.GetFileName(path), error is null ? "small" : "danger", Pango.EllipsizeMode.End));
            if (error is not null)
            {
                var reason = Text(error, "warning");
                reason.Wrap = true;
                reason.MaxWidthChars = 1;
                reason.Hexpand = true;
                row.Append(reason);
            }
            return row;
        }

        // ── done ────────────────────────────────────────────────────────

        Gtk.Box DonePage()
        {
            _doneBody.MarginStart = _doneBody.MarginEnd = _doneBody.MarginTop = _doneBody.MarginBottom = 18;
            _doneBody.Vexpand = true;
            var done = Gtk.Button.NewWithLabel("Done");
            done.AddCssClass("suggested-action");
            done.OnClicked += (_, _) => _state.SetScreen(Screen.Explore);
            var footer = Hints(("enter", Gtk.Label.New("continue")));
            footer.Prepend(done);
            var page = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
            page.Append(ScreenHeader("Done", Text("removal finished", "dim")));
            page.Append(_doneBody);
            page.Append(footer);
            return page;
        }

        // Through Scanning, as `r` does: one scan at a time, its progress on
        // the treemap and in the status line.
        void Rescan()
        {
            _rescanning = true;
            UpdateDone();
            var root = Scanning.Root;
            Scanning.Start(root, tree =>
            {
                _rescanning = false;
                _space = SpaceInfo.Of(root);
                UpdateDone();
                if (TestHooks.Trace)
                {
                    Console.Error.WriteLine($"trace: rescanned {tree?.Bytes} bytes; removed {_removed}, failed {_failed}, claimed {_bytes}, volume freed {(long?)_space?.Available - (long?)_baseline?.Available}");
                }
            });
        }

        void UpdateDone()
        {
            Clear(_doneBody);
            var stats = Gtk.Box.New(Gtk.Orientation.Horizontal, 48);
            stats.Append(Stat("removed", $"{_removed} items", "success"));
            stats.Append(Stat("bytes claimed", HumanBytes(_bytes), null));
            stats.Append(Stat("failed", $"{_failed}", _failed > 0 ? "danger" : null));
            // Measured, not summed: free space now against before the removal.
            if (_baseline is { } before && _space is { } after)
            {
                var delta = (long)after.Available - (long)before.Available;
                stats.Append(Stat("volume freed", (delta >= 0 ? "+" : "-") + HumanBytes((ulong)Math.Abs(delta)), delta >= 0 ? "success" : "warning"));
            }
            _doneBody.Append(stats);
            _doneBody.Append(Text("The treemap is being re-scanned so the numbers on screen match the disk again.", "dim"));
            if (_space is { } volume)
            {
                _doneBody.Append(SpaceMeter(volume, 0));
            }
            if (_rescanning)
            {
                _doneBody.Append(Text("re-scanning…", "accent"));
            }
            if (_failed > 0)
            {
                _doneBody.Append(Separator());
                _doneBody.Append(Caps("What could not be removed"));
                foreach (var (path, error) in _log.Where(entry => entry.Error is not null).Take(40))
                {
                    var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
                    row.Append(Text(Path.GetFileName(path), "danger"));
                    var reason = Text(error!, "dim");
                    reason.Wrap = true;
                    reason.MaxWidthChars = 1;
                    reason.Hexpand = true;
                    row.Append(reason);
                    _doneBody.Append(row);
                }
            }
        }

        // ── shared ──────────────────────────────────────────────────────

        /// The space meter: free now, free after `reclaiming`, and
        /// the used share.
        static Gtk.Box SpaceMeter(SpaceInfo volume, ulong reclaiming)
        {
            var after = volume.AfterRemoving(reclaiming);
            var line = Text(reclaiming > 0
                ? $"{HumanBytes(volume.Available)} free → {HumanBytes(after.Available)} free"
                : $"{HumanBytes(volume.Available)} free of {HumanBytes(volume.Total)}", "value");
            var bar = Gtk.ProgressBar.New();
            bar.AddCssClass("disk");
            bar.Fraction = volume.Total == 0 ? 0 : (double)after.Used / volume.Total;
            var box = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
            box.Append(line);
            box.Append(bar);
            return box;
        }

        static Gtk.Box ScreenHeader(string title, Gtk.Label subtitle)
        {
            var header = Gtk.Box.New(Gtk.Orientation.Horizontal, 14);
            header.AddCssClass("screen-header");
            header.Append(Text(title, "title"));
            subtitle.Valign = Gtk.Align.Center;
            header.Append(subtitle);
            return header;
        }

        static Gtk.Box Hints(params (string Key, Gtk.Label What)[] hints)
        {
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 18);
            row.AddCssClass("screen-footer");
            foreach (var (key, what) in hints)
            {
                var hint = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
                hint.Append(Keycap(key));
                what.AddCssClass("dim");
                hint.Append(what);
                row.Append(hint);
            }
            return row;
        }

        static Gtk.Box Group(string title, params Gtk.Widget[] children)
        {
            var group = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
            group.Append(Caps(title));
            foreach (var child in children)
            {
                group.Append(child);
            }
            return group;
        }

        static Gtk.Box Buttons(params Gtk.Button[] buttons)
        {
            var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            row.Halign = Gtk.Align.End;
            foreach (var button in buttons)
            {
                button.FocusOnClick = false;
                row.Append(button);
            }
            return row;
        }

        static Gtk.Box Stat(string label, string value, string? css)
        {
            var stat = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
            stat.Append(Caps(label));
            var number = Text(value, "big");
            if (css is not null)
            {
                number.AddCssClass(css);
            }
            stat.Append(number);
            return stat;
        }

        static string Css() => """
            .screen-header { padding: 14px 18px; border-bottom: 1px solid alpha(@dt_foreground, 0.12); }
            .screen-footer { padding: 8px 18px; border-top: 1px solid alpha(@dt_foreground, 0.12); }
            .inset { border: 1px solid alpha(@dt_foreground, 0.12); background: @dt_inset; }
            .marked-list { background: transparent; }
            .mark-row { padding: 8px 12px; border-bottom: 1px solid alpha(@dt_foreground, 0.06); }
            .row-dim { opacity: 0.55; }
            .log-row { padding: 3px 12px; font-size: 0.9em; }
            .summary { padding: 18px; border: 1px solid alpha(@dt_foreground, 0.12); background: @dt_surface; }
            .danger { color: @dt_danger; }
            .warning { color: @dt_warning; }
            .success { color: @dt_success; }
            progressbar.danger > trough > progress { background-color: @dt_danger; }
            progressbar.disk > trough > progress { background-color: alpha(@dt_foreground, 0.22); }
            .backdrop { background: alpha(@dt_background, 0.7); }
            .dialog { background: @dt_background; border: 1px solid @dt_border; padding: 20px; }
            .dialog-title { font-size: 1.2em; font-weight: bold; }
            """;
    }
}
