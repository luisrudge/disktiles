// The Recycle Bin, through the shell's own file operation (with Win32.cs,
// the Windows calls disktiles makes). .NET has no recycle of its own, and the
// Microsoft.VisualBasic wrapper's quiet mode deletes outright, without
// asking, whatever cannot be recycled (too big, or on a network drive).
// FOF_WANTNUKEWARNING asks first in exactly that case, and only then.
// FOF_NO_CONNECTED_ELEMENTS keeps the shell from taking `page_files` along
// with a marked `page.htm`, or the other way round.
// ponytail: when the user accepts that warning, the shell's own delete
// clears ReadOnly on files it removes, and so on their hard links outside
// the target (an attribute, nothing deleted). Refusing instead would need
// IFileOperation's progress sink, a COM object this AOT build does not have.
using System.Runtime.InteropServices;

namespace Disktiles.Core;

static partial class RecycleBin
{
    const uint Delete = 3; // FO_DELETE
    const ushort Silent = 0x0004, NoConfirmation = 0x0010, AllowUndo = 0x0040, NoErrorUi = 0x0400,
        NoConnectedElements = 0x2000, WantNukeWarning = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    struct FileOperation
    {
        public nint Window;
        public uint Function;
        public nint From;
        public nint To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public nint NameMappings;
        public nint ProgressTitle;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static partial int SHFileOperation(ref FileOperation operation);

    /// Move `path` (absolute: the shell reads relative paths against a
    /// process-wide directory) to the Recycle Bin.
    public static void Recycle(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("the Recycle Bin is only on Windows");
        }
        if (!Path.IsPathFullyQualified(path))
        {
            throw new IOException($"{path} is not absolute, which the Recycle Bin cannot take safely");
        }
        // A list of names, each ended by a NUL, the list by one more.
        var from = Marshal.StringToHGlobalUni(path + '\0');
        try
        {
            var operation = new FileOperation
            {
                Function = Delete,
                From = from,
                Flags = (ushort)(Silent | NoConfirmation | AllowUndo | NoErrorUi | NoConnectedElements | WantNukeWarning),
            };
            var result = SHFileOperation(ref operation);
            if (operation.AnyOperationsAborted != 0)
            {
                throw new IOException("not removed: Windows could not recycle it, and deleting it permanently was declined");
            }
            if (result != 0)
            {
                throw new IOException($"the Recycle Bin refused it (shell error 0x{result:X})");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }
}
