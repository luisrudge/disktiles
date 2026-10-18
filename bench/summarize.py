#!/usr/bin/env python3
# summarize.py BENCH.txt [label...]: median (min–max) per app per metric, as a Markdown table.
import statistics, sys
rows = {}
for line in open(sys.argv[1]):
    parts = line.split()
    if len(parts) < 2 or "=" not in parts[1]: continue
    d = rows.setdefault(parts[0], {})
    for kv in parts[1:]:
        k, v = kv.split("=", 1)
        try: d.setdefault(k, []).append(float(v))
        except ValueError: pass
labels = sys.argv[2:] or list(rows)
metrics = [("window_ms", "Time to window (ms)", 1), ("rss_kb", "RSS (MB)", 1024), ("pss_kb", "PSS (MB)", 1024),
           ("threads", "Threads", 1), ("procs", "Processes", 1), ("idle_permille", "Idle CPU (‰ of a core)", 1),
           ("wakeups_s", "Idle wake-ups/s", 1), ("close_ms", "Close (ms)", 1)]
def fmt(xs, div):
    if not xs: return "–"
    xs = [x / div for x in xs]
    m, lo, hi = statistics.median(xs), min(xs), max(xs)
    f = (lambda x: f"{x:.0f}") if div == 1 else (lambda x: f"{x:.1f}")
    return f(m) if lo == hi else f"{f(m)} ({f(lo)}–{f(hi)})"
print("| | " + " | ".join(labels) + " |")
print("|---|" + "---|" * len(labels))
for key, name, div in metrics:
    print(f"| {name} | " + " | ".join(fmt(rows[l][key], div) if key in rows.get(l, {}) else "–" for l in labels) + " |")
print(f"\nn = {', '.join(f'{l}: {len(rows[l].get(metrics[0][0], []))}' for l in labels)} runs")
