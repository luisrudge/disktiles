// List a directory with getattrlistbulk(2),
// which returns every entry's size, identity, age and flags for a whole buffer
// of entries, instead of an lstat per entry.
using System.Runtime.InteropServices;

namespace Disktiles.Core;

/// A directory being listed, leaving out `.` and `..`.
/// A ref struct, so it lives on the stack.
public ref struct MacDirReader : IDirReader
{
    /// Bytes the kernel fills per call: some hundreds of entries.
    const int BufferBytes = 64 * 1024;

    /// The last finished listing's buffer, for the next one on this thread.
    [ThreadStatic] static byte[]? s_spare;

    readonly int _fd;
    byte[]? _buffer;
    uint _remaining;
    int _offset;
    bool _done;

    MacDirReader(int fd, byte[] buffer)
    {
        _fd = fd;
        _buffer = buffer;
    }

    /// List `dir`; 0 or the errno. A link at `dir` itself is followed.
    public static int Open(string dir, out MacDirReader reader)
    {
        var fd = Native.open(dir, Native.O_RDONLY | Native.O_DIRECTORY | Native.O_CLOEXEC);
        if (fd < 0)
        {
            reader = default;
            return Marshal.GetLastPInvokeError();
        }
        var buffer = s_spare ?? GC.AllocateArray<byte>(BufferBytes, pinned: true);
        s_spare = null;
        reader = new MacDirReader(fd, buffer);
        return 0;
    }

    public void Dispose()
    {
        if (_buffer is { } buffer)
        {
            Native.close(_fd);
            s_spare = buffer;
            _buffer = null;
        }
    }

    /// The listing already carried everything a stat says.
    public readonly int Facts(ref Entry entry) => 0;

    public bool Next(out Entry entry, out int error)
    {
        entry = default;
        error = 0;
        if (_remaining == 0)
        {
            if (_done)
            {
                return false;
            }
            var count = Fill(out error);
            if (error != 0)
            {
                _done = true;
                return true;
            }
            if (count == 0)
            {
                _done = true;
                return false;
            }
            _remaining = count;
            _offset = 0;
        }
        _remaining--;
        try
        {
            _offset += Record(out entry, out error);
        }
        catch (ArgumentOutOfRangeException)
        {
            // The kernel returned a malformed record: every read is
            // bounds-checked, and a bad length ends the listing.
            _remaining = 0;
            _done = true;
            error = Native.EIO;
        }
        return true;
    }

    unsafe uint Fill(out int error)
    {
        var request = new Native.AttrList
        {
            BitmapCount = Native.ATTR_BIT_MAP_COUNT,
            CommonAttr = Native.COMMON_ATTRIBUTES,
            FileAttr = Native.FILE_ATTRIBUTES,
        };
        fixed (byte* buffer = _buffer)
        {
            while (true)
            {
                var count = Native.getattrlistbulk(_fd, ref request, buffer, BufferBytes, 0);
                if (count >= 0)
                {
                    error = 0;
                    return (uint)count;
                }
                error = Marshal.GetLastPInvokeError();
                if (error != Native.EINTR)
                {
                    return 0;
                }
            }
        }
    }

    /// Reads the entry at `_offset`; returns the record's length.
    /// Layout per getattrlist(2): a u32 length, the returned attribute_set_t,
    /// ATTR_CMN_ERROR when set, then every other attribute in bit order.
    int Record(out Entry entry, out int error)
    {
        ReadOnlySpan<byte> bytes = _buffer;
        var length = (int)MemoryMarshal.Read<uint>(bytes[_offset..]);
        var cursor = new Cursor(bytes.Slice(_offset, length), 4);
        var common = cursor.U32();
        cursor.At += 8;
        var file = cursor.U32();
        cursor.At += 4;
        var recordError = (common & Native.ATTR_CMN_ERROR) == 0 ? 0u : cursor.U32();
        var name = cursor.Name();
        entry = default;
        if (recordError != 0)
        {
            error = (int)recordError;
            return length;
        }
        error = 0;
        if ((common & Native.MEASURED) == Native.MEASURED && cursor.Entry(file, name, out entry))
        {
            return length;
        }
        error = StatAt(name, out entry);
        return length;
    }

    /// An entry the file system described only in part (or a directory),
    /// read the ordinary way, relative to the directory already open.
    unsafe int StatAt(ReadOnlySpan<byte> nameWithNul, out Entry entry)
    {
        fixed (byte* path = nameWithNul)
        {
            if (Native.fstatat(_fd, path, out var stat, Native.AT_SYMLINK_NOFOLLOW) != 0)
            {
                entry = default;
                return Marshal.GetLastPInvokeError();
            }
            entry = FromStat(nameWithNul[..^1], stat);
            return 0;
        }
    }

    static Entry FromStat(ReadOnlySpan<byte> name, scoped in Native.Stat stat) => new()
    {
        Name = name,
        Kind = (stat.Mode & FileFacts.TypeMask) switch
        {
            FileFacts.TypeDirectory => EntryKind.Directory,
            FileFacts.TypeLink => EntryKind.Link,
            FileFacts.TypeRegular => EntryKind.File,
            _ => EntryKind.Other,
        },
        Apparent = (ulong)Math.Max(stat.Size, 0),
        Allocated = Entry.SaturatingMul512((ulong)Math.Max(stat.Blocks, 0)),
        Modified = stat.MtimeSec,
        Device = (ulong)(long)stat.Dev,
        Inode = stat.Ino,
        Links = stat.Nlink,
        Flags = stat.Flags,
    };

    ref struct Cursor(ReadOnlySpan<byte> record, int at)
    {
        readonly ReadOnlySpan<byte> _record = record;
        public int At = at;

        public uint U32()
        {
            var value = MemoryMarshal.Read<uint>(_record.Slice(At, 4));
            At += 4;
            return value;
        }

        public ulong U64()
        {
            var value = MemoryMarshal.Read<ulong>(_record.Slice(At, 8));
            At += 8;
            return value;
        }

        /// An attrreference_t: an offset from itself and a length that counts
        /// the terminating NUL. Returned with the NUL, for fstatat.
        public ReadOnlySpan<byte> Name()
        {
            var reference = At;
            var offset = (int)U32();
            var length = (int)U32();
            if (length < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }
            return _record.Slice(reference + offset, length);
        }

        /// The attributes after the name; false for a directory or a file
        /// whose sizes the file system left out, which are stated instead.
        public bool Entry(uint file, ReadOnlySpan<byte> nameWithNul, out Entry entry)
        {
            entry = default;
            var device = (int)U32();
            var kind = U32() switch
            {
                Native.VDIR => EntryKind.Directory,
                Native.VLNK => EntryKind.Link,
                Native.VREG => EntryKind.File,
                _ => EntryKind.Other,
            };
            var modified = (long)U64();
            At += 8;
            var flags = U32();
            var inode = U64();
            // A mount point is listed with the attributes of the directory it
            // covers, so directories are stated.
            if (kind == EntryKind.Directory || (file & Native.FILE_ATTRIBUTES) != Native.FILE_ATTRIBUTES)
            {
                return false;
            }
            entry = new Entry
            {
                Name = nameWithNul[..^1],
                Kind = kind,
                Links = U32(),
                Allocated = U64(),
                Apparent = U64(),
                Modified = modified,
                Device = (ulong)(long)device,
                Inode = inode,
                Flags = flags,
            };
            return true;
        }
    }
}

