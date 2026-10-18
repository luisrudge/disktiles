// The Linux listing: opendir + readdir64, `.` and `..` skipped, the type from
// `d_type`, and for a leaf statx(2) relative to the open directory.
// Directories are not stated at all; see Scanner.ClassifyDir.
using System.Runtime.InteropServices;

namespace Disktiles.Core;

public unsafe ref struct LinuxDirReader : IDirReader
{
    nint _dir;
    /// The current entry's NUL-terminated name, inside readdir's buffer:
    /// valid until the next readdir, which is when `Facts` may use it.
    byte* _name;
    bool _done;

    public static int Open(string path, out LinuxDirReader reader)
    {
        reader = default;
        var dir = Native.opendir(path);
        if (dir == 0)
        {
            return Marshal.GetLastPInvokeError();
        }
        reader._dir = dir;
        return 0;
    }

    public void Dispose()
    {
        if (_dir != 0)
        {
            _ = Native.closedir(_dir); // the stream is gone either way
            _dir = 0;
        }
    }

    public bool Next(out Entry entry, out int error)
    {
        entry = default;
        error = 0;
        while (!_done)
        {
            var dirent = Native.readdir(_dir);
            if (dirent == null)
            {
                // As std: an errno ends the stream after one `Err`.
                _done = true;
                error = Marshal.GetLastPInvokeError();
                return error != 0;
            }
            var name = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(dirent + Native.DirentName);
            if (name is [(byte)'.'] or [(byte)'.', (byte)'.'])
            {
                continue;
            }
            _name = dirent + Native.DirentName;
            // ponytail: the tree keeps the exact bytes, but child paths are
            // strings, so an invalid UTF-8 directory name is joined lossily.
            entry.Name = name;
            switch (dirent[Native.DirentType])
            {
                case Native.DT_DIR: entry.Kind = EntryKind.Directory; break;
                case Native.DT_LNK: entry.Kind = EntryKind.Link; break;
                case Native.DT_REG: entry.Kind = EntryKind.File; break;
                case Native.DT_UNKNOWN:
                    // std's `file_type` falls back to the entry's metadata.
                    if (Native.statx(Native.dirfd(_dir), _name, Native.AT_SYMLINK_NOFOLLOW_LINUX, Native.STATX_MASK, out var stx) != 0)
                    {
                        error = Marshal.GetLastPInvokeError();
                        return true;
                    }
                    entry.Kind = (stx.Mode & FileFacts.TypeMask) switch
                    {
                        FileFacts.TypeDirectory => EntryKind.Directory,
                        FileFacts.TypeLink => EntryKind.Link,
                        FileFacts.TypeRegular => EntryKind.File,
                        _ => EntryKind.Other,
                    };
                    break;
                default: entry.Kind = EntryKind.Other; break;
            }
            return true;
        }
        return false;
    }

    /// `DirEntry::metadata`: statx(dirfd, name, AT_SYMLINK_NOFOLLOW).
    public readonly int Facts(ref Entry entry)
    {
        if (Native.statx(Native.dirfd(_dir), _name, Native.AT_SYMLINK_NOFOLLOW_LINUX, Native.STATX_MASK, out var stx) != 0)
        {
            return Marshal.GetLastPInvokeError();
        }
        entry.Apparent = stx.Size;
        entry.Allocated = Entry.SaturatingMul512(stx.Blocks);
        entry.Device = Native.MakeDev(stx.DevMajor, stx.DevMinor);
        entry.Inode = stx.Ino;
        entry.Links = stx.Nlink;
        // `SystemTime::duration_since(UNIX_EPOCH)`: before the epoch is 0.
        entry.Modified = stx.MtimeSec < 0 ? 0 : stx.MtimeSec;
        return 0;
    }
}

static unsafe partial class Native
{
    // struct dirent64 on glibc, the same on x86_64 and aarch64.
    public const int DirentType = 18, DirentName = 19;
    public const byte DT_UNKNOWN = 0, DT_DIR = 4, DT_REG = 8, DT_LNK = 10;

    const int AT_FDCWD = -100;
    public const int AT_SYMLINK_NOFOLLOW_LINUX = 0x100;
    /// STATX_BASIC_STATS | STATX_BTIME, std's mask; AT_STATX_SYNC_AS_STAT is 0.
    public const uint STATX_MASK = 0x7ff | 0x800;

    /// `struct statx`, the same on every Linux architecture.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    public struct Statx
    {
        [FieldOffset(16)] public uint Nlink;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Ino;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(48)] public ulong Blocks;
        [FieldOffset(112)] public long MtimeSec;
        [FieldOffset(136)] public uint DevMajor;
        [FieldOffset(140)] public uint DevMinor;
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint opendir(string path);

    [LibraryImport("libc", EntryPoint = "readdir64", SetLastError = true)]
    public static partial byte* readdir(nint dir);

    [LibraryImport("libc")]
    public static partial int closedir(nint dir);

    [LibraryImport("libc")]
    public static partial int dirfd(nint dir);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int statx(int dirfd, byte* path, int flags, uint mask, out Statx buf);

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int statx(int dirfd, string path, int flags, uint mask, out Statx buf);

    /// glibc's makedev, as std builds `st_dev` from statx.
    public static ulong MakeDev(uint major, uint minor) =>
        ((ulong)(major & 0x0000_0fff) << 8) | ((ulong)(major & 0xffff_f000) << 32)
        | (minor & 0x0000_00ffUL) | ((ulong)(minor & 0xffff_ff00) << 12);

    /// `std::fs::metadata` / `symlink_metadata` on Linux: statx of a path.
    public static int StatxPath(string path, bool follow, out FileFacts facts)
    {
        if (statx(AT_FDCWD, path, follow ? 0 : AT_SYMLINK_NOFOLLOW_LINUX, STATX_MASK, out var stx) != 0)
        {
            facts = default;
            return Marshal.GetLastPInvokeError();
        }
        facts = new FileFacts(stx.Mode, stx.Size, stx.Blocks, MakeDev(stx.DevMajor, stx.DevMinor), stx.Ino,
            stx.Nlink, stx.MtimeSec < 0 ? 0 : stx.MtimeSec);
        return 0;
    }

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint realpath(string path, nint resolved);

    [LibraryImport("libc")]
    private static partial void free(nint pointer);

    /// `std::fs::canonicalize`: realpath(3).
    public static string? RealPath(string path)
    {
        var resolved = realpath(path, 0);
        if (resolved == 0)
        {
            return null;
        }
        try
        {
            return Marshal.PtrToStringUTF8(resolved);
        }
        finally
        {
            free(resolved);
        }
    }

    public static string ErrorMessage(int errno) => $"{Marshal.GetPInvokeErrorMessage(errno)} (os error {errno})";
}
