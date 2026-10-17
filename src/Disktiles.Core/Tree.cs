// The node, aggregation, and hardlink dedup.
//
// Laid out as an arena: nodes are unmanaged structs in large blocks, a
// directory's children one contiguous run of a block, and names UTF-8 bytes in
// blocks of their own. The GC sees a few hundred big arrays with nothing
// inside to trace, instead of a million objects and strings, so a collection
// during or after a scan no longer marks and moves the whole tree. The blocks
// are ordinary managed arrays, not NativeMemory: a tree is freed by the GC
// when its last `NodeRef` goes, so a filter still running on the old tree
// when a rescan or a re-sort replaces it can never read freed memory.
using System.Collections;
using System.Runtime.CompilerServices;
using System.Text;

namespace Disktiles.Core;

public enum NodeKind : byte { Directory, File, Symlink, Other }

public enum Metric : byte { Bytes, Files }

/// Where a node's name is: `Length` UTF-8 bytes at `Start` of name block `Block`.
public readonly record struct NameRef(int Block, int Start, int Length);

/// A directory's children: `Count` nodes at `Start` of node block `Block`.
public readonly record struct Run(int Block, int Start, int Count);

/// One entry in the scanned tree, 56 bytes with no references; walk it by
/// `ref`, never by copy. Its name and children live in the `Tree` that holds
/// it. Hardlink identity is not kept: dedup happens as leaves are listed.
public struct Node
{
    public ulong Bytes;
    public ulong Files;
    public long Modified;
    public NameRef Name;
    public Run Children;
    public uint Dirs;
    public NodeKind Kind;
    public bool ReadError;
    public Category Category;
    public Reclaim Reclaim;

    public static Node Directory(NameRef name, Run children, bool readError) =>
        new() { Name = name, Kind = NodeKind.Directory, Children = children, Dirs = 1, ReadError = readError };

    public static Node Entry(NameRef name, NodeKind kind, ulong bytes) =>
        new() { Name = name, Kind = kind, Bytes = bytes, Files = kind == NodeKind.File ? 1UL : 0UL };

    public readonly bool IsDir => Kind == NodeKind.Directory;

    public readonly ulong Value(Metric metric) => metric == Metric.Bytes ? Bytes : Files;
}

/// A node as the app holds it: its tree and its slot. Cheap to copy, and
/// valid as long as it is held: a tree is never changed once it is handed
/// out (a re-sort makes a new one), so a handle can only be old, never wrong.
public readonly struct NodeRef(Tree tree, int block, int index)
{
    public Tree Tree => tree;
    public ref readonly Node Data => ref tree.At(block, index);
    internal ref Node Slot => ref tree.At(block, index);
    /// Decoded on each call: keep it for display, compare with `NameIs`.
    public string Name => tree.NameString(in Data);
    public ReadOnlySpan<byte> Utf8Name => tree.Name(in Data);
    public ulong Bytes => Data.Bytes;
    public ulong Files => Data.Files;
    public uint Dirs => Data.Dirs;
    public long Modified => Data.Modified;
    public NodeKind Kind => Data.Kind;
    public bool IsDir => Data.IsDir;
    public bool ReadError => Data.ReadError;
    public Category Category => Data.Category;
    public Reclaim Reclaim => Data.Reclaim;
    public ChildList Children => new(tree, Data.Children);
    public ulong Value(Metric metric) => Data.Value(metric);
}

/// A directory's children as handles, in their order.
public readonly struct ChildList(Tree tree, Run run) : IReadOnlyList<NodeRef>
{
    public int Length => run.Count;
    public int Count => run.Count;

    public NodeRef this[int index] => (uint)index < (uint)run.Count
        ? new NodeRef(tree, run.Block, run.Start + index)
        : throw new ArgumentOutOfRangeException(nameof(index));

    /// The nodes themselves, for walks that need no handle.
    public ReadOnlySpan<Node> Span => tree.Span(run);

    /// The position of the child called `name`, or -1.
    public int IndexOf(string name)
    {
        var utf8 = Encoding.UTF8.GetBytes(name);
        var children = Span;
        for (var index = 0; index < children.Length; index++)
        {
            if (tree.Name(in children[index]).SequenceEqual(utf8))
            {
                return index;
            }
        }
        return -1;
    }

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<NodeRef> IEnumerable<NodeRef>.GetEnumerator()
    {
        for (var index = 0; index < run.Count; index++)
        {
            yield return this[index];
        }
    }
    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<NodeRef>)this).GetEnumerator();

    public struct Enumerator(ChildList list)
    {
        int _index = -1;
        public readonly NodeRef Current => list[_index];
        public bool MoveNext() => ++_index < list.Length;
    }
}

