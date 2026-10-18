#!/usr/bin/env python3
"""analyze.py RAWDIR: tables (markdown) from ibench.sh's runs. Prints to stdout."""
import json, re, statistics as st, sys, glob, os
from collections import defaultdict

raw = sys.argv[1]

def load(path):
    evs = []
    for line in open(path):
        line = line.strip()
        if line:
            try: evs.append(json.loads(line))
            except json.JSONDecodeError: pass
    return evs

def med(xs): return st.median(xs) if xs else float('nan')
def pct(xs, q):
    xs = sorted(xs)
    return xs[min(len(xs) - 1, int(q * len(xs)))] if xs else float('nan')
def rng(xs): return f"{med(xs):,.0f} ({min(xs):,.0f}–{max(xs):,.0f})" if xs else "–"
def f1(x): return "–" if x != x else f"{x:,.1f}"
def f0(x): return "–" if x != x else f"{x:,.0f}"

runs = defaultdict(list)  # (app, workload, folder) -> [events]
def first(evs, ev):
    for e in evs:
        if e["ev"] == ev: return e
    return None

# Qt's render thread frames: (seconds since process start, render ms) and gui prepare ms.
QSG_RENDER = re.compile(r"^\s*([\d.]+) qt.scenegraph.time.renderloop .*render thread.*frame rendered in (\d+)ms, sync=(\d+), render=(\d+), swap=(\d+)")
QSG_PREP = re.compile(r"^\s*([\d.]+) qt.scenegraph.time.renderloop .*gui thread\] Frame prepared, polish=(\d+) ms, lock=(\d+) ms, blockedForSync=(\d+) ms, animations=(\d+) ms")

def anchor(evs):
    a = first(evs, "anchor")
    return (a["mono_ms"], a["age_ms"]) if a else None

def qsg_perf(evs, path):
    """Qt's frames in our clock (ms since the process started)."""
    a = anchor(evs)
    rendered, prepared = qsg(path)
    if not a: return [], []
    conv = lambda x: x - a[0] + a[1]
    return [(conv(t), w) for t, w in rendered], [(conv(t), w) for t, w in prepared]

def qsg(path):
    rendered, prepared = [], []
    for line in open(path, errors="replace"):
        m = QSG_RENDER.match(line)
        # Work: sync + render. The swap is mostly waiting for vsync.
        if m: rendered.append((float(m.group(1)) * 1000, float(m.group(3)) + float(m.group(4)))); continue
        m = QSG_PREP.match(line)
        # Work on the GUI thread: polish (QML paints the Canvas here) + animations; lock and blockedForSync are waits.
        if m: prepared.append((float(m.group(1)) * 1000, float(m.group(2)) + float(m.group(5))))
    return rendered, prepared

qtf = {}  # id(evs) -> (rendered, prepared) in our clock
for p in sorted(glob.glob(f"{raw}/*.jsonl")):
    name = os.path.basename(p)[:-6]
    m = re.match(r"(gtk|qml)-(\w+)-(doc|share)-(\d+)$", name) or re.match(r"detail-(qml)-(\w+)-(doc|share)()$", name)
    if not m: continue
    app, w, folder, i = m.groups()
    if name.startswith("detail-"): app = "qmlx"
    evs = load(p)
    if app in ("qml", "qmlx"):
        qtf[id(evs)] = qsg_perf(evs, p[:-6] + ".log")
    runs[(app, w, folder)].append(evs)

def presented_after(evs, app, t):
    """When the first frame after t was presented: GTK's after-paint (our marks), Qt's render thread."""
    if app in ("qml", "qmlx"):
        r = [x[0] for x in qtf[id(evs)][0] if x[0] > t]
        return r[0] if r else None
    r = [e["t"] for e in evs if e["ev"] == "presented" and e["t"] > t]
    return r[0] if r else None


out = []
def table(title, header, rows, note=""):
    out.append(f"\n### {title}\n")
    if note: out.append(note + "\n")
    out.append("| " + " | ".join(header) + " |")
    out.append("|" + "|".join(["---"] * len(header)) + "|")
    for r in rows: out.append("| " + " | ".join(r) + " |")

