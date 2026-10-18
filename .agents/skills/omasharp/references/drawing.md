# Custom drawing: charts, maps, treemaps (window apps)

Don't paint big pictures with QML's `Canvas` (JavaScript, a full-window image repainted on every change) or with hundreds of `Rectangle`s fed from a JSON list: both are slow. Build a `Drawing` in C# and show it with `DrawView`. The host turns it into native scene-graph nodes (one draw call for all shapes), so it updates fast and stays smooth:

```csharp
var d = new Drawing(viewScale, viewX, viewY);   // the view (zoom/pan) these coordinates are for
d.Rect(x, y, w, h, Color.Parse("#7aa2f7"));
d.Frame(x, y, w, h, 2, Color.Parse("#f7768e"));          // outline inside the rectangle
d.Hatch(x, y, w, h, 6, Color.Parse("#40ffffff"));        // diagonal stripes
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
- `Bridge.Show` and `DrawView` are for window apps; in panels, use QML items.