/// A scanned tree: the blocks every node and name is in. The root is the
/// only node of its own block.
public sealed class Tree
{
    /// Levels whose subtrees are aggregated (and classified) in parallel.
    public const int ParallelLevels = 4;

    /// How many levels a walk that recurses once per level goes on one
    /// stack: a pool thread's holds about 3 000, and Windows paths allow
    /// about 16 000.
    public const int LevelsPerStack = 1000;

    /// Run `work` on a new thread with a stack of its own, and wait; what it
    /// throws is thrown here. ponytail: a thread per thousand levels, not an
    /// iterative walk, since trees that deep are runaway copies, and rare.
    public static void OnFreshStack(Action work)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception error)
            {
                failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error);
            }
        }, 16 << 20);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    readonly Node[][] _nodes;
    readonly byte[][] _names;
    readonly int _rootBlock;

    internal Tree(Node[][] nodes, byte[][] names, int rootBlock) =>
        (_nodes, _names, _rootBlock) = (nodes, names, rootBlock);

    public NodeRef Root => new(this, _rootBlock, 0);

    internal ref Node At(int block, int index) => ref _nodes[block][index];

    internal ref Node RootNode => ref _nodes[_rootBlock][0];

    internal Span<Node> Span(Run run) => run.Count == 0 ? [] : _nodes[run.Block].AsSpan(run.Start, run.Count);

    public Span<Node> Children(in Node node) => Span(node.Children);

    /// The name as the file system gave it, UTF-8.
    public ReadOnlySpan<byte> Name(in Node node) => Name(_names, node.Name);

    static ReadOnlySpan<byte> Name(byte[][] names, NameRef name) =>
        names[name.Block].AsSpan(name.Start, name.Length);

    /// The name for display; decoded on each call.
    public string NameString(in Node node) => Encoding.UTF8.GetString(Name(node));

    /// Totals and order again, in place, beneath `root`: for a tree no one
    /// else holds yet (a hand-made one, or a scan before it is shown).
    public static void Aggregate(NodeRef root, Metric metric) => root.Tree.AggregateAt(ref root.Slot, metric, 0);

    /// The tree ordered by `metric` instead, as a copy, as the app's
    /// toggle_metric clones and re-aggregates. Names are shared: they never
    /// change. Handles into the old tree stay what they were.
    public static NodeRef Resorted(NodeRef root, Metric metric)
    {
        var old = root.Tree;
        var copy = new Tree([.. old._nodes.Select(static block => (Node[])block.Clone())], old._names, old._rootBlock);
        copy.AggregateAt(ref copy.RootNode, metric, 0);
        return copy.Root;
    }

    void AggregateAt(ref Node node, Metric metric, int depth)
    {
        if (!node.IsDir)
        {
            return;
        }
        var children = Children(in node);
        if (depth < ParallelLevels && children.Length > 1)
        {
            // A lambda cannot hold the span; it holds the block and indexes it.
            var (block, start) = (_nodes[node.Children.Block], node.Children.Start);
            Parallel.For(start, start + children.Length, i => AggregateAt(ref block[i], metric, depth + 1));
        }
        else if (depth % LevelsPerStack == LevelsPerStack - 1)
        {
            var (block, start, count) = (_nodes[node.Children.Block], node.Children.Start, children.Length);
            OnFreshStack(() =>
            {
                for (var i = start; i < start + count; i++)
                {
                    AggregateAt(ref block[i], metric, depth + 1);
                }
            });
        }
        else
        {
            foreach (ref var child in children)
            {
                AggregateAt(ref child, metric, depth + 1);
            }
        }
        Summarize(ref node, children, metric, _names);
    }

    /// A directory's totals from its finished children, which are then
    /// sorted largest first. The scan calls it as each directory completes.
    internal static void Summarize(ref Node node, Span<Node> children, Metric metric, byte[][] names)
    {
        ulong bytes = 0, files = 0;
        uint dirs = 1;
        long modified = 0;
        foreach (ref readonly var child in children)
        {
            modified = Math.Max(modified, child.Modified);
            // Saturating: a corrupt volume's file table can claim any size.
            bytes = SaturatingAdd(child.Bytes, bytes);
            files += child.Files;
            dirs += child.Dirs;
        }
        node.Bytes = bytes;
        node.Files = files;
        node.Dirs = dirs;
        node.Modified = modified;

        // Largest first, ties by name. Unstable, which is fine: names are
        // unique, so the order is total anyway.
        if (metric == Metric.Bytes)
        {
            children.Sort(new ByBytes(names));
        }
        else
        {
            children.Sort(new ByFiles(names));
        }
    }

    /// Ties compare names bytewise. Struct comparers,
    /// so the span sort is specialized for them.
    readonly struct ByBytes(byte[][] names) : IComparer<Node>
    {
        public int Compare(Node left, Node right) => right.Bytes != left.Bytes
            ? right.Bytes.CompareTo(left.Bytes)
            : Name(names, left.Name).SequenceCompareTo(Name(names, right.Name));
    }

    readonly struct ByFiles(byte[][] names) : IComparer<Node>
    {
        public int Compare(Node left, Node right) => right.Files != left.Files
            ? right.Files.CompareTo(left.Files)
            : Name(names, left.Name).SequenceCompareTo(Name(names, right.Name));
    }

    public static ulong SaturatingAdd(ulong a, ulong b)
    {
        var sum = a + b;
        return sum < a ? ulong.MaxValue : sum;
    }

    /// A tree written out by hand (the tests), stored as a scan stores one,
    /// not yet aggregated.
    public static NodeRef Of(Draft root)
    {
        var arena = new Arena();
        var writer = arena.Local;
        return arena.Finish(Store(root)).Root;

        Node Store(Draft draft)
        {
            var run = writer.Reserve(draft.Children.Length, out var block);
            for (var i = 0; i < draft.Children.Length; i++)
            {
                block[run.Start + i] = Store(draft.Children[i]);
            }
            var name = writer.AddName(draft.Name);
            return draft.Kind == NodeKind.Directory
                ? Node.Directory(name, run, false)
                : Node.Entry(name, draft.Kind, draft.Bytes);
        }
    }
}

