// The lister for Windows (and any other OS without one of its own): .NET's
// own enumerator, which lists a buffer of entries per call with each entry's
// name, attributes, length and time. It reports neither the space a file
// occupies nor a file id, so sizes are apparent sizes and hardlinks are not
// deduplicated there.
using System.IO.Enumeration;
using System.Text;

namespace Disktiles.Core;

public ref struct ManagedDirReader : IDirReader
{
    Lister? _lister;

    public static int Open(string path, out ManagedDirReader reader)
    {
        reader = default;
        try
        {
            // Literal: `junk.` lists junk., not its sibling junk.
            var lister = new Lister(Space.Literal(path));
            if (lister.OpenError != 0)
            {
                lister.Dispose();
                return lister.OpenError;
            }
            // From here an error is a read error, even before the first entry.
            lister.Opened = true;
            reader._lister = lister;
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Errors.Of(error);
        }
    }

    public readonly bool Next(out Entry entry, out int error)
    {
        entry = default;
        error = 0;
        if (_lister is not { } lister)
        {
            return false;
        }
        if (!lister.MoveNext())
        {
            // A read that failed partway: reported once, then the end.
            (error, lister.ReadError) = (lister.ReadError, 0);
            return error != 0;
        }
        var directory = (lister.Attributes & FileAttributes.Directory) != 0;
        // A junction, a symbolic link or a mounted volume is a link, as on
        // Unix. Other reparse points (OneDrive's folders, cloud placeholders)
        // are ordinary folders and files to measure.
        entry.Kind = directory && lister.IsLink ? EntryKind.Link
            : directory ? EntryKind.Directory
            : EntryKind.File;
        entry.Name = lister.Name;
        entry.Apparent = directory ? 0 : (ulong)lister.Length;
        // An online-only file takes no disk space; a folder not yet fetched
        // is skipped, as macOS skips evicted ones, so listing it fetches nothing.
        entry.Allocated = Removal.IsPlaceholder(lister.Attributes) ? 0 : entry.Apparent;
        entry.Flags = directory && (lister.Attributes & Removal.RecallOnOpen) != 0 ? Native.SF_DATALESS : 0;
        entry.Modified = lister.Modified;
        entry.Links = 1;
        return true;
    }

    /// The listing carried everything there is.
    public readonly int Facts(ref Entry entry) => 0;

    public readonly void Dispose() => _lister?.Dispose();

    sealed class Lister(string path) : FileSystemEnumerator<byte>(path, Options)
    {
        static readonly EnumerationOptions Options = new()
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        byte[] _name = new byte[512];
        int _nameLength;
        public FileAttributes Attributes;
        public long Length;
        public long Modified;
        public bool IsLink;
        /// Set when the directory itself could not be opened.
        public int OpenError;
        /// Set when reading failed after the open.
        public int ReadError;
        /// Set by Open once the constructor, which opens the directory, returned.
        public bool Opened;

        public ReadOnlySpan<byte> Name => _name.AsSpan(0, _nameLength);

        protected override byte TransformEntry(ref FileSystemEntry entry)
        {
            var name = entry.FileName;
            if (Encoding.UTF8.GetMaxByteCount(name.Length) > _name.Length)
            {
                _name = new byte[Encoding.UTF8.GetMaxByteCount(name.Length)];
            }
            _nameLength = Encoding.UTF8.GetBytes(name, _name);
            Attributes = entry.Attributes;
            Length = entry.Length;
            try
            {
                Modified = entry.LastWriteTimeUtc.ToUnixTimeSeconds();
            }
            catch (ArgumentOutOfRangeException)
            {
                // Past year 9999: NTFS keeps such a time, .NET cannot hold it.
                Modified = 0;
            }
            // Only for reparse folders, which are few: what it leads to.
            IsLink = (Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == (FileAttributes.Directory | FileAttributes.ReparsePoint)
                && Space.LinkTarget(entry.ToFullPath()) is not null;
            return 0;
        }

        // Opening the directory reports here, in the constructor: kept for
        // Open. A later error ends the listing, and Next reports it.
        protected override bool ContinueOnError(int error)
        {
            if (Opened)
            {
                ReadError = error;
            }
            else
            {
                OpenError = error;
            }
            return true;
        }
    }
}
