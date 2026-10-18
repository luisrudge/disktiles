#!/bin/sh
# ibench.sh OUTDIR: internal-ops benchmark, GTK original vs this port (from the earlier port's
# docs/bench/disktiles-internals/ibench.sh; paths adapted), all inside one private
# nested Hyprland hosted on a hidden special workspace of the user's session (no monitor is
# added there). Each run: DISKTILES_PERF=<jsonl> DISKTILES_DRIVE=<workload>; the app quits itself.
set -u
OUT=$1; mkdir -p "$OUT"
RUNS=${RUNS:-5}
GTK=${GTK:-/tmp/claude-disktiles/orig-perf/out/disktiles}
QML=${QML:-/tmp/claude-disktiles/port-perf/disktiles-omasharp}
WORKLOADS=${WORKLOADS:-"IDLE:/usr/share/doc IDLE:/usr/share SWEEP:/usr/share/doc ZOOMSWEEP:/usr/share/doc NAV:/usr/share/doc MODES:/usr/share/doc DEPTH:/usr/share/doc FILTER:/usr/share/doc"}
export XDG_RUNTIME_DIR=${XDG_RUNTIME_DIR:-/run/user/$(id -u)}
main_sig=; main_wl=
for s in $(hyprctl instances -j 2>/dev/null | jq -r 'sort_by(.time) | .[].instance'); do
  if HYPRLAND_INSTANCE_SIGNATURE=$s hyprctl monitors >/dev/null 2>&1; then
    main_sig=$s; main_wl=$(hyprctl instances -j | jq -r --arg s "$s" '.[] | select(.instance == $s) | .wl_socket'); break
  fi
done
main() { HYPRLAND_INSTANCE_SIGNATURE=$main_sig WAYLAND_DISPLAY=$main_wl hyprctl "$@"; }
mkdir -p /tmp/claude-disktiles; tmp=$(mktemp -d /tmp/claude-disktiles/ibench-run.XXXXXX); nest_pid=; nest_sig=; app_pid=
cleanup() {
  [ -n "$app_pid" ] && kill -9 "$app_pid" 2>/dev/null
  [ -n "$nest_pid" ] && { kill "$nest_pid" 2>/dev/null; sleep 0.3; kill -9 "$nest_pid" 2>/dev/null; }
  [ -n "$nest_sig" ] && [ "$nest_sig" != "$main_sig" ] && rm -rf "$XDG_RUNTIME_DIR/hypr/$nest_sig"
  rm -rf "$tmp"
}
trap cleanup EXIT INT TERM
here=$(cd "$(dirname "$0")/../.agents/skills/omasharp/scripts" && pwd)
before=$(hyprctl instances -j | jq -r '.[].instance' | sort | tr '\n' ' ')
printf '#!/bin/sh\necho $$ > %s/nest.pid\nexport HYPRLAND_NO_SD_VARS=1 HYPRLAND_NO_SD_NOTIFY=1 HYPRLAND_NO_CRASHREPORTER=1 WAYLAND_DISPLAY=%s\nunset HYPRLAND_INSTANCE_SIGNATURE DISPLAY\nexec Hyprland --config %s/shot-hyprland.lua >%s/hyprland.log 2>&1\n' "$tmp" "$main_wl" "$here" "$tmp" > "$tmp/nest"
chmod +x "$tmp/nest"
main dispatch "hl.dsp.exec_cmd(\"$tmp/nest\", { workspace = \"special:claude-disktiles-ibench silent\" })" >/dev/null
for _ in $(seq 100); do
  nest_sig=$(hyprctl instances -j | jq -r '.[].instance' | while read -r s; do case " $before " in *" $s "*) ;; *) echo "$s";; esac; done | head -1)
  [ -n "$nest_sig" ] && break; sleep 0.1
done
nest_pid=$(cat "$tmp/nest.pid")
[ -n "$nest_sig" ] || { echo "nested Hyprland didn't start" >&2; exit 1; }
export HYPRLAND_INSTANCE_SIGNATURE=$nest_sig
export WAYLAND_DISPLAY=$(hyprctl instances -j | jq -r --arg s "$nest_sig" '.[] | select(.instance == $s) | .wl_socket')
sleep 0.5
# Inside the nested compositor: a 1600x1000 logical output at 2x, like the laptop's panel (2560x1600 at 2x).
hyprctl output create headless bench >/dev/null
hyprctl eval 'hl.monitor({ output = "bench", mode = "3200x2000@60", position = "auto", scale = 2 })' >/dev/null
sleep 0.3
hyprctl dispatch 'hl.dsp.focus({ monitor = "bench" })' >/dev/null
nws=$(hyprctl monitors -j | jq -r '.[] | select(.name=="bench") | .activeWorkspace.id')
echo "nested $nest_sig on $WAYLAND_DISPLAY, workspace $nws; $(hyprctl monitors -j | jq -c '.[] | {name, width, height, scale, refreshRate}')" >&2

# run APP WORKLOAD FOLDER TAG [ENV...]
run() {
  app=$1; drive=$2; folder=$3; tag=$4; shift 4
  bin=$GTK; [ "$app" = qml ] && bin=$QML
  perf="$OUT/$tag.jsonl"; log="$OUT/$tag.log"; rm -f "$perf"
  secs=8; [ "$drive" = IDLE ] && secs=1
  { echo '#!/bin/sh'; echo "echo \$\$ > '$tmp/app.pid'"
    echo "export DISKTILES_PERF='$perf' DISKTILES_WORKLOAD=$drive DISKTILES_WORKLOAD_SECONDS=$secs QT_FORCE_STDERR_LOGGING=1 QSG_RENDER_TIMING=1 QT_MESSAGE_PATTERN='%{time boot} %{category} %{message}' $* ${EXTRA_ENV:-}"
    echo "exec '$bin' '$folder' >'$log' 2>&1"; } > "$tmp/app"
  chmod +x "$tmp/app"; rm -f "$tmp/app.pid"
  hyprctl dispatch "hl.dsp.exec_cmd(\"$tmp/app\", { workspace = \"$nws silent\" })" >/dev/null
  app_pid=
  for _ in $(seq 100); do app_pid=$(cat "$tmp/app.pid" 2>/dev/null); [ -n "$app_pid" ] && break; sleep 0.05; done
  for _ in $(seq 600); do [ -d /proc/$app_pid ] || break; sleep 0.1; done
  [ -d /proc/$app_pid ] && { echo "TIMEOUT $tag" >&2; kill -9 $app_pid; }
  app_pid=
  echo "$tag $(grep -o '"ev":"drive_end".*' "$perf" 2>/dev/null | head -1)" >&2
  sleep 0.5
}

[ -n "${NOWARM:-}" ] || { run gtk IDLE /usr/share warm-gtk; run qml IDLE /usr/share warm-qml; }
for i in $(seq 1 "$RUNS"); do
  for w in $WORKLOADS; do
    d=${w%%:*}; f=${w#*:}; name=$d-$(basename "$f")
    # Alternate which app goes first.
    if [ $((i % 2)) = 1 ]; then order="gtk qml"; else order="qml gtk"; fi
    for a in $order; do run $a $d "$f" "$a-$name-$i"; done
  done
done
# Detail runs (they cost time themselves, so they are kept out of the numbers above): the
# port's encode probe and Qt's own per-frame render timing. GTK's frame clock is in every run.
if [ -z "${NODETAIL:-}" ]; then
  for w in $WORKLOADS; do
    d=${w%%:*}; f=${w#*:}
    run qml $d "$f" "detail-qml-$d-$(basename "$f")" DISKTILES_PERF_DETAIL=1
  done
fi
echo DONE >&2
