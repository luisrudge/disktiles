#!/usr/bin/env python3
"""compare.py BEFORE_DIR AFTER_DIR: GTK vs QML-before vs QML-after tables (markdown).
GTK comes from AFTER_DIR's runs (the same GTK build ran in both passes)."""
import json, re, statistics as st, sys, glob, os
from collections import defaultdict

QSG_RENDER = re.compile(r"^\s*([\d.]+) qt.scenegraph.time.renderloop .*render thread.*frame rendered in (\d+)ms, sync=(\d+), render=(\d+), swap=(\d+)")
QSG_PREP = re.compile(r"^\s*([\d.]+) qt.scenegraph.time.renderloop .*gui thread\] Frame prepared, polish=(\d+) ms, lock=(\d+) ms, blockedForSync=(\d+) ms, animations=(\d+) ms")

def load(path):
    out = []
    for line in open(path, errors="replace"):
        line = line.strip()
        if line:
            try: out.append(json.loads(line))
            except json.JSONDecodeError: pass
    return out

def first(evs, ev):
    return next((e for e in evs if e["ev"] == ev), None)

class Run:
    def __init__(self, path, app):
        self.evs = load(path)
        self.app = app
        self.rendered, self.prepared = [], []
        log = path[:-6] + ".log"
        if not os.path.exists(log): log += ".gz"
        a = first(self.evs, "anchor")
        if app == "qml" and a and os.path.exists(log):
            import gzip
            f = gzip.open(log, "rt", errors="replace") if log.endswith(".gz") else open(log, errors="replace")
            conv = lambda s: float(s) * 1000 - a["mono_ms"] + a["age_ms"]
            for line in f:
                m = QSG_RENDER.match(line)
                if m: self.rendered.append((conv(m.group(1)), float(m.group(3)) + float(m.group(4)))); continue
                m = QSG_PREP.match(line)
                if m: self.prepared.append((conv(m.group(1)), float(m.group(2)) + float(m.group(5))))

    def presented_after(self, t):
        if self.app == "qml":
            return next((x[0] for x in self.rendered if x[0] > t), None)
        return next((e["t"] for e in self.evs if e["ev"] == "presented" and e["t"] > t), None)

def load_dir(d):
    runs = defaultdict(list)
    for p in sorted(glob.glob(f"{d}/*.jsonl")):
        m = re.match(r"(gtk|qml)-(\w+)-(doc|share)-(\d+)$", os.path.basename(p)[:-6])
        if m:
            app, w, folder, _ = m.groups()
            runs[(app, w, folder)].append(Run(p, app))
    return runs

before, after = load_dir(sys.argv[1]), load_dir(sys.argv[2])
cols = [("GTK", after, "gtk"), ("QML before", before, "qml"), ("QML after", after, "qml")]

def med(xs): return st.median(xs) if xs else float("nan")
def pct(xs, q):
    xs = sorted(xs)
    return xs[min(len(xs) - 1, int(q * len(xs)))] if xs else float("nan")
def f0(x): return "–" if x != x else f"{x:,.0f}"
def f1(x): return "–" if x != x else f"{x:,.1f}"

out = []
def table(title, header, rows, note=""):
    out.append(f"\n#### {title}\n")
    if note: out.append(note + "\n")
    out.append("| " + " | ".join(header) + " |")
    out.append("|" + "|".join(["---"] * len(header)) + "|")
    for r in rows: out.append("| " + " | ".join(r) + " |")

def drawn_marks(r):
    # Where the treemap's picture was handed over: QML-after "shown", QML-before "paint" (Canvas), GTK "paint".
    s = [e for e in r.evs if e["ev"] == "shown"]
    return s if s else [e for e in r.evs if e["ev"] == "paint"]

def startup(runs, app, folder, wl=None):
    res = defaultdict(list)
    for (a, w, f), rs in runs.items():
        if a != app or f != folder or (wl and w not in wl): continue
        for r in rs:
            for ev in ["main", "toolkit", "window", "scan_end"]:
                e = first(r.evs, ev)
                if e: res[ev].append(e["t"])
            if app == "qml":
                if r.rendered: res["frame"].append(r.rendered[0][0])
                d = [e for e in drawn_marks(r) if e["tiles"] > 0]
                t = r.presented_after(d[0]["t"]) if d else None
                if t: res["first_treemap"].append(t)
            else:
                for ev in ["frame", "first_treemap"]:
                    e = first(r.evs, ev)
                    if e: res[ev].append(e["t"])
            e = first(r.evs, "first_treemap")
            if e and "cpu" in e: res["cpu"].append(e["cpu"])
            ft = res["first_treemap"][-1] if res["first_treemap"] else 1e9
            res["layouts"].append(sum(1 for e in r.evs if e["ev"] == "layout" and e["t"] <= ft and e.get("tiles", 0) > 0))
    return res

for folder, label, wl in [("doc", "/usr/share/doc (14.8k entries); medians, ms since the process started", None),
                          ("share", "/usr/share (120k entries); medians", ["IDLE"])]:
    data = [startup(runs, app, folder, wl) for _, runs, app in cols]
    rows = []
    for key, what in [("main", "first C# runs"), ("toolkit", "toolkit ready"), ("window", "window built"), ("scan_end", "scan done"),
                      ("frame", "first frame presented"), ("first_treemap", "**first treemap presented**"), ("cpu", "CPU to first treemap"),
                      ("layouts", "layouts until the first treemap")]:
        rows.append([what] + [f0(med(d[key])) for d in data])
    table(f"Startup, {label}", ["", "GTK", "QML before", "QML after"], rows)

