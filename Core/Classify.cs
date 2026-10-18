// What a directory is, and whether its space can be had back, from names.
using System.Text;

namespace Disktiles.Core;

public enum Category : byte { Code, AgentScratch, Toolchain, Synced, Git, Media, Documents, Cache, Other }

/// Why a directory's space can be had back; `None`, the zero value, is not at all.
public enum Reclaim : byte
{
    None, Regenerable, SyncHistory, PackageStore, BuildOutput, Reinstallable,
    SandboxLayers, Snapshots, Trash, Temporary,
}

public static class Classifier
{
    /// Chars of a name lowercased on the stack.
    const int Lowered = 32;

    /// `name` decoded with ASCII letters lowercased, into `buffer` when it
    /// fits (the common case, no allocation), else a new array.
    static ReadOnlySpan<char> Lower(ReadOnlySpan<byte> name, Span<char> buffer)
    {
        var target = name.Length <= Lowered ? buffer : new char[Encoding.UTF8.GetMaxCharCount(name.Length)];
        target = target[..Encoding.UTF8.GetChars(name, target)];
        for (var i = 0; i < target.Length; i++)
        {
            var c = target[i];
            target[i] = c is >= 'A' and <= 'Z' ? (char)(c | 0x20) : c;
        }
        return target;
    }

    /// The kind a directory name announces on its own; null if none.
    public static Category? CategoryOfName(ReadOnlySpan<byte> name)
    {
        var lower = Lower(name, stackalloc char[Lowered]);
        if (lower.StartsWith("onedrive - ") || lower.StartsWith("dropbox ("))
        {
            return Category.Synced;
        }
        return lower switch
        {
            "src" or "code" or "projects" or "repos" or "dev" or "work"
                or "workspace" or "workspaces" or "github.com" or "gitlab.com"
                or "sites" or "development" => Category.Code,
            ".codex" or ".claude" or ".herdr" or ".pi" or ".cursor" or ".aider"
                or ".gemini" or ".continue" or ".windsurf" or ".microsandbox" or ".omp"
                or ".agents" or ".openai" or "tries" or "worktrees" or "experiments"
                or "scratch" or "playground" => Category.AgentScratch,
            ".cargo" or ".rustup" or ".local" or ".npm" or ".pnpm-store" or "pnpm"
                or ".bun" or ".deno" or "go" or ".gradle" or ".m2" or ".platformio"
                or "mise" or ".mise" or ".pyenv" or ".nvm" or ".gem" or "gem" or ".rbenv"
                or ".espressif" or ".arduino15" or ".config" or ".vscode" or ".zig"
                or ".rye" or ".conda" or "anaconda3" or "miniconda3" or ".opam"
                or ".ghcup" or ".stack" or ".julia" or ".dotnet" or ".android"
                or ".sdkman" or ".volta" or ".yarn" or ".java" or ".nuget"
                or "xcode" or "coresimulator" => Category.Toolchain,
            "sync" or "dropbox" or "nextcloud" or "google drive" or "onedrive"
                or "pclouddrive" or "mega" or ".stversions"
                or "mobile documents" or "cloudstorage"
                or "iclouddrive" => Category.Synced,
            ".git" => Category.Git,
            "pictures" or "photos" or "music" or "videos" or "movies" or "steam"
                or "steamlibrary" or "steamapps" or "emulation"
                or "models" or ".ollama" or ".lmstudio" or "games" or "wineprefix" => Category.Media,
            "documents" or "desktop" or "downloads" or "books" or "notes"
                or "obsidian" or "public" or "templates" => Category.Documents,
            ".cache" or "cache" or "caches" or ".ccache" or ".sccache" or "_cacache"
                or "__pycache__" or "node_modules" or "trash" or ".trash" or "tmp"
                or ".tmp" or "deriveddata" or "ios devicesupport"
                or "watchos devicesupport"
                or "temp" or "$recycle.bin" or "npm-cache" or "v3-cache" or "inetcache"
                or "d3dscache" or "dxcache" or "glcache" or "crashdumps" => Category.Cache,
            _ => null,
        };
    }

    /// Whether a directory's space can be had back, from its name, its
    /// parent's kind, and (only where a name is too common) its siblings.
    static Reclaim ReclaimOf(Tree tree, ReadOnlySpan<byte> name, Category parent, ReadOnlySpan<Node> siblings)
    {
        var lower = Lower(name, stackalloc char[Lowered]);
        return lower switch
        {
            ".cache" or "cache" or "caches" or ".ccache" or ".sccache" or "_cacache"
                or "npm-cache" or "v3-cache" or "inetcache" or "d3dscache" or "dxcache"
                or "glcache"
                or "ios devicesupport" or "watchos devicesupport" => Reclaim.Regenerable,
            ".stversions" => Reclaim.SyncHistory,
            ".pnpm-store" or "pnpm" => Reclaim.PackageStore,
            "__pycache__" or ".pytest_cache" or ".mypy_cache" or ".ruff_cache"
                or ".next" or ".turbo" or ".parcel-cache"
                or "deriveddata" => Reclaim.BuildOutput,
            "logs" when HasSibling(tree, siblings, "Application Support"u8) => Reclaim.Temporary,
            "target" when HasSibling(tree, siblings, "Cargo.toml"u8) => Reclaim.BuildOutput,
            "node_modules" when HasSibling(tree, siblings, "package.json"u8) => Reclaim.Reinstallable,
            "layers" when parent == Category.AgentScratch => Reclaim.SandboxLayers,
            "snapshots" when parent == Category.AgentScratch => Reclaim.Snapshots,
            "trash" or ".trash" or "$recycle.bin" => Reclaim.Trash,
            "tmp" or ".tmp" or "temp" or "crashdumps" => Reclaim.Temporary,
            _ => Reclaim.None,
        };
    }