# ── startup ──
def startup(app, folder, workloads=None):
    rows = defaultdict(list)
    for (a, w, f), rs in runs.items():
        if a != app or f != folder or (workloads and w not in workloads): continue
        for evs in rs:
            for ev in ["main", "toolkit", "window", "scan_start"]:
                e = first(evs, ev)
                if e: rows[ev].append(e["t"])
            if app == "qml":
                rf = qtf[id(evs)][0]
                if rf: rows["frame"].append(rf[0][0])
                fp = [e for e in evs if e["ev"] == "paint" and e["tiles"] > 0]
                ft = presented_after(evs, app, fp[0]["t"]) if fp else None
                if ft: rows["first_treemap"].append(ft)
            else:
                for ev in ["frame", "first_treemap"]:
                    e = first(evs, ev)
                    if e: rows[ev].append(e["t"])
            e = first(evs, "scan_end")
            if e: rows["scan_end"].append(e["t"]); rows["scan_ms"].append(e["ms"]); rows["entries"].append(e["entries"])
            e = first(evs, "first_treemap")
            if e: rows["cpu"].append(e["cpu"])
            # The update that drew the first treemap: the layout/vm/notify/paint after scan_end.
            se = first(evs, "scan_end")
            if se:
                after = [x for x in evs if x["t"] >= se["t"]]
                ft = first(evs, "first_treemap")
                upto = [x for x in after if not ft or x["t"] <= ft["t"]]
                for k in ["layout", "vm", "notify", "paint", "encode"]:
                    xs = [x["ms"] for x in upto if x["ev"] == k and x.get("tiles", 1) > 0]
                    if xs: rows["sum_" + k].append(sum(xs)); rows["n_" + k].append(len(xs))
    return rows

for folder, label, wl in [("doc", "/usr/share/doc (14.8k entries)", None), ("share", "/usr/share (120k entries)", ["IDLE"])]:
    g, q = startup("gtk", folder, wl), startup("qml", folder, wl)
    n = (len(g["main"]), len(q["main"]))
    rows = []
    for key, what in [("main", "Main entered (first managed code)"), ("toolkit", "toolkit init done"), ("window", "window shown"),
                      ("scan_start", "scan start"), ("scan_end", "scan end"), ("frame", "first frame presented"),
                      ("first_treemap", "first treemap presented")]:
        rows.append([what, rng(g[key]), rng(q[key]), f0(med(q[key]) - med(g[key]))])
    rows.append(["scan duration (ms)", rng(g["scan_ms"]), rng(q["scan_ms"]), f0(med(q["scan_ms"]) - med(g["scan_ms"]))])
    rows.append(["layouts / paints until the first treemap", f"{f0(med(g['n_layout']))} / {f0(med(g['n_paint']))}", f"{f0(med(q['n_layout']))} / {f0(med(q['n_paint']))}", ""])
    rows.append(["CPU at first treemap (ms)", rng(g["cpu"]), rng(q["cpu"]), f0(med(q["cpu"]) - med(g["cpu"]))])
    for k, what in [("layout", "layout (Core, all passes until first treemap)"), ("vm", "view-model build"),
                    ("notify", "transfer: encode + host parse + QML bindings"), ("paint", "paint (GTK: Cairo to texture; port: C# Drawing)")]:
        rows.append([what + " ms", rng(g["sum_" + k]) if g["sum_" + k] else "–", rng(q["sum_" + k]) if q["sum_" + k] else "–",
                     f0(med(q["sum_" + k]) - med(g["sum_" + k])) if g["sum_" + k] and q["sum_" + k] else "–"])
    table(f"Startup, {label}: ms since the process started, median (min–max); GTK n={n[0]}, QML n={n[1]}",
          ["phase", "GTK", "QML", "QML − GTK"], rows)

# ── one view update ──
def updates(app, w, keyfilter=None):
    res = defaultdict(list)
    for full in runs.get((app, w, "doc"), []):
        ds = first(full, "drive_start"); de = first(full, "drive_end")
        if not ds or not de: continue
        if app in ("qml", "qmlx"): qtf[id(evs_slice := [e for e in full if ds["t"] <= e["t"] <= de["t"]])] = qtf[id(full)]
        else: evs_slice = [e for e in full if ds["t"] <= e["t"] <= de["t"]]
        evs = evs_slice
        keys = [i for i, e in enumerate(evs) if e["ev"] == "key"]
        for n, i in enumerate(keys):
            k = evs[i]["key"]
            if keyfilter and k not in keyfilter: continue
            end = keys[n + 1] if n + 1 < len(keys) else len(evs)
            seg = evs[i + 1:end]
            paints = [e for e in seg if e["ev"] == "paint"]
            if not paints: continue
            p0 = presented_after(evs, app, paints[0]["t"]); p1 = presented_after(evs, app, paints[-1]["t"])
            if p0 is None or p1 is None: continue
            res["latency"].append(p1 - evs[i]["t"])
            res["first"].append(p0 - evs[i]["t"])
            res["npaint"].append(len(paints))
            for kind in ["layout", "vm", "notify", "paint", "encode"]:
                xs = [e["ms"] for e in seg if e["ev"] == kind]
                if xs: res[kind].append(sum(xs))
            tl = [e["tiles"] for e in seg if e["ev"] == "layout"]
            if tl: res["tiles"].append(tl[-1])
    return res

