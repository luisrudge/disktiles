// The final path behind a Windows path: what Windows itself opens, every
// junction, symbolic link, subst drive and mounted volume followed. The
// guards judge it as well as the spelling, since C:\Windows has other names
// (\\localhost\C$\Windows, a subst drive, a junction). And whether a path
// is in a cloud sync root. With RecycleBin.cs, the only Windows calls
// disktiles makes: .NET has no final-path or Cloud Files API.
using System.Runtime.InteropServices;

namespace Disktiles.Core;

static unsafe partial class Win32
{
    const uint ReadAttributes = 0x80; // FILE_READ_ATTRIBUTES
    const uint ShareAll = 0x7;
    const uint OpenExisting = 3;
    const uint BackupSemantics = 0x02000000; // opens folders too
    const uint DeleteAccess = 0x10000; // DELETE
    const uint OpenReparsePoint = 0x00200000; // a link as itself
    const int FileDispositionInfoEx = 21;
    // FILE_DISPOSITION_FLAG_DELETE, _POSIX_SEMANTICS, _IGNORE_READONLY_ATTRIBUTE.
    const uint DispositionDelete = 0x1, DispositionPosix = 0x2, DispositionIgnoreReadOnly = 0x10;

    public const int FileNotFound = 2, PathNotFound = 3;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static partial uint GetFinalPathNameByHandle(nint file, char* path, uint length, uint flags);

    [LibraryImport("cldapi.dll", EntryPoint = "CfGetSyncRootInfoByPath", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CfGetSyncRootInfoByPath(string path, int infoClass, void* info, uint length, out uint returned);

    [LibraryImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(nint file, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeInformationByHandle(nint file, char* name, uint nameSize, uint* serial,
        uint* maxComponent, uint* flags, char* fileSystem, uint fileSystemSize);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    /// Delete the file at `path` (a link as itself), read-only or not,
    /// without clearing the flag: attributes belong to the file, so clearing
    /// them clears them on every hard link of it, outside the target too.
    /// False where the file system cannot (FAT, Windows before 10 1809): it
    /// has no hard links, and the caller clears the flag there.
    public static bool DeleteIgnoringReadOnly(string path)
    {
        var file = CreateFile(Space.Literal(path), DeleteAccess, ShareAll, 0, OpenExisting, OpenReparsePoint, 0);
        if (file == -1)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException($"could not open {path} to delete it ({Errors.Text(error)})");
        }
        try
        {
            var flags = DispositionDelete | DispositionPosix | DispositionIgnoreReadOnly;
            if (SetFileInformationByHandle(file, FileDispositionInfoEx, &flags, sizeof(uint)))
            {
                return true;
            }
            // ERROR_INVALID_FUNCTION, _NOT_SUPPORTED, _INVALID_PARAMETER: no
            // such deletion here.
            var error = Marshal.GetLastPInvokeError();
            return error is 1 or 50 or 87 ? false : throw new IOException($"could not delete {path} ({Errors.Text(error)})");
        }
        finally
        {
            CloseHandle(file);
        }
    }

    /// Whether `path` is in, or is, a folder a cloud provider syncs through
    /// the Cloud Files API (OneDrive, SharePoint, Dropbox, iCloud). Windows
    /// hides the reparse points that mark them from .NET, so only this tells.
    public static bool InSyncRoot(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        // CF_SYNC_ROOT_BASIC_INFO: the root's file id, 8 bytes.
        long info;
        try
        {
            return CfGetSyncRootInfoByPath(Space.Literal(path), 0, &info, sizeof(long), out _) == 0;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            // Before Windows 10 1709: no Cloud Files, so no sync roots of it.
            return false;
        }
    }

    /// `path`'s final path, in plain spelling (`C:\x`, `\\server\share\x`);
    /// null with the Win32 error when it cannot be opened.
    public static string? FinalPath(string path, out int error)
    {
        error = 0;
        if (!OperatingSystem.IsWindows())
        {
            return path;
        }
        var file = Open(path, out error);
        if (file == -1)
        {
            return null;
        }
        try
        {
            return Final(file, 0, out error) is { } final ? Space.Normalize(final) : null;
        }
        finally
        {
            CloseHandle(file);
        }
    }

    /// The disk `path` is on (its volume GUID and serial number: a card
    /// swapped in the same reader keeps the GUID, not the serial), and the
    /// final path by that GUID (`\\?\Volume{…}\x`), which shows a re-pointed
    /// subst drive too. A share or a drive without a GUID is spelled by its
    /// NT device (`\Device\Mup\…`), so a letter moving from a share to a
    /// disk shows as well. Null only when it cannot be opened.
    public static (string Disk, string Real)? Identify(string path)
    {
        var file = Open(path, out _);
        if (file == -1)
        {
            return null;
        }
        try
        {
            // VOLUME_NAME_GUID, else VOLUME_NAME_NT.
            if ((Final(file, 1, out _) ?? Final(file, 2, out _)) is not { } real)
            {
                return null;
            }
            var volume = real.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)
                ? real[..(real.IndexOf('}') + 1)]
                : string.Join('\\', real.Split('\\').Take(3));
            uint serial;
            var known = GetVolumeInformationByHandle(file, null, 0, &serial, null, null, null, 0);
            return ($"{volume} {(known ? serial.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) : "-")}", real);
        }
        finally
        {
            CloseHandle(file);
        }
    }

    static nint Open(string path, out int error)
    {
        var file = CreateFile(Space.Literal(path), ReadAttributes, ShareAll, 0, OpenExisting, BackupSemantics, 0);
        error = file == -1 ? Marshal.GetLastPInvokeError() : 0;
        return file;
    }

    /// `flags`: VOLUME_NAME_DOS (0) or VOLUME_NAME_GUID (1).
    static string? Final(nint file, uint flags, out int error)
    {
        error = 0;
        var buffer = new char[512];
        while (true)
        {
            uint length;
            fixed (char* chars = buffer)
            {
                length = GetFinalPathNameByHandle(file, chars, (uint)buffer.Length, flags);
            }
            if (length == 0)
            {
                error = Marshal.GetLastPInvokeError();
                return null;
            }
            if (length < buffer.Length)
            {
                return new string(buffer, 0, (int)length);
            }
            buffer = new char[length + 1];
        }
    }
}
