// The name filter: "/" opens it, typing dims what does not match, Enter lays
// out only the matches (the largest selected), Escape clears it. A field of
// three keys on purpose: no editor state to keep in sync with the tree, and
// Escape always gives the keyboard back.
using Disktiles.Core;
using static Disktiles.Look;

namespace Disktiles;

sealed class FilterBar
{
    public readonly Gtk.Box Widget;
    readonly App _app;
    readonly AppState _state;
    readonly Gtk.Label _text, _summary, _hint, _notice;
    readonly Func<Key, bool> _field;
    string _find = "";
    bool _open, _finding, _applyPending;
    // Bumped per keystroke: only the newest search's result is kept.
    int _epoch;
    // The folder the text was typed in.
    int[] _base = [];

    public FilterBar(App app)
    {
        _app = app;
        _state = app.State;
        _field = Field;
        Widget = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        Widget.AddCssClass("find");
        Widget.Visible = false;
        var icon = Gtk.Image.NewFromIconName("system-search-symbolic");
        icon.AddCssClass("find-icon");
        _text = Gtk.Label.New("");
        _summary = Gtk.Label.New("");
        _summary.AddCssClass("find-summary");
        _hint = Gtk.Label.New("");
        _hint.AddCssClass("find-hint");
        Widget.Append(icon);
        Widget.Append(_text);
        Widget.Append(_summary);
        Widget.Append(_hint);
        app.Header.Under.Append(Widget);

        // "nothing here matches …", as a notice in the panel.
        _notice = Sidebar.Text("", "find-notice", Pango.EllipsizeMode.End);
        _notice.Visible = false;
        app.Sidebar.Add(Slot.Notice, _notice);

        app.AddCss("""
            .find { border: 1px solid alpha(currentColor, 0.2); padding: 2px 8px; }
            .find.open { border-color: alpha(currentColor, 0.7); }
            .find.applied { border-color: @dt_highlight; }
            .find.applied .find-icon, .find.applied .find-summary { color: @dt_highlight; }
            .find .placeholder, .find-icon, .find-summary { opacity: 0.7; }
            .find-summary, .find-hint { font-size: 0.85em; }
            .find-hint { opacity: 0.45; }
            .find-notice { color: @dt_highlight; }
            """);

        app.Keys.Bind("slash s", "Filter", "filter by name", Open);
        // A filter is the first thing Escape takes away.
        app.Keys.Bind("Escape", "Filter", "clear the filter", () =>
        {
            if (_find.Length == 0 && _state.Matches is null)
            {
                return false;
            }
            Clear();
            return true;
        }, priority: 10);
        _state.TreeChanged += Clear;
        // AppState lets a filter lapse above the folder it was typed in.
        _state.FolderChanged += () =>
        {
            if (_find.Length > 0 && !_state.Crumbs.AsSpan().StartsWith(_base))
            {
                Clear();
            }
        };
    }

    void Open()
    {
        if (!_open)
        {
            _open = true;
            _app.Keys.Grab(_field);
        }
        Update();
    }

    void Close()
    {
        _open = false;
        _app.Keys.Release(_field);
    }

    // Every key while open, so typing never reaches a binding.
    bool Field(Key key)
    {
        switch (key.Name)
        {
            case "Escape":
                Clear();
                break;
            case "Return":
                Apply();
                break;
            case "BackSpace":
                if (_find.Length > 0)
                {
                    _find = _find[..^(char.IsLowSurrogate(_find[^1]) && _find.Length > 1 ? 2 : 1)];
                }
                Refresh();
                break;
            default:
                if (key.Text is { } typed)
                {
                    _find += typed;
                    Refresh();
                }
                break;
        }
        return true;
    }

    /// What the text matches in the folder on screen, off the UI thread: a
    /// whole disk is millions of names, and typing must not wait for them.
    async void Refresh()
    {
        var epoch = ++_epoch;
        _notice.Visible = false;
        var needle = _find;
        if (_state.Current is not { } node || needle.Trim().Length == 0)
        {
            _finding = false;
            _state.SetFilter(null, false);
            Update();
            return;
        }
        _finding = true;
        _base = _state.Crumbs;
        var at = _base;
        Update();
        var found = await Task.Run(() => Filter.Run(node, at, needle));
        if (epoch != _epoch)
        {
            return;
        }
        _finding = false;
        _state.SetFilter(found, _state.FilterApplied);
        if (_applyPending)
        {
            _applyPending = false;
            Apply();
        }
        Update();
    }

    /// Lay out only the matches, with the largest selected so Space marks it.
    void Apply()
    {
        Close();
        if (_finding)
        {
            _applyPending = true;
            Update();
            return;
        }
        if (_state.Matches is not { } matches)
        {
            Update();
            return;
        }
        if (matches.Count == 0)
        {
            _notice.SetText($"nothing here matches {matches.Needle}");
            _notice.Visible = true;
            Update();
            return;
        }
        _state.SetFilter(matches, applied: true);
        int[]? largest = null;
        ulong bytes = 0;
        foreach (var (crumbs, keep) in matches.Keep)
        {
            if (keep.Whole && _state.Resolve(crumbs) is { } node && (largest is null || node.Bytes > bytes))
            {
                (largest, bytes) = (crumbs, node.Bytes);
            }
        }
        _state.Select(largest);
        _notice.Visible = false;
        Update();
    }

    /// Drop the text and the filter with it.
    void Clear()
    {
        _epoch++;
        _finding = _applyPending = false;
        _find = "";
        Close();
        _notice.Visible = false;
        if (_state.Matches is not null || _state.FilterApplied)
        {
            _state.SetFilter(null, false);
        }
        Update();
    }

    // What the text matches, said beside it, and what Enter and Escape will do.
    void Update()
    {
        var showing = _open || _find.Length > 0;
        if (showing != Widget.Visible)
        {
            // The filter takes the legends' place at the row's current
            // height: beside them it made the legend wrap, and the treemap
            // below jumped while you typed.
            var row = _app.Header.Legends.GetParent()!;
            row.HeightRequest = showing ? row.GetHeight() : -1;
            _app.Header.Legends.Visible = !showing;
        }
        Widget.Visible = showing;
        if (!Widget.Visible)
        {
            return;
        }
        var (summary, hint) = _state.Matches switch
        {
            null when _finding => ("searching…", ""),
            null => ("", "type to filter"),
            { Count: 0 } => ("no matches", "esc clears"),
            var m => ($"{HumanCount((ulong)m.Count)} match{(m.Count == 1 ? "" : "es")} · {HumanBytes(m.Bytes)}",
                _state.FilterApplied ? "esc clears" : "enter shows only these"),
        };
        _text.SetText(_find.Length == 0 ? "Filter by name" : _find);
        Toggle(_text, "placeholder", _find.Length == 0);
        _summary.SetText(summary);
        _summary.Visible = summary.Length > 0;
        _hint.SetText(hint);
        Toggle(Widget, "open", _open);
        Toggle(Widget, "applied", _state.FilterApplied);
        if (TestHooks.Trace)
        {
            Console.Error.WriteLine($"trace: filter \"{_find}\" {(_open ? "open" : "closed")}{(_state.FilterApplied ? " applied" : "")}: {summary} | {hint} | {_app.Treemap.Tiles.Count} tiles, selected [{string.Join(',', _state.Selected ?? [])}]");
        }
    }

    static void Toggle(Gtk.Widget widget, string css, bool on)
    {
        if (on)
        {
            widget.AddCssClass(css);
        }
        else
        {
            widget.RemoveCssClass(css);
        }
    }
}
