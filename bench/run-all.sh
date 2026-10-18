#!/bin/sh
# run-all.sh OUTDIR: the whole benchmark, GTK original vs this port, one after the other.
#   1. ibench.sh: the instrumented builds (test hooks) inside a private nested Hyprland:
#      startup phases, first treemap, key -> presented, workloads.
#   2. bench.sh (omasharp's tools/bench.sh): the plain release builds on a hidden special
#      workspace: time to window, memory, threads, idle CPU and wake-ups, close.
# Builds (see README.md, "Benchmark"): GTK in /tmp/claude-disktiles/orig-{plain,perf}/out,
# the port in /tmp/claude-disktiles/port-{plain,perf}.
set -u
out=$1; here=$(cd "$(dirname "$0")" && pwd); mkdir -p "$out"
# The machine's load while it runs (other work skews both apps; interleaving spreads it evenly).
( while :; do echo "$(date +%T) $(cut -d' ' -f1-3 /proc/loadavg)"; sleep 10; done ) > "$out/loadavg.txt" &
loadlog=$!
trap 'kill $loadlog 2>/dev/null' EXIT
NODETAIL=1 RUNS=${RUNS:-5} sh "$here/ibench.sh" "$out/internal" 2> "$out/ibench.log"
python3 "$here/analyze.py" "$out/internal" > "$out/internal.md"
gtk=/tmp/claude-disktiles/orig-plain/out/disktiles
qml=/tmp/claude-disktiles/port-plain/disktiles-omasharp
for i in $(seq 1 "${EXT_RUNS:-7}"); do
  if [ $((i % 2)) = 1 ]; then
    sh "$here/bench.sh" gtk "$gtk" /usr/share/doc; sh "$here/bench.sh" qml "$qml" /usr/share/doc
  else
    sh "$here/bench.sh" qml "$qml" /usr/share/doc; sh "$here/bench.sh" gtk "$gtk" /usr/share/doc
  fi
done > "$out/external.txt" 2> "$out/external.log"
python3 "$here/summarize.py" "$out/external.txt" gtk qml > "$out/external.md"
echo DONE > "$out/DONE"
