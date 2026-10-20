# Benchmark

disktiles 0.2.0 (GTK, "GTK" below) against the Omasharp version ("port"), both native AOT release builds, on this machine (a 2015 MacBook Pro:
i5-5257U, 2 cores / 4 threads; Omarchy, kernel 7.2.8, GTK 4.24.1, Qt 6.12.0). Raw data and the
full tables: [results/](results/).

**Startup and latency** (`ibench.sh`, the earlier port's harness): builds with test hooks
(`-p:TestHooks=true`; the GTK one with the earlier port's instrumentation patch) log the same
`DISKTILES_PERF` events and run the same `DISKTILES_WORKLOAD` drivers. A private nested
Hyprland on a hidden special workspace, 3200×2000 at 2x, windows 1440×900. Warm cache, the two
apps alternating, 5 runs of each workload.

| `/usr/share/doc` (14.8k entries), median ms | GTK | port | earlier port (its doc) |
|---|---|---|---|
| Window shown | 115 | 170 | 170 |
| Scan done | 258 | 173 | 172 |
| **First treemap presented** | **391** | **258** | 262 (GTK then: 360) |
| CPU to the first treemap | 440 | 300 | 310 |
| Layouts / paints until then | 2 / 2 | 1 / 1 | 1 / 1 |
| First treemap, `/usr/share` (120k entries) | 482 | 358 | 366 |

| Key → frame presented, median / p95 ms | GTK | port |
|---|---|---|
| Return, into the largest folder (~100 tiles) | 58 / 81 | 13 / 26 |
| BackSpace, out to the root (~1,300 tiles) | 53 / 85 | 16 / 33 |
| `t` (Size / Files / Age) | 84 / 117 | 16 / 31 |
| `[` `]` (depth) | 52 / 83 | 12 / 24 |
| one filter key | 54 / 80 | 18 / 31 |

| 8 s workloads: CPU ms / missed vsyncs | GTK | port |
|---|---|---|
| SWEEP (hover every frame) | 2,740 / 76 | 4,120 / 0 |
| ZOOMSWEEP (wheel in and out) | 1,520 / 74 | 3,320 / 1 |
| NAV (Return, BackSpace every 400 ms) | 1,840 / 18 | 1,560 / 10 |
| MODES (`t` every 400 ms) | 1,780 / 12 | 500 / 0 |
| DEPTH (`]` `]` `]` `[` `[` `[`) | 1,140 / 0 | 410 / 0 |
| FILTER (`/license` Return Escape) | 2,120 / 66 | 1,100 / 41 |

**The window from outside** (`bench.sh`, omasharp's `tools/bench.sh` made safe to run
next to other benchmarks): plain release builds, launched on a hidden special workspace of the
session, 7 alternating runs, `/usr/share/doc`.

| median (range) | GTK | port |
|---|---|---|
| Time to window | 398 ms (304–466) | 365 ms (275–571) |
| RSS / PSS | 89 / 64 MB | 117 / 60 MB |
| Threads | 19 | 20 |
| Idle CPU, wake-ups/s | 0‰, 0 | 0‰, 3 |
| Close | 42 ms | 58 ms |
| On disk | 5.05 MB (one file) | 5.31 MB (host 0.25 + lib 5.03 + QML 0.04) |

What it says:

- The port shows its first treemap about 130 ms sooner and answers keys 3–5× faster. It lays out
  and paints once where GTK does it twice, and its paint is a 4 ms C# `Drawing` against GTK's
  70 ms Cairo texture. That holds the earlier port's level (262 ms; 13–20 ms per key).
- Its frames are steadier: Qt renders on its own thread, so hover and zoom miss 0–1 vsyncs in
  8 s where GTK misses about 75.
- It costs more CPU while frames animate nonstop (hover and zoom sweeps): Qt redraws the whole
  window every frame, where GTK repaints only what changed. For keys and modes it uses less.
- Its window appears about 55 ms later inside the process (the QML engine), but its scan starts
  earlier, so the treemap still wins.
- More RSS (Qt's shared libraries), less PSS. The 3 idle wake-ups a second are Qt's Wayland
  event threads: a plain Qt C++ app (`/usr/bin/omacalc`) has the same 3.
- A first pass ran while other work loaded the machine (load average up to 6 on 4 threads):
  everything was slower for both apps, by about the same amount. The tables are from a second,
  quiet pass (load average median 1.05, logged in `loadavg.txt`).

To run it again: build the four binaries (GTK from luisrudge/disktiles with the .NET 11 SDK:
`dotnet publish src/Disktiles -c Release`, and with `-p:TestHooks=true` plus
`orig-instrumentation.patch` from the earlier port's docs; this app with `dotnet publish -o
/tmp/claude-disktiles/port-plain` and `-p:TestHooks=true -o /tmp/claude-disktiles/port-perf`),
then `sh bench/run-all.sh /tmp/claude-disktiles/bench` (about 20 minutes; it never shows
anything on screen).