    static bool HasDir(ReadOnlySpan<Node> children)
    {
        foreach (ref readonly var child in children)
        {
            if (child.IsDir)
            {
                return true;
            }
        }
        return false;
    }

    static bool HasSibling(Tree tree, ReadOnlySpan<Node> siblings, ReadOnlySpan<byte> wanted)
    {
        foreach (ref readonly var sibling in siblings)
        {
            if (tree.Name(in sibling).SequenceEqual(wanted))
            {
                return true;
            }
        }
        return false;
    }

    /// Assign a category and a reclaim reason to every node beneath `root`.
    public static void Classify(NodeRef top)
    {
        var tree = top.Tree;
        ref var root = ref top.Slot;
        root.Category = Category.Other;
        root.Reclaim = Reclaim.None;
        var siblings = tree.Children(in root);
        foreach (ref var child in siblings)
        {
            // A top-level directory with an unknown name takes the kind of its
            // largest recognisable child: `~/world` is mostly `.git`.
            var name = tree.Name(in child);
            var category = CategoryOfName(name)
                ?? GitStore(tree, in child)
                ?? DominantChildCategory(tree, in child)
                ?? Category.Other;
            var reclaim = child.IsDir ? ReclaimOf(tree, name, Category.Other, siblings) : Reclaim.None;
            ClassifyBelow(tree, ref child, category, reclaim, 1);
        }
    }

    static void ClassifyBelow(Tree tree, ref Node node, Category category, Reclaim reclaim, int depth)
    {
        node.Category = category;
        node.Reclaim = reclaim;
        var children = tree.Children(in node);
        // Most directories hold only files, which take this one's kind as they are.
        if (!HasDir(children))
        {
            foreach (ref var child in children)
            {
                child.Category = category;
                child.Reclaim = reclaim;
            }
            return;
        }
        if (depth < Tree.ParallelLevels)
        {
            var kinds = new (Category, Reclaim)[children.Length];
            for (var index = 0; index < kinds.Length; index++)
            {
                kinds[index] = KindOf(tree, children, index, category, reclaim);
            }
            var parent = node;
            Parallel.For(0, kinds.Length, index =>
                ClassifyBelow(tree, ref tree.Children(in parent)[index], kinds[index].Item1, kinds[index].Item2, depth + 1));
        }
        else if (depth % Tree.LevelsPerStack == Tree.LevelsPerStack - 1)
        {
            var parent = node;
            Tree.OnFreshStack(() =>
            {
                var below = tree.Children(in parent);
                for (var index = 0; index < below.Length; index++)
                {
                    var (childCategory, childReclaim) = KindOf(tree, below, index, category, reclaim);
                    ClassifyBelow(tree, ref below[index], childCategory, childReclaim, depth + 1);
                }
            });
        }
        else
        {
            for (var index = 0; index < children.Length; index++)
            {
                var (childCategory, childReclaim) = KindOf(tree, children, index, category, reclaim);
                ClassifyBelow(tree, ref children[index], childCategory, childReclaim, depth + 1);
            }
        }
    }

    /// What the child at `index` is: its own name wins, otherwise it inherits.
    static (Category, Reclaim) KindOf(Tree tree, ReadOnlySpan<Node> siblings, int index, Category category, Reclaim reclaim)
    {
        ref readonly var child = ref siblings[index];
        if (!child.IsDir)
        {
            return (category, reclaim);
        }
        var name = tree.Name(in child);
        var childCategory = CategoryOfName(name)
            ?? GitStore(tree, in child)
            ?? category;
        var childReclaim = reclaim != Reclaim.None ? reclaim : ReclaimOf(tree, name, category, siblings);
        return (childCategory, childReclaim);
    }

    /// The first recognisable name down the largest children, a few levels deep.
    static Category? DominantChildCategory(Tree tree, in Node node)
    {
        var children = tree.Children(in node);
        for (var level = 0; level < 3; level++)
        {
            var next = -1;
            for (var index = 0; index < children.Length; index++)
            {
                ref readonly var child = ref children[index];
                if (!child.IsDir)
                {
                    continue;
                }
                if (next < 0)
                {
                    next = index;
                }
                if ((CategoryOfName(tree.Name(in child)) ?? GitStore(tree, in child)) is { } found)
                {
                    return found;
                }
            }
            if (next < 0)
            {
                return null;
            }
            children = tree.Children(in children[next]);
        }
        return null;
    }

    static Category? GitStore(Tree tree, in Node node) => IsGitStore(tree, in node) ? Category.Git : null;

    /// A git object store by its shape: `objects`, `refs` and `HEAD`.
    public static bool IsGitStore(Tree tree, in Node node)
    {
        if (!node.IsDir)
        {
            return false;
        }
        var children = tree.Children(in node);
        return HasSibling(tree, children, "objects"u8) && HasSibling(tree, children, "refs"u8)
            && HasSibling(tree, children, "HEAD"u8);
    }
}
