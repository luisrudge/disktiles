#!/bin/sh
# bench.sh LABEL CMD [ARGS...]: one run on a hidden special workspace of the real session.
# Prints: label window_ms rss_kb pss_kb threads procs idle_cpu_permille wakeups_s close_ms
# Memory, threads, CPU and wake-ups cover the whole process tree (app + any child backend).
# BENCH_IDLE=0 skips the 10 s idle sample.
set -u
label=$1; shift
# shellcheck source=tools/session.sh
. "$(dirname "$0")/session.sh"
export HYPRLAND_INSTANCE_SIGNATURE="$MAIN_SIG" WAYLAND_DISPLAY="$MAIN_WL"
tmp=${XDG_RUNTIME_DIR}/omasharp-bench; mkdir -p "$tmp"
pid=; launcher=
# shellcheck disable=SC2013 # child pids are single words
tree() { echo "$1"; for c in $(cat "/proc/$1/task/"*/children 2>/dev/null); do tree "$c"; done; }
cleanup() {
  [ -n "$pid" ] && for p in $(tree "$pid"); do kill -9 "$p" 2>/dev/null; done
  rm -f "$launcher"
}
trap cleanup EXIT
# A hidden special workspace: no monitor is added to the user's session (layout and pointer untouched).
ws="special:omasharp-bench"
launcher=$(mktemp "$tmp/launch.XXXXXX")
{ echo '#!/bin/sh'; printf 'exec'; for a in "$@"; do printf " '%s'" "$a"; done; echo " >'$tmp/$label.log' 2>&1"; } >"$launcher"; chmod +x "$launcher"
start=$(date +%s%N)
hyprctl dispatch "hl.dsp.exec_cmd(\"$launcher\", { workspace = \"$ws silent\" })" >/dev/null
for _ in $(seq 500); do
  pid=$(hyprctl clients -j | jq -r ".[] | select(.workspace.name==\"$ws\") | .pid" | head -1)
  [ -n "$pid" ] && break; sleep 0.01
done
[ -n "$pid" ] || { echo "$label no-window"; exit 1; }
win=$(( ($(date +%s%N) - start) / 1000000 ))
addr=$(hyprctl clients -j | jq -r ".[] | select(.workspace.name==\"$ws\") | .address" | head -1)
sleep 3
procs=$(tree "$pid")
rss=0; pss=0; thr=0; n=0
for p in $procs; do
  rss=$((rss + $(awk '/VmRSS/ {print $2}' "/proc/$p/status")))
  pss=$((pss + $(awk '/^Pss:/ {print $2}' "/proc/$p/smaps_rollup")))
  thr=$((thr + $(find "/proc/$p/task" -mindepth 1 -maxdepth 1 | wc -l))); n=$((n+1))
done
cpu() { s=0; for p in $procs; do s=$((s + $(awk '{print $14+$15}' "/proc/$p/stat"))); done; echo "$s"; }
# shellcheck disable=SC2046 # the globs expand to every thread's status file
ctx() { cat $(for p in $procs; do echo "/proc/$p/task/"*/status; done) 2>/dev/null | awk '/^voluntary_ctxt/ {s+=$2} END {print s}'; }
idle=-; wk=-
if [ "${BENCH_IDLE:-1}" != 0 ]; then
  hz=$(getconf CLK_TCK); t1=$(cpu); c1=$(ctx); sleep 10; t2=$(cpu); c2=$(ctx)
  idle=$(( (t2-t1)*1000/(10*hz) )); wk=$(( (c2-c1)/10 ))
fi
s=$(date +%s%N); hyprctl dispatch "hl.dsp.window.close({ window = \"address:$addr\" })" >/dev/null
close=-
for _ in $(seq 300); do [ -d "/proc/$pid" ] || { close=$(( ($(date +%s%N)-s)/1000000 )); break; }; sleep 0.01; done
[ "$close" = - ] && close=timeout
sleep 0.2
left=0; for p in $procs; do [ -d "/proc/$p" ] && left=$((left+1)); done
echo "$label window_ms=$win rss_kb=$rss pss_kb=$pss threads=$thr procs=$n idle_permille=$idle wakeups_s=$wk close_ms=$close leftover_procs=$left"