/// libc, through source-generated (AOT-safe) P/Invoke. macOS half; Linux.cs
/// holds the other. Imports bind lazily, so neither platform loads the other's.
static unsafe partial class Native
{
    public const int O_RDONLY = 0, O_DIRECTORY = 0x0010_0000, O_CLOEXEC = 0x0100_0000;
    public const int AT_SYMLINK_NOFOLLOW = 0x20;
    public const int EINTR = 4, EIO = 5, ENOENT = 2;

    public const ushort ATTR_BIT_MAP_COUNT = 5;
    public const uint ATTR_CMN_NAME = 0x0000_0001;
    public const uint ATTR_CMN_DEVID = 0x0000_0002;
    public const uint ATTR_CMN_OBJTYPE = 0x0000_0008;
    public const uint ATTR_CMN_MODTIME = 0x0000_0400;
    public const uint ATTR_CMN_FLAGS = 0x0004_0000;
    public const uint ATTR_CMN_FILEID = 0x0200_0000;
    public const uint ATTR_CMN_ERROR = 0x2000_0000;
    public const uint ATTR_CMN_RETURNED_ATTRS = 0x8000_0000;
    public const uint ATTR_FILE_LINKCOUNT = 0x0000_0001;
    public const uint ATTR_FILE_ALLOCSIZE = 0x0000_0004;
    public const uint ATTR_FILE_DATALENGTH = 0x0000_0200;
    public const uint VREG = 1, VDIR = 2, VLNK = 5;
    public const uint SF_DATALESS = 0x4000_0000;