for w, keys, label in [("NAV", ["Return"], "NAV: Return (into the largest directory; ~410 tiles)"),
                       ("NAV", ["BackSpace"], "NAV: BackSpace (out to the root; ~1,150 tiles)"),
                       ("MODES", None, "MODES: t"), ("DEPTH", None, "DEPTH: [ and ]"), ("FILTER", None, "FILTER: one key")]:
    g, q = updates("gtk", w, keys), updates("qml", w, keys)
    q["encode"] = updates("qmlx", w, keys)["encode"]
    rows = []
    for k, what in [("tiles", "tiles laid out"), ("layout", "layout (Core)"), ("vm", "view-model build"), ("encode", "  transfer, of it: C# JSON encode (detail runs, n=1 run)"),
                    ("notify", "transfer (encode, host parse, QML bindings)"), ("paint", "paint (all paints in the step)"), ("npaint", "paints per step"),
                    ("first", "key → first frame presented after a paint"), ("latency", "key → last frame presented for this step")]:
        gv, qv = g[k], q[k]
        rows.append([what, f1(med(gv)) if gv else "–", f1(med(qv)) if qv else "–", f"{f1(pct(gv,0.95))} / {f1(pct(qv,0.95))}" if gv and qv else "–"])
    table(f"One step, {label}: ms, median over all steps of 5 runs (GTK n={len(g['latency'])}, QML n={len(q['latency'])})",
          ["", "GTK", "QML", "p95 GTK / QML"], rows)

# encode from the detail runs
enc = defaultdict(list)
for p in glob.glob(f"{raw}/detail-qml-*.jsonl"):
    for e in load(p):
        if e["ev"] == "encode": enc["ms"].append(e["ms"]); enc["bytes"].append(e["bytes"])
if enc["ms"]:
    out.append(f"\nC# JSON encode of the tile list (detail runs, n={len(enc['ms'])}): median {med(enc['ms']):.1f} ms, p95 {pct(enc['ms'],0.95):.1f} ms, max {max(enc['ms']):.1f} ms; median {med(enc['bytes'])/1024:.0f} KB of JSON.")

# ── frames per workload ──
def frames(app, w):
    res = defaultdict(list)
    for (a, ww, f), rs in runs.items():
        if a != app or ww != w or f != "doc": continue
    for i, evs in enumerate(runs.get((app, w, "doc"), []), 1):
        de = first(evs, "drive_end"); fr = first(evs, "frames"); ds = first(evs, "drive_start")
        if not de or not fr: continue
        res["cpu"].append(de["cpu_ms"])
        at = fr["at"]
        if app == "gtk":
            work = [x for x in fr["work"] if x >= 0]
        else:
            # Qt: the render thread's frames (QSG_RENDER_TIMING), in the drive's window. Qt's
            # process clock and ours differ by a few ms; anchor on the drive length.
            rendered, prepared = qtf[id(evs)]
            lo, hi = ds["t"], de["t"]
            win = [r for r in rendered if lo <= r[0] <= hi]
            at = [r[0] for r in win]
            work = [r[1] for r in win]
            res["prep"].extend([p[1] for p in prepared if lo <= p[0] <= hi])
        iv = [b - a for a, b in zip(at, at[1:])]
        anim = [x for x in iv if x < 250]
        res["frames"].append(len(at))
        res["iv"].extend(anim)
        res["missed"].append(sum(1 for x in anim if x > 20))
        res["over167"].append(sum(1 for x in anim if x > 16.7))
        res["work"].extend(work)
    return res

rows = []
for w in ["SWEEP", "ZOOMSWEEP", "NAV", "MODES", "DEPTH", "FILTER", "IDLE"]:
    for app in ["gtk", "qml"]:
        r = frames(app, w)
        if not r["cpu"]: continue
        rows.append([w if app == "gtk" else "", app.upper(), f0(med(r["frames"])), f1(med(r["iv"])), f1(pct(r["iv"], 0.95)), f1(max(r["iv"]) if r["iv"] else float('nan')),
                     f0(med(r["missed"])), f0(med(r["over167"])), f1(med(r["work"])), f1(pct(r["work"], 0.95)),
                     f1(med(r["prep"])) + " / " + f1(pct(r["prep"], 0.95)) if r["prep"] else "–", rng(r["cpu"])])
table("Workloads on /usr/share/doc, 8 s each (IDLE 1 s), 5 runs: frames from each toolkit's own clock",
      ["workload", "app", "frames", "interval median", "p95", "max", "missed vsync (>20 ms)", ">16.7 ms", "frame work median", "p95", "QML GUI polish+anim med / p95", "CPU ms, median (range)"], rows,
      "Intervals between presented frames within animation bursts (gaps ≥250 ms between keys are idle, not counted). GTK: GdkFrameClock after-paint times; frame work = before-paint → after-paint on the GTK thread. QML: the render thread's frames from QSG_RENDER_TIMING (1 ms resolution); frame work = sync+render there (swap left out: it is mostly the vsync wait); GUI = polish (the Canvas paints here) + animations on the GUI thread. All ms.")

print("\n".join(out))
