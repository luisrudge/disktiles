// What both platform listers share: an interface a ref struct can implement,
// so the walk is generic over the lister with no boxing or virtual calls
// (`where T : IDirReader, allows ref struct`).
namespace Disktiles.Core;

public enum EntryKind : byte { Directory, Link, File, Other }

/// One directory entry, with what a measurement needs. On macOS the listing
/// fills every field; on Linux only `Name` and `Kind` until `Facts` stats it.
/// A ref struct: `Name` is the lister's own UTF-8 bytes, valid until the next
/// entry, and is copied only into the tree's name blocks.
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
    public uint Flags;

    public readonly bool Shared => Links > 1;

    /// A directory iCloud Drive or a File Provider has evicted (macOS only).
    public readonly bool Evicted => Kind == EntryKind.Directory && (Flags & Native.SF_DATALESS) != 0;

    public static ulong SaturatingMul512(ulong blocks) =>
        blocks > ulong.MaxValue / 512 ? ulong.MaxValue : blocks * 512;
}

public interface IDirReader : IDisposable
{
    /// The next entry: false when the listing is done. On true, `error` is
    /// nonzero when this entry (or the listing) failed.
    bool Next(out Entry entry, out int error);

    /// Size, identity and age of the current leaf; 0 or the errno. A no-op
    /// where the listing already carried them.
    int Facts(ref Entry entry);
}

/// What a stat of a path says, on either platform.
public readonly record struct FileFacts(ushort Mode, ulong Size, ulong Blocks, ulong Device, ulong Inode,
    uint Links, long MtimeSec)
{
    // st_mode file types: S_IFMT, S_IFDIR, S_IFLNK, S_IFREG.
    public const ushort TypeMask = 0xF000, TypeDirectory = 0x4000, TypeLink = 0xA000, TypeRegular = 0x8000;

    public bool IsDir => (Mode & TypeMask) == TypeDirectory;

    /// `std::fs::symlink_metadata` / `std::fs::metadata` of a full path.
    public static int Of(string path, bool follow, out FileFacts facts) =>
        OperatingSystem.IsLinux() ? Native.StatxPath(path, follow, out facts)
        : OperatingSystem.IsMacOS() ? Native.StatPath(path, follow, out facts)
        : Managed(path, follow, out facts);

    // Elsewhere (Windows): what .NET's file info says, by the literal path.
    // No device or link count: every path is on device 0 with one name. A
    // folder's inode is 64 bits of a hash of its final path, exact case (a
    // case-sensitive folder may hold Foo and foo), so following links still
    // visits each folder once.
    static int Managed(string path, bool follow, out FileFacts facts)
    {
        facts = default;
        var literal = Space.Literal(path);
        try
        {
            FileSystemInfo info = Directory.Exists(literal) ? new DirectoryInfo(literal) : new FileInfo(literal);
            if (!info.Exists)
            {
                return Errors.NotFound;
            }
            if (follow && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { Exists: true } target)
            {
                info = target;
            }
            var link = !follow && info.LinkTarget is not null;
            var mode = link ? TypeLink : info is DirectoryInfo ? TypeDirectory : TypeRegular;
            var size = !link && info is FileInfo file ? (ulong)file.Length : 0;
            var inode = mode == TypeDirectory && Win32.FinalPath(path, out _) is { } final
                ? BitConverter.ToUInt64(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(final))) | 1
                : 0UL;
            long modified;
            try
            {
                modified = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
            }
            catch (ArgumentOutOfRangeException)
            {
                // Past year 9999: NTFS keeps such a time, .NET cannot hold it.
                modified = 0;
            }
            facts = new FileFacts(mode, size, (size + 511) / 512, 0, inode, 1, modified);
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Errors.Of(error);
        }
    }
}

/// Error numbers: errno on Linux and macOS, a Win32 error code on Windows.
public static class Errors
{
    public const int NotFound = 2; // ENOENT, and ERROR_FILE_NOT_FOUND

    public static string Text(int error) => OperatingSystem.IsWindows()
        ? new System.ComponentModel.Win32Exception(error).Message
        : Native.ErrorMessage(error);

    /// The Win32 code an exception from the file APIs carries.
    public static int Of(Exception error) => error is UnauthorizedAccessException ? 5 : error.HResult & 0xFFFF;
}