def steps(runs, app, w, keys=None):
    res = defaultdict(list)
    for r in runs.get((app, w, "doc"), []):
        ds, de = first(r.evs, "drive_start"), first(r.evs, "drive_end")
        if not ds or not de: continue
        evs = [e for e in r.evs if ds["t"] <= e["t"] <= de["t"]]
        ki = [i for i, e in enumerate(evs) if e["ev"] == "key"]
        for n, i in enumerate(ki):
            if keys and evs[i]["key"] not in keys: continue
            seg = evs[i + 1:(ki[n + 1] if n + 1 < len(ki) else len(evs))]
            d = [e for e in seg if e["ev"] == "shown"] or [e for e in seg if e["ev"] == "paint"]
            if not d: continue
            p = r.presented_after(d[-1]["t"])
            if p is None: continue
            res["latency"].append(p - evs[i]["t"])
            for k in ["layout", "vm", "notify", "paint", "shown"]:
                xs = [e["ms"] for e in seg if e["ev"] == k]
                if xs: res[k].append(sum(xs))
    return res

rows = []
for w, keys, label in [("NAV", ["Return"], "NAV Return (~410 tiles)"), ("NAV", ["BackSpace"], "NAV BackSpace (~1,150 tiles)"),
                       ("MODES", None, "MODES t"), ("DEPTH", None, "DEPTH [ ]"), ("FILTER", None, "FILTER key")]:
    data = [steps(runs, app, w, keys) for _, runs, app in cols]
    rows.append([label] + [f"{f0(med(d['latency']))} / {f0(pct(d['latency'], 0.95))}" for d in data])
table("Key → presented: median / p95 ms (all steps of 5 runs)", ["step", "GTK", "QML before", "QML after"], rows)

data = [steps(runs, app, "NAV", ["BackSpace"]) for _, runs, app in cols]
rows = []
for k, what in [("layout", "layout (Core)"), ("vm", "tile styling (C#)"), ("notify", "notify QML (before: + the tile list as JSON)"),
                ("paint", "paint (GTK: Cairo texture; before: Canvas JS; after: C# Drawing)"), ("shown", "host takes the drawing (after)"),
                ("latency", "**key → presented**")]:
    rows.append([what] + [f1(med(d[k])) if d[k] else "–" for d in data])
table("One BackSpace step, where the time goes: median ms", ["", "GTK", "QML before", "QML after"], rows)

def workload(runs, app, w):
    res = defaultdict(list)
    for r in runs.get((app, w, "doc"), []):
        ds, de, fr = first(r.evs, "drive_start"), first(r.evs, "drive_end"), first(r.evs, "frames")
        if not de or not fr: continue
        res["cpu"].append(de["cpu_ms"])
        at = [x[0] for x in r.rendered if ds["t"] <= x[0] <= de["t"]] if app == "qml" else fr["at"]
        iv = [b - a for a, b in zip(at, at[1:]) if b - a < 250]
        res["frames"].append(len(at))
        res["iv"].extend(iv)
        res["missed"].append(sum(1 for x in iv if x > 20))
        if app == "qml":
            res["polish"].extend(p[1] for p in r.prepared if ds["t"] <= p[0] <= de["t"])
        th = first(r.evs, "drive_threads")
        if th:
            for k, v in th["threads"].items(): res["th:" + k].append(v)
    return res

rows = []
for w in ["SWEEP", "ZOOMSWEEP", "NAV", "MODES", "DEPTH", "FILTER"]:
    data = [workload(runs, app, w) for _, runs, app in cols]
    rows.append([w] + [f"{f0(med(d['cpu']))}" for d in data] + [f"{f0(med(d['missed']))}" for d in data] + [f"{f1(pct(d['iv'], 0.95))}" for d in data]
                + [f"{f0(pct(d['polish'], 0.95))}" if d['polish'] else "–" for d in data[1:]])
table("Workloads (8 s each, 5 runs): CPU ms (median), missed vsyncs (frame intervals >20 ms, median per run), frame interval p95 ms, QML GUI-thread polish p95 ms",
      ["workload", "CPU GTK", "CPU QML before", "CPU QML after", "missed GTK", "missed before", "missed after", "p95 GTK", "p95 before", "p95 after", "polish p95 before", "polish p95 after"], rows)

rows = []
names = {"disktiles": "GUI", "QSGRenderThread": "render", "Thread (pooled)": "pooled", "WaylandEventThr": "Wayland"}
for w in ["SWEEP", "ZOOMSWEEP", "NAV", "MODES", "DEPTH", "FILTER"]:
    d = workload(after, "qml", w)
    g = workload(after, "gtk", w)
    parts = ", ".join(f"{names[k]} {f0(med(d['th:' + k]))}" for k in names if d.get("th:" + k))
    rows.append([w, f0(med(g.get("th:disktiles", []))), parts])
table("CPU per thread after (median ms over 5 runs)", ["workload", "GTK (GUI thread; GTK renders there)", "QML after"], rows)

print("\n".join(out))
