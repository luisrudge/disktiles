# Custom drawing: charts, maps, treemaps (window apps)

Don't paint big pictures with QML's `Canvas` (JavaScript, a full-window image repainted on every change) or with hundreds of `Rectangle`s fed from a JSON list: both are slow. Build a `Drawing` in C# and show it with `DrawView`. The host turns it into native scene-graph nodes (one draw call for all shapes), so it updates fast and stays smooth:

```csharp
var d = new Drawing(viewScale, viewX, viewY);   // the view (zoom/pan) these coordinates are for
d.Rect(x, y, w, h, Color.Parse("#7aa2f7"));
d.Frame(x, y, w, h, 2, Color.Parse("#f7768e"));          // outline inside the rectangle
d.Hatch(x, y, w, h, 6, Color.Parse("#40ffffff"));        // diagonal stripes
d.Line(x1, y1, x2, y2, 1, Color.Parse("#565f89"));       // one segment, 1 px thick
d.Polyline(points, 2, Color.Parse("#7aa2f7"));           // a line chart: (x, y) points
d.Area(points, baselineY, Color.Parse("#407aa2f7"));     // filled from those points down to baselineY
d.Text(x, yCenter, "label", 12, Color.Parse("#c0caf5"), bold: true, maxWidth: w - 8);
Bridge.Show("chart", d);                                  // any thread; latest drawing wins
```
```qml
import Omasharp
DrawView { anchors.fill: parent; clip: true; source: "chart"; view: backend.view }   // view: [scale, x, y]
```

- Text is the monospace (Omarchy) font: `n` characters are `n * Drawing.MonoAdvance * pixelSize` wide, so C# can lay out labels exactly.
- **Zoom and pan:** bind `view`; the host moves and scales the last drawing immediately (no repaint), then send a fresh `Drawing` for the new view once it settles (e.g. 150 ms after the last change) to get sharp labels.
- Keep small, frequently-changing things (hover outline, selection ring, tooltips) as normal QML items on top: they don't redraw the picture.
- Theme colors for a drawing: `Theme.Color("accent")`; redraw on `Theme.Changed` ([look.md](look.md)).
- Under Qt's software renderer (`check.sh --offscreen`, `QT_QUICK_BACKEND=software`, a machine without a GPU) `DrawView` paints its shapes into an image: the same picture, drawn more slowly. Offscreen checks and `grabToImage` snapshots see the shapes, not only the text.
- `Bridge.Show` and `DrawView` are for window apps; in panels, use QML items.

## Line and area charts

`Polyline` draws connected lines through points; `Area` fills between those points and a baseline. Draw the area first, then the line through the same points. `Line(x1, y1, x2, y2, width, color)` is a single segment (a grid line, a threshold). Corners are mitred, and cut flat where they're sharper than 60°, so steep samples draw neither gaps nor spikes. Points are `(x, y)` tuples in a span: a collection expression, an array or a `stackalloc`, nothing allocated per point.

A CPU history graph, one sample per second, newest on the right:

```csharp
readonly Queue<double> _cpu = new();          // 0..100, at most 60 samples
double _w = 300, _h = 80;                     // the DrawView's size: QML calls Resize

public void Resize(double w, double h) { (_w, _h) = (w, h); DrawCpu(); }

void DrawCpu()                                // after each new sample, and on Theme.Changed
{
    if (_cpu.Count < 2) return;
    Span<(double X, double Y)> points = stackalloc (double X, double Y)[_cpu.Count];
    var i = 0;
    foreach (var v in _cpu)
        points[i] = (_w * (60 - _cpu.Count + i++) / 59, _h - _h * v / 100);   // 60 slots, newest at the right; 100 % at the top
    var accent = Theme.Color("accent");
    var d = new Drawing();
    d.Line(0, _h / 2, _w, _h / 2, 1, Theme.Color("color8").WithAlpha(0.5));   // 50 % guide
    d.Area(points, _h, accent.WithAlpha(0.25));
    d.Polyline(points, 2, accent);
    Bridge.Show("cpu", d);
}
```
```qml
DrawView {
  width: parent.width; height: 80
  source: "cpu"; view: [1, 0, 0]
  onWidthChanged: backend.resize(width, height)
  onHeightChanged: backend.resize(width, height)
}
```

- Shapes have hard edges (no antialiasing): fine for rectangles, slightly stepped on slanted lines. For smooth lines, have the scene graph multisample the view: `layer.enabled: true; layer.samples: 4` on the `DrawView`.
