// The name filter: "/" opens it, typing dims what does not match, Enter lays
// out only the matches (the largest selected), Escape clears it. A field of
// three keys on purpose: no editor state to keep in sync with the tree, and
// Escape always gives the keyboard back.
using Disktiles.Core;

namespace Disktiles;

/// The filter field under the trail, in the legend's place while shown.
sealed record FilterModel(bool Visible, string Text, bool Placeholder, string Summary, string Hint, bool Open, bool Applied, string Notice);

sealed partial class App
{
    string _find = "";
    bool _filterOpen, _finding, _applyPending;
    // Bumped per keystroke: only the newest search's result is kept.
    int _filterEpoch;
    // The folder the text was typed in.
    int[] _filterBase = [];
    string _filterNotice = "";

    void InitFilter()
    {
        Keys.Bind("slash s", "Filter", "filter by name", OpenFilter);
        // A filter is the first thing Escape takes away.
        Keys.Bind("Escape", "Filter", "clear the filter", () =>
        {
            if (_find.Length == 0 && State.Matches is null)
            {
                return false;
            }
            ClearFilter();
            return true;
        }, priority: 10);
        State.TreeChanged += ClearFilter;
        // AppState lets a filter lapse above the folder it was typed in.
        State.FolderChanged += () =>
        {
            if (_find.Length > 0 && !State.Crumbs.AsSpan().StartsWith(_filterBase))
            {
                ClearFilter();
            }
        };
        State.FilterChanged += () => Touch(Part.Header | Part.Tiles);
    }

    void OpenFilter()
    {
        if (!_filterOpen)
        {
            _filterOpen = true;
            Keys.Grab(FilterField);
        }
        Touch(Part.Header);
    }

    void CloseFilter()
    {
        _filterOpen = false;
        Keys.Release(FilterField);
    }

    // Every key while open, so typing never reaches a binding.
    bool FilterField(Key key)
    {
        switch (key.Name)
        {
            case "Escape":
                ClearFilter();
                break;
            case "Return":
                ApplyFilter();
                break;
            case "BackSpace":
                if (_find.Length > 0)
                {
                    _find = _find[..^(char.IsLowSurrogate(_find[^1]) && _find.Length > 1 ? 2 : 1)];
                }
                RefreshFilter();
                break;
            default:
                if (key.Text is { } typed)
                {
                    _find += typed;
                    RefreshFilter();
                }
                break;
        }
        return true;
    }

    /// What the text matches in the folder on screen, off the UI thread: a
    /// whole disk is millions of names, and typing must not wait for them.
    void RefreshFilter()
    {
        var epoch = ++_filterEpoch;
        _filterNotice = "";
        var needle = _find;
        if (State.Current is not { } node || needle.Trim().Length == 0)
        {
            _finding = false;
            State.SetFilter(null, false);
            Touch(Part.Header);
            return;
        }
        _finding = true;
        _filterBase = State.Crumbs;
        var at = _filterBase;
        Touch(Part.Header);
        Task.Run(() => Core.Filter.Run(node, at, needle)).ContinueWith(task => Ui.Post(() =>
        {
            if (epoch != _filterEpoch || !task.IsCompletedSuccessfully)
            {
                return;
            }
            _finding = false;
            State.SetFilter(task.Result, State.FilterApplied);
            if (_applyPending)
            {
                _applyPending = false;
                ApplyFilter();
            }
            Touch(Part.Header);
            Log($"filter \"{_find}\": {FilterBar.Summary}");
        }), TaskScheduler.Default);
    }

    /// Lay out only the matches, with the largest selected so Space marks it.
    void ApplyFilter()
    {
        CloseFilter();
        if (_finding)
        {
            _applyPending = true;
            Touch(Part.Header);
            return;
        }
        if (State.Matches is not { } matches)
        {
            Touch(Part.Header);
            return;
        }
        if (matches.Count == 0)
        {
            _filterNotice = $"nothing here matches {matches.Needle}";
            Touch(Part.Header | Part.Panel);
            return;
        }
        State.SetFilter(matches, applied: true);
        int[]? largest = null;
        ulong bytes = 0;
        foreach (var (crumbs, keep) in matches.Keep)
        {
            if (keep.Whole && State.Resolve(crumbs) is { } node && (largest is null || node.Bytes > bytes))
            {
                (largest, bytes) = (crumbs, node.Bytes);
            }
        }
        State.Select(largest);
        _filterNotice = "";
        Touch(Part.Header | Part.Panel);
    }

    /// Drop the text and the filter with it.
    void ClearFilter()
    {
        _filterEpoch++;
        _finding = _applyPending = false;
        _find = "";
        CloseFilter();
        _filterNotice = "";
        if (State.Matches is not null || State.FilterApplied)
        {
            State.SetFilter(null, false);
        }
        Touch(Part.Header | Part.Panel);
    }

    // What the text matches, said beside it, and what Enter and Escape will do.
    public FilterModel FilterBar
    {
        get
        {
            var (summary, hint) = State.Matches switch
            {
                null when _finding => ("searching…", ""),
                null => ("", "type to filter"),
                { Count: 0 } => ("no matches", "esc clears"),
                var m => ($"{Look.HumanCount((ulong)m.Count)} match{(m.Count == 1 ? "" : "es")} · {Look.HumanBytes(m.Bytes)}",
                    State.FilterApplied ? "esc clears" : "enter shows only these"),
            };
            return new FilterModel(_filterOpen || _find.Length > 0, _find.Length == 0 ? "Filter by name" : _find, _find.Length == 0,
                summary, hint, _filterOpen, State.FilterApplied, _filterNotice);
        }
    }
}
