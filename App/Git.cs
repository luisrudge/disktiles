// What git says about a checkout in the selection: before deleting a worktree
// the question is "would anything be lost": uncommitted changes, stashes,
// commits nobody pushed. Three cheap git calls, off the UI thread, one
// checkout at a time, remembered per path until the next scan.
using System.ComponentModel;
using System.Diagnostics;

namespace Disktiles;

/// A checkout's state, as far as losing work is concerned. Changed is null
/// when asking would run a program the checkout names; Unpushed is null
/// without an upstream.
sealed record GitState(int? Changed, int Stashes, int? Unpushed)
{
    public bool IsClean => Changed == 0 && Stashes == 0 && Unpushed == 0;

    /// `clean, no stash`, `3 changed, 1 stash, 2 unpushed`.
    public string Summary => string.Join(", ", new[]
    {
        Changed switch { 0 => "clean", null => "changes unknown", var n => $"{n} changed" },
        Stashes switch { 0 => "no stash", 1 => "1 stash", var n => $"{n} stashes" },
        Unpushed switch { 0 => null, null => "no upstream", var n => $"{n} unpushed" },
    }.OfType<string>());
}

/// The panel's git line: shown for a checkout in the selection.
sealed record GitModel(bool Visible, string Text, bool Clean);

sealed partial class App
{
    // null: asked, and git could not say.
    readonly Dictionary<string, GitState?> _gitKnown = [];
    readonly HashSet<string> _gitAsking = [];
    Task _gitQueue = Task.CompletedTask;
    string? _gitShown;

    void InitGit()
    {
        State.TreeChanged += () =>
        {
            _gitKnown.Clear();
            _gitAsking.Clear();
        };
    }

    /// Follows the selection section, which is throttled to 10/s; a stat per update.
    void GitFollow(int[] crumbs, Disktiles.Core.NodeRef node)
    {
        var path = node.IsDir ? State.PathOf(crumbs) : null;
        _gitShown = path is not null && Git.IsCheckout(path) ? path : null;
        if (_gitShown is not { } shown || _gitKnown.ContainsKey(shown) || !_gitAsking.Add(shown))
        {
            return;
        }
        // One at a time: a pointer sweeping a folder of checkouts must not
        // start a dozen gits at once on a two-core laptop.
        var task = _gitQueue.ContinueWith(_ =>
        {
            try
            {
                return Git.State(shown);
            }
            catch (IOException)
            {
                return null;
            }
        }, TaskScheduler.Default);
        _gitQueue = task;
        task.ContinueWith(done => Ui.Post(() =>
        {
            if (!_gitAsking.Remove(shown))
            {
                return;  // a new scan started meanwhile
            }
            _gitKnown[shown] = done.Result;
            Log($"git {shown} -> {done.Result?.Summary ?? "not readable"}");
            if (_gitShown == shown)
            {
                Touch(Part.Selection);
            }
        }), TaskScheduler.Default);
    }

    public GitModel GitLine => _gitShown is not { } path ? new(false, "", false)
        : _gitKnown.TryGetValue(path, out var state) ? new(true, state?.Summary ?? "not readable", state?.IsClean == true)
        : new(true, "asking…", false);
}

static class Git
{
    /// The top of a checkout: a `.git` directory, or the `.git` file a worktree gets.
    public static bool IsCheckout(string path)
    {
        var git = Path.Join(path, ".git");
        return Directory.Exists(git) || File.Exists(git);
    }

    /// Ask git; null when git is not installed or cannot answer.
    public static GitState? State(string path)
    {
        if (!IsCheckout(path))
        {
            return null;
        }
        int? changed = null;
        if (!NamesItsOwnFilters(path))
        {
            // Submodules are other checkouts with configs of their own.
            if (Run(path, "status", "--porcelain=v1", "-z", "--ignore-submodules=all") is not { Code: 0 } status)
            {
                return null;
            }
            changed = status.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Length;
        }
        // Counted from the reflog, not listed: `stash list` is `git log`,
        // which verifies signatures with the checkout's gpg.program when its
        // config sets log.showSignature.
        var stashes = Count(Run(path, "rev-list", "--walk-reflogs", "--count", "refs/stash")) ?? 0;
        var unpushed = Count(Run(path, "rev-list", "--count", "@{upstream}..HEAD"));
        return new GitState(changed, stashes, unpushed);

        static int? Count((int Code, string Output)? run) =>
            run is { Code: 0, Output: var text } && int.TryParse(text.Trim(), out var n) ? n : null;
    }

    // `git status` runs a file's clean filter, and a downloaded repository
    // can name any program as one in .git/config. No -c can turn off drivers
    // whose names are not known in advance, so a checkout that defines any is
    // not asked for its status at all. Filters from the user's own config
    // (git-lfs) are theirs and still run. Unreadable: the cautious answer.
    static bool NamesItsOwnFilters(string path) =>
        Run(path, "config", "--show-scope", "--includes", "--get-regexp", @"^filter\.") switch
        {
            { Code: 1 } => false,
            { Code: 0, Output: var lines } => lines.Split('\n').Any(l => l.StartsWith("local\t") || l.StartsWith("worktree\t")),
            _ => true,
        };

    // A checkout's own config can name programs for git to run: an
    // fsmonitor on every status, hooks, a pager, a signature verifier, and
    // whatever a partial clone would run to fetch. A command-line -c outranks
    // the repository's config; selecting a folder runs nothing it contains,
    // and nothing here reaches the network.
    static readonly string[] Guards =
    [
        "-c", "core.fsmonitor=false", "-c", "core.hooksPath=/dev/null", "-c", "core.pager=cat",
        "-c", "log.showSignature=false", "-c", "gpg.program=false", "-c", "gpg.ssh.program=false",
        "-c", "gpg.x509.program=false", "-c", "core.sshCommand=false", "-c", "protocol.allow=never",
    ];

    static (int Code, string Output)? Run(string path, params string[] args) =>
        Exec("git", ["-C", path, .. Guards, .. args]);

    // Never through a shell, never prompting, paging or taking the index lock.
    static (int Code, string Output)? Exec(string program, string[] args)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        try
        {
            using var process = Process.Start(start)!;
            process.StandardInput.Close();
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output);
        }
        catch (Win32Exception)
        {
            return null;  // not installed
        }
    }
}
