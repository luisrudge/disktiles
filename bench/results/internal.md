
### Startup, /usr/share/doc (14.8k entries): ms since the process started, median (min–max); GTK n=35, QML n=35

| phase | GTK | QML | QML − GTK |
|---|---|---|---|
| Main entered (first managed code) | 10 (10–20) | 30 (20–40) | 20 |
| toolkit init done | 95 (92–197) | 64 (53–101) | -32 |
| window shown | 115 (113–252) | 170 (157–253) | 55 |
| scan start | 118 (116–260) | 65 (54–102) | -53 |
| scan end | 258 (252–495) | 173 (160–256) | -86 |
| first frame presented | 257 (251–490) | 199 (186–286) | -58 |
| first treemap presented | 391 (381–694) | 258 (245–363) | -133 |
| scan duration (ms) | 140 (136–235) | 108 (105–153) | -32 |
| layouts / paints until the first treemap | 2 / 2 | 1 / 1 |  |
| CPU at first treemap (ms) | 440 (420–650) | 300 (290–390) | -140 |
| layout (Core, all passes until first treemap) ms | 2 (2–4) | 2 (1–4) | -0 |
| view-model build ms | – | 4 (3–5) | – |
| transfer: encode + host parse + QML bindings ms | – | 2 (2–3) | – |
| paint (GTK: Cairo to texture; port: C# Drawing) ms | 73 (71–111) | 4 (3–6) | -69 |

### Startup, /usr/share (120k entries): ms since the process started, median (min–max); GTK n=5, QML n=5

| phase | GTK | QML | QML − GTK |
|---|---|---|---|
| Main entered (first managed code) | 10 (10–10) | 30 (20–30) | 20 |
| toolkit init done | 94 (93–100) | 63 (53–63) | -31 |
| window shown | 114 (113–120) | 265 (225–270) | 151 |
| scan start | 117 (116–123) | 65 (54–65) | -53 |
| scan end | 353 (330–360) | 270 (267–300) | -83 |
| first frame presented | 352 (329–359) | 295 (266–300) | -57 |
| first treemap presented | 482 (462–494) | 358 (352–360) | -124 |
| scan duration (ms) | 231 (214–239) | 206 (203–246) | -26 |
| layouts / paints until the first treemap | 2 / 2 | 1 / 1 |  |
| CPU at first treemap (ms) | 990 (990–1,010) | 880 (870–910) | -110 |
| layout (Core, all passes until first treemap) ms | 4 (4–4) | 4 (3–5) | -0 |
| view-model build ms | – | 6 (4–6) | – |
| transfer: encode + host parse + QML bindings ms | – | 2 (2–4) | – |
| paint (GTK: Cairo to texture; port: C# Drawing) ms | 71 (68–72) | 7 (6–7) | -64 |

### One step, NAV: Return (into the largest directory; ~410 tiles): ms, median over all steps of 5 runs (GTK n=50, QML n=50)

|  | GTK | QML | p95 GTK / QML |
|---|---|---|---|
| tiles laid out | 98.0 | 98.0 | 98.0 / 98.0 |
| layout (Core) | 0.1 | 0.2 | 0.3 / 0.5 |
| view-model build | – | 0.3 | – |
|   transfer, of it: C# JSON encode (detail runs, n=1 run) | – | – | – |
| transfer (encode, host parse, QML bindings) | – | 6.1 | – |
| paint (all paints in the step) | 35.6 | 0.4 | 62.7 / 1.5 |
| paints per step | 1.0 | 1.0 | 1.0 / 1.0 |
| key → first frame presented after a paint | 58.1 | 13.3 | 81.2 / 25.8 |
| key → last frame presented for this step | 58.1 | 13.3 | 81.2 / 25.8 |

### One step, NAV: BackSpace (out to the root; ~1,150 tiles): ms, median over all steps of 5 runs (GTK n=45, QML n=45)

|  | GTK | QML | p95 GTK / QML |
|---|---|---|---|
| tiles laid out | 1,285.0 | 1,311.0 | 1,285.0 / 1,311.0 |
| layout (Core) | 1.0 | 2.6 | 2.7 / 4.1 |
| view-model build | – | 2.0 | – |
|   transfer, of it: C# JSON encode (detail runs, n=1 run) | – | – | – |
| transfer (encode, host parse, QML bindings) | – | 4.2 | – |
| paint (all paints in the step) | 39.1 | 2.5 | 67.1 / 6.6 |
| paints per step | 1.0 | 1.0 | 1.0 / 1.0 |
| key → first frame presented after a paint | 53.1 | 16.4 | 84.8 / 32.6 |
| key → last frame presented for this step | 53.1 | 16.4 | 84.8 / 32.6 |

### One step, MODES: t: ms, median over all steps of 5 runs (GTK n=95, QML n=95)

|  | GTK | QML | p95 GTK / QML |
|---|---|---|---|
| tiles laid out | 1,285.0 | 1,311.0 | 1,285.0 / 1,311.0 |
| layout (Core) | 2.0 | 1.2 | 3.8 / 3.4 |
| view-model build | – | 2.0 | – |
|   transfer, of it: C# JSON encode (detail runs, n=1 run) | – | – | – |
| transfer (encode, host parse, QML bindings) | – | 2.0 | – |
| paint (all paints in the step) | 66.7 | 2.4 | 98.1 / 6.9 |
| paints per step | 2.0 | 1.0 | 2.0 / 1.0 |
| key → first frame presented after a paint | 84.1 | 15.6 | 117.2 / 31.0 |
| key → last frame presented for this step | 84.1 | 15.6 | 117.2 / 31.0 |

### One step, DEPTH: [ and ]: ms, median over all steps of 5 runs (GTK n=95, QML n=95)

|  | GTK | QML | p95 GTK / QML |
|---|---|---|---|
| tiles laid out | 1,451.0 | 1,481.0 | 1,451.0 / 1,481.0 |
| layout (Core) | 1.2 | 2.4 | 2.7 / 4.0 |
| view-model build | – | 4.2 | – |
|   transfer, of it: C# JSON encode (detail runs, n=1 run) | – | – | – |
| transfer (encode, host parse, QML bindings) | – | 0.7 | – |
| paint (all paints in the step) | 40.3 | 4.7 | 72.4 / 10.8 |
| paints per step | 1.0 | 1.0 | 1.0 / 1.0 |
| key → first frame presented after a paint | 51.7 | 11.9 | 82.5 / 23.5 |
| key → last frame presented for this step | 51.7 | 11.9 | 82.5 / 23.5 |

### One step, FILTER: one key: ms, median over all steps of 5 runs (GTK n=175, QML n=175)

|  | GTK | QML | p95 GTK / QML |
|---|---|---|---|
| tiles laid out | 1,285.0 | 1,311.0 | 1,285.0 / 1,311.0 |
| layout (Core) | 1.1 | 2.4 | 2.4 / 6.2 |
| view-model build | – | 5.1 | – |
|   transfer, of it: C# JSON encode (detail runs, n=1 run) | – | – | – |
| transfer (encode, host parse, QML bindings) | – | 2.6 | – |
| paint (all paints in the step) | 41.3 | 5.7 | 65.6 / 12.1 |
| paints per step | 1.0 | 1.0 | 1.0 / 1.0 |
| key → first frame presented after a paint | 53.7 | 18.3 | 80.0 / 30.5 |
| key → last frame presented for this step | 53.7 | 18.3 | 80.0 / 30.5 |

### Workloads on /usr/share/doc, 8 s each (IDLE 1 s), 5 runs: frames from each toolkit's own clock

Intervals between presented frames within animation bursts (gaps ≥250 ms between keys are idle, not counted). GTK: GdkFrameClock after-paint times; frame work = before-paint → after-paint on the GTK thread. QML: the render thread's frames from QSG_RENDER_TIMING (1 ms resolution); frame work = sync+render there (swap left out: it is mostly the vsync wait); GUI = polish (the Canvas paints here) + animations on the GUI thread. All ms.

| workload | app | frames | interval median | p95 | max | missed vsync (>20 ms) | >16.7 ms | frame work median | p95 | QML GUI polish+anim med / p95 | CPU ms, median (range) |
|---|---|---|---|---|---|---|---|---|---|---|---|
| SWEEP | GTK | 470 | 16.7 | 27.1 | 48.9 | 76 | 227 | 4.1 | 14.9 | – | 2,740 (2,690–2,790) |
|  | QML | 483 | 17.0 | 18.0 | 23.0 | 0 | 309 | 1.0 | 2.0 | 1.0 / 4.0 | 4,120 (3,990–4,280) |
| ZOOMSWEEP | GTK | 464 | 16.7 | 29.4 | 120.3 | 74 | 215 | 0.1 | 14.4 | – | 1,520 (1,300–1,570) |
|  | QML | 481 | 17.0 | 18.0 | 25.0 | 1 | 300 | 0.0 | 1.0 | 0.0 / 2.0 | 3,320 (2,750–3,420) |
| NAV | GTK | 291 | 16.6 | 131.4 | 247.2 | 18 | 135 | 2.3 | 8.7 | – | 1,840 (1,610–1,910) |
|  | QML | 244 | 17.0 | 18.0 | 248.0 | 10 | 127 | 0.0 | 1.0 | 0.0 / 0.0 | 1,560 (1,450–1,660) |
| MODES | GTK | 187 | 16.7 | 162.8 | 227.6 | 12 | 60 | 0.0 | 8.8 | – | 1,780 (1,690–1,960) |
|  | QML | 38 | 12.0 | 14.0 | 20.0 | 0 | 0 | 1.0 | 4.0 | 0.0 / 0.0 | 500 (490–790) |
| DEPTH | GTK | 25 | 7.8 | 10.1 | 10.7 | 0 | 0 | 8.1 | 10.7 | – | 1,140 (980–1,200) |
|  | QML | 38 | 12.0 | 16.0 | 18.0 | 0 | 0 | 1.0 | 4.0 | 0.0 / 0.0 | 410 (360–440) |
| FILTER | GTK | 78 | 86.7 | 195.2 | 243.3 | 66 | 66 | 2.3 | 10.3 | – | 2,120 (2,020–2,140) |
|  | QML | 100 | 16.0 | 191.0 | 223.0 | 41 | 48 | 0.0 | 5.0 | 0.0 / 0.0 | 1,100 (920–1,170) |
| IDLE | GTK | 0 | – | – | – | 0 | 0 | – | – | – | 0 (0–10) |
|  | QML | 0 | – | – | – | 0 | 0 | – | – | – | 20 (20–20) |