/// A node for `Tree.Of`.
public sealed record Draft(string Name, NodeKind Kind, ulong Bytes, Draft[] Children)
{
    public static Draft Dir(string name, params Draft[] children) => new(name, NodeKind.Directory, 0, children);
    public static Draft File(string name, ulong bytes) => new(name, NodeKind.File, bytes, []);
}

/// Where one scan's workers put nodes and names: each worker fills blocks of
/// its own, so reserving space takes no lock; only a new block does.
public sealed class Arena : IDisposable
{
    /// Nodes per block (896 KiB); a directory with more gets a block to itself.
    const int NodesPerBlock = 16 * 1024;
    const int NameBytesPerBlock = 1024 * 1024;

    readonly Lock _lock = new();
    Node[][] _nodes = new Node[64][];
    int _nodeCount;
    byte[][] _names = new byte[64][];
    int _nameCount;
    readonly ThreadLocal<Writer> _writers;

    public Arena() => _writers = new(() => new Writer(this));

    /// The per-thread writers; Finish does it, a cancelled scan's context too.
    public void Dispose() => _writers.Dispose();

    /// This thread's writer.
    public Writer Local => _writers.Value!;

    /// Every name block so far. Replaced, never changed, when it grows, so a
    /// reader needs no lock: a directory being summarized finds every block
    /// its children's names went into, since they were added before the
    /// fence that let it finish.
    internal byte[][] Names => Volatile.Read(ref _names);

