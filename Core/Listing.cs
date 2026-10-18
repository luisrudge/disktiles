// What the lister hands the walk: an interface a ref struct can implement,
// so the walk is generic over the lister with no boxing or virtual calls
// (`where T : IDirReader, allows ref struct`).
namespace Disktiles.Core;

public enum EntryKind : byte { Directory, Link, File, Other }

/// One directory entry, with what a measurement needs: only `Name` and `Kind`
/// until `Facts` stats it. A ref struct: `Name` is the lister's own UTF-8
/// bytes, valid until the next entry, and is copied only into the tree's
/// name blocks.
public ref struct Entry
{
    public ReadOnlySpan<byte> Name;
    public EntryKind Kind;
    public ulong Apparent;
    public ulong Allocated;
    public long Modified;
    public ulong Device;
    public ulong Inode;
    public uint Links;

    public readonly bool Shared => Links > 1;

    public static ulong SaturatingMul512(ulong blocks) =>
        blocks > ulong.MaxValue / 512 ? ulong.MaxValue : blocks * 512;
}

public interface IDirReader : IDisposable
{
    /// The next entry: false when the listing is done. On true, `error` is
    /// nonzero when this entry (or the listing) failed.
    bool Next(out Entry entry, out int error);

    /// Size, identity and age of the current leaf; 0 or the errno.
    int Facts(ref Entry entry);
}

/// What a stat of a path says.
public readonly record struct FileFacts(ushort Mode, ulong Size, ulong Blocks, ulong Device, ulong Inode,
    uint Links, long MtimeSec)
{
    // st_mode file types: S_IFMT, S_IFDIR, S_IFLNK, S_IFREG.
    public const ushort TypeMask = 0xF000, TypeDirectory = 0x4000, TypeLink = 0xA000, TypeRegular = 0x8000;

    public bool IsDir => (Mode & TypeMask) == TypeDirectory;

    /// `std::fs::symlink_metadata` / `std::fs::metadata` of a full path.
    public static int Of(string path, bool follow, out FileFacts facts) => Native.StatxPath(path, follow, out facts);
}

/// Error numbers: errno.
public static class Errors
{
    public const int NotFound = 2; // ENOENT

    public static string Text(int error) => Native.ErrorMessage(error);
}