    public const uint COMMON_ATTRIBUTES = ATTR_CMN_RETURNED_ATTRS | ATTR_CMN_NAME | ATTR_CMN_DEVID
        | ATTR_CMN_OBJTYPE | ATTR_CMN_MODTIME | ATTR_CMN_FLAGS | ATTR_CMN_FILEID | ATTR_CMN_ERROR;
    public const uint FILE_ATTRIBUTES = ATTR_FILE_LINKCOUNT | ATTR_FILE_ALLOCSIZE | ATTR_FILE_DATALENGTH;
    public const uint MEASURED = ATTR_CMN_DEVID | ATTR_CMN_OBJTYPE | ATTR_CMN_MODTIME | ATTR_CMN_FLAGS | ATTR_CMN_FILEID;

    [StructLayout(LayoutKind.Sequential)]
    public struct AttrList
    {
        public ushort BitmapCount;
        public ushort Reserved;
        public uint CommonAttr;
        public uint VolAttr;
        public uint DirAttr;
        public uint FileAttr;
        public uint ForkAttr;
    }

    /// `struct stat` on Darwin arm64 (64-bit inodes), the fields read here.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    public struct Stat
    {
        [FieldOffset(0)] public int Dev;
        [FieldOffset(4)] public ushort Mode;
        [FieldOffset(6)] public ushort Nlink;
        [FieldOffset(8)] public ulong Ino;
        [FieldOffset(48)] public long MtimeSec;
        [FieldOffset(56)] public long MtimeNsec;
        [FieldOffset(96)] public long Size;
        [FieldOffset(104)] public long Blocks;
        [FieldOffset(116)] public uint Flags;
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int open(string path, int flags);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int getattrlistbulk(int dirfd, ref AttrList attrList, byte* attrBuf, nuint attrBufSize, ulong options);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int fstatat(int fd, byte* path, out Stat buf, int flag);

    [LibraryImport("libc", EntryPoint = "stat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int stat(string path, out Stat buf);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int lstat(string path, out Stat buf);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint realpath(string path, nint resolved);

    [LibraryImport("libc")]
    public static partial void free(nint pointer);

    /// stat(2) or lstat(2) of a full path.
    public static int StatPath(string path, bool follow, out FileFacts facts)
    {
        if ((follow ? stat(path, out var st) : lstat(path, out st)) != 0)
        {
            facts = default;
            return Marshal.GetLastPInvokeError();
        }
        facts = new FileFacts(st.Mode, (ulong)Math.Max(st.Size, 0), (ulong)Math.Max(st.Blocks, 0),
            (ulong)(long)st.Dev, st.Ino, st.Nlink, st.MtimeSec < 0 ? 0 : st.MtimeSec);
        return 0;
    }

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