    /// The finished tree, `root` stored in a block of its own; the arena is done with.
    public Tree Finish(Node root)
    {
        Dispose();
        var rootBlock = Add(ref _nodes, ref _nodeCount, [root]);
        return new Tree(_nodes[.._nodeCount], _names[.._nameCount], rootBlock);
    }

    int Add<T>(ref T[][] blocks, ref int count, T[] block)
    {
        lock (_lock)
        {
            if (count == blocks.Length)
            {
                var grown = new T[blocks.Length * 2][];
                blocks.CopyTo(grown, 0);
                Volatile.Write(ref blocks, grown);
            }
            blocks[count] = block;
            return count++;
        }
    }

    /// One worker's current blocks.
    public sealed class Writer(Arena arena)
    {
        Node[] _nodes = [];
        int _nodeBlock, _nodesUsed;
        byte[] _names = [];
        int _nameBlock, _namesUsed;

        /// Copy `name` into this worker's name blocks.
        public NameRef AddName(ReadOnlySpan<byte> name)
        {
            if (_names.Length - _namesUsed < name.Length)
            {
                _names = GC.AllocateUninitializedArray<byte>(Math.Max(NameBytesPerBlock, name.Length));
                _nameBlock = arena.Add(ref arena._names, ref arena._nameCount, _names);
                _namesUsed = 0;
            }
            name.CopyTo(_names.AsSpan(_namesUsed));
            var reference = new NameRef(_nameBlock, _namesUsed, name.Length);
            _namesUsed += name.Length;
            return reference;
        }

        public NameRef AddName(string name) => AddName(Encoding.UTF8.GetBytes(name));

        /// `count` contiguous node slots for one directory's children; the
        /// block they are in, to fill without looking it up.
        public Run Reserve(int count, out Node[] block)
        {
            if (count == 0)
            {
                block = [];
                return default;
            }
            if (_nodes.Length - _nodesUsed < count)
            {
                // ponytail: the rest of the old block is left empty; at ~15
                // entries a directory, that wastes little of 16 Ki slots.
                _nodes = GC.AllocateUninitializedArray<Node>(Math.Max(NodesPerBlock, count));
                _nodeBlock = arena.Add(ref arena._nodes, ref arena._nodeCount, _nodes);
                _nodesUsed = 0;
            }
            block = _nodes;
            var run = new Run(_nodeBlock, _nodesUsed, count);
            _nodesUsed += count;
            return run;
        }
    }
}

/// Identities a finish pass has met: an 8 MiB lock-free bitmap for small inode
/// numbers on the first volume met, and 64 locked hash-set shards for the rest.
public sealed class Seen
{
    const ulong Bits = 1UL << 26;
    const int ShardBits = 6;

    // Set once: the first volume met wins.
    StrongBox<ulong>? _volume;
    // Made on the first key that fits.
    readonly Lazy<ulong[]> _bits = new(() => new ulong[Bits / 64], LazyThreadSafetyMode.ExecutionAndPublication);
    readonly (Lock Lock, HashSet<(ulong, ulong)> Set)[] _rest =
        [.. Enumerable.Range(0, 1 << ShardBits).Select(_ => (new Lock(), new HashSet<(ulong, ulong)>()))];

    /// Whether the key is new.
    public bool Insert(ulong volume, ulong number)
    {
        var first = Volatile.Read(ref _volume)
            ?? Interlocked.CompareExchange(ref _volume, new StrongBox<ulong>(volume), null)
            ?? _volume!;
        if (first.Value == volume && number < Bits)
        {
            var bit = 1UL << (int)(number % 64);
            return (Interlocked.Or(ref _bits.Value[number / 64], bit) & bit) == 0;
        }
        var shard = (int)((number * 0x9E37_79B9_7F4A_7C15UL) >> (64 - ShardBits));
        ref var rest = ref _rest[shard];
        lock (rest.Lock)
        {
            return rest.Set.Add((volume, number));
        }
    }
}
