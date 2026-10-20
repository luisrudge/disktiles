// A picture for QML's DrawView (window apps): rectangles, outlines, hatching, lines, areas and text,
// drawn natively by the host's scene graph. Build one, then Bridge.Show("name", drawing);
// QML shows it with `DrawView { source: "name"; view: [scale, x, y] }`. Coordinates are in
// the DrawView's pixels for the view given here; when QML's `view` later differs (zoom,
// pan), the host moves and scales what it has until you send a fresh drawing.
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Omasharp;

public sealed class Drawing
{
    readonly ArrayBufferWriter<byte> _buf = new(1024);
    int _count;

    /// <summary>Width of one monospace character per pixel of font size (for laying out labels).
    /// Set by the host; 0.6 outside the app (unit tests).</summary>
    public static double MonoAdvance { get; internal set; } = 0.6;

    public Drawing(double viewScale = 1, double viewX = 0, double viewY = 0)
    {
        _buf.Write("ODL1"u8);
        D(viewScale);
        D(viewX);
        D(viewY);
        I(0); // count, patched in ToBytes
    }

    public int Count => _count;

    public void Rect(double x, double y, double w, double h, Color color)
    {
        if (w <= 0 || h <= 0 || color.A == 0) return;
        B(1); F(x); F(y); F(w); F(h); U(color.Argb); _count++;
    }

    /// <summary>An outline `width` thick, inside the rectangle.</summary>
    public void Frame(double x, double y, double w, double h, double width, Color color)
    {
        if (w <= 0 || h <= 0 || color.A == 0) return;
        B(2); F(x); F(y); F(w); F(h); F(width); U(color.Argb); _count++;
    }

    /// <summary>Diagonal stripes every `spacing` pixels, clipped to the rectangle.</summary>
    public void Hatch(double x, double y, double w, double h, double spacing, Color color, double thickness = 1)
    {
        if (w <= 0 || h <= 0 || color.A == 0) return;
        B(3); F(x); F(y); F(w); F(h); F(spacing); F(thickness); U(color.Argb); _count++;
    }

    /// <summary>A straight line `width` thick from (x1, y1) to (x2, y2), with square-cut ends.</summary>
    public void Line(double x1, double y1, double x2, double y2, double width, Color color) =>
        Polyline([(x1, y1), (x2, y2)], width, color);

    /// <summary>Connected lines `width` thick through the points (a line chart). Corners are mitred,
    /// or cut flat where they're sharper than 60°, so a steep chart gets no spikes.</summary>
    public void Polyline(ReadOnlySpan<(double X, double Y)> points, double width, Color color)
    {
        if (points.Length < 2 || width <= 0 || color.A == 0) return;
        B(5); F(width); U(color.Argb); Points(points); _count++;
    }

    /// <summary>The area between the line through the points and the level `baselineY` (an area
    /// chart: draw it first, then a Polyline through the same points). Points go left to right.</summary>
    public void Area(ReadOnlySpan<(double X, double Y)> points, double baselineY, Color color)
    {
        if (points.Length < 2 || color.A == 0) return;
        B(6); F(baselineY); U(color.Argb); Points(points); _count++;
    }

    /// <summary>One line of text in the monospace (Omarchy) font, vertically centred on `yCenter`,
    /// cut to `maxWidth` pixels when that's &gt; 0. Width of n characters: n * MonoAdvance * pixelSize.</summary>
    public void Text(double x, double yCenter, string text, double pixelSize, Color color, bool bold = false, double maxWidth = 0)
    {
        if (string.IsNullOrEmpty(text) || color.A == 0) return;
        B(4); F(x); F(yCenter); F(pixelSize); F(maxWidth); U(color.Argb);
        B(bold ? (byte)1 : (byte)0);
        var bytes = Encoding.UTF8.GetBytes(text);
        I(bytes.Length);
        _buf.Write(bytes);
        _count++;
    }

    // The buffer the host reads: little-endian, packed (see host/drawview.cpp).
    void Points(ReadOnlySpan<(double X, double Y)> points)
    {
        I(points.Length);
        foreach (var (x, y) in points) { F(x); F(y); }
    }
    void B(byte v) { _buf.GetSpan(1)[0] = v; _buf.Advance(1); }
    void I(int v) { BinaryPrimitives.WriteInt32LittleEndian(_buf.GetSpan(4), v); _buf.Advance(4); }
    void U(uint v) { BinaryPrimitives.WriteUInt32LittleEndian(_buf.GetSpan(4), v); _buf.Advance(4); }
    void F(double v) { BinaryPrimitives.WriteSingleLittleEndian(_buf.GetSpan(4), (float)v); _buf.Advance(4); }
    void D(double v) { BinaryPrimitives.WriteDoubleLittleEndian(_buf.GetSpan(8), v); _buf.Advance(8); }

    internal byte[] ToBytes()
    {
        var bytes = _buf.WrittenSpan.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4 + 3 * 8, 4), _count);
        return bytes;
    }
}

/// <summary>A colour for Drawing: `Color.Parse("#7aa2f7")` or `"#cc7aa2f7"` (alpha first, as QML).</summary>
public readonly record struct Color(uint Argb)
{
    public byte A => (byte)(Argb >> 24);

    public static Color Parse(string css)
    {
        var hex = css.AsSpan().TrimStart('#');
        if (hex.Length == 3) hex = new string([hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]]);
        var v = uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new Color(hex.Length == 6 ? 0xFF000000 | v : v);
    }

    public Color WithAlpha(double alpha) => new(((uint)Math.Clamp(alpha * 255, 0, 255) << 24) | (Argb & 0xFFFFFF));
}
