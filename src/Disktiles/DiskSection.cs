// The disk under the scan, pinned at the panel's bottom: free, used and total.
using Disktiles.Core;
using static Disktiles.Look;
using static Disktiles.Sidebar;

namespace Disktiles;

sealed class DiskSection
{
    public readonly Gtk.Box Widget;
    /// The "Disk" row: Targets puts its Volumes button at the end.
    public readonly Gtk.Box Header;
    readonly Gtk.Label _device, _free, _freeUnit, _used, _total;
    readonly Gtk.ProgressBar _disk;

    public DiskSection(AppState state)
    {
        Widget = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        _device = Text("", "dim");
        var disk = Header = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        disk.Append(Caps("Disk"));
        _device.Hexpand = true;
        disk.Append(_device);
        Widget.Append(disk);
        _free = Text("", "big");
        _freeUnit = Text("", "unit");
        _freeUnit.Valign = Gtk.Align.End;
        _freeUnit.MarginBottom = 8;
        var free = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        free.Append(_free);
        free.Append(_freeUnit);
        Widget.Append(free);
        _disk = Gtk.ProgressBar.New();
        _disk.AddCssClass("disk");
        Widget.Append(_disk);
        _used = Text("", "dim");
        _used.Hexpand = true;
        _total = Text("", "dim");
        _total.Xalign = 1;
        var usage = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        usage.Append(_used);
        usage.Append(_total);
        Widget.Append(usage);
        state.TreeChanged += () => Update(state.Root);
    }

    /// The statvfs numbers: free is what an ordinary user can still write;
    /// used is the total less every free block.
    public void Update(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            var total = (ulong)drive.TotalSize;
            var used = total - (ulong)drive.TotalFreeSpace;
            var (number, unit) = Split(HumanBytes((ulong)drive.AvailableFreeSpace));
            _free.SetText(number);
            _freeUnit.SetText($"{unit} free");
            _disk.Fraction = total == 0 ? 0 : (double)used / total;
            _used.SetText($"{HumanBytes(used)} used");
            _total.SetText($"{HumanBytes(total)} total");
            _device.SetText(DeviceFor(root));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _free.SetText("?");
            _used.SetText(error.Message);
        }
    }

    /// The mount source of the longest mount point covering `path`.
    static string DeviceFor(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return "";
        }
        Mount? best = null;
        foreach (var mount in Space.ParseMounts(File.ReadAllText("/proc/self/mounts")))
        {
            if (Space.PathStartsWith(path, mount.Point) && (best is null || mount.Point.Length > best.Point.Length))
            {
                best = mount;
            }
        }
        return best?.Source ?? "";
    }
}
