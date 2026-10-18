#!/bin/sh
# shot.sh OUT.png LOG CMD...: screenshot an app without touching the user's screen.
# Starts a private nested Hyprland (minimal config, no autostart, exports nothing to the
# session) on a hidden special workspace, runs CMD inside it on a private D-Bus session,
# optionally types keys into it with wtype (input stays inside the nested compositor),
# screenshots, and cleans up. Exits 1 if the app didn't start, died, or the shot failed.
# Works whether or not the user's session is locked. check.sh runs it.
# Optional, read from files so no shell ever evaluates them:
#   OMASHARP_SHOT_KEYS_FILE  text plus {Tab} {Enter} {Up} {Ctrl+a} {Alt+1} ... (see keys.py)
#   OMASHARP_SHOT_ARGS_FILE  extra app arguments, split on whitespace
# Also: OMASHARP_SHOT_SIZE (WxH logical pixels), OMASHARP_SHOT_WAIT (seconds before keys).
# (The omasharp repo's tools/session.sh has a copy of the session lookup; keep them in step.)
set -u
out=$1; log=$2; shift 2
here=$(cd "$(dirname "$0")" && pwd)
sq() { printf "'%s'" "$(printf '%s' "$1" | sed "s/'/'\\\\''/g")"; } # shell-quote one word
q() { printf ' '; sq "$1"; }                                          # ... after a space
size=${OMASHARP_SHOT_SIZE:-}; [ -n "$size" ] || size=1280x960
printf '%s' "$size" | grep -Eqx '[0-9]+x[0-9]+' || { echo "shot: bad size '$size' (use WxH, like 1280x960)" >&2; exit 1; }
keys=; [ -n "${OMASHARP_SHOT_KEYS_FILE:-}" ] && [ -s "$OMASHARP_SHOT_KEYS_FILE" ] && keys=$(cat "$OMASHARP_SHOT_KEYS_FILE")
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
# The user's session: the oldest Hyprland instance that answers (ignore stale env vars).
main_sig=; main_wl=
for s in $(hyprctl instances -j 2>/dev/null | jq -r 'sort_by(.time) | .[].instance'); do
  if HYPRLAND_INSTANCE_SIGNATURE=$s hyprctl monitors >/dev/null 2>&1; then
    main_sig=$s; main_wl=$(hyprctl instances -j | jq -r --arg s "$s" '.[] | select(.instance == $s) | .wl_socket'); break
  fi
done
[ -n "$main_sig" ] || { echo "shot: needs a running Hyprland session" >&2; exit 1; }
main() { HYPRLAND_INSTANCE_SIGNATURE=$main_sig WAYLAND_DISPLAY=$main_wl hyprctl "$@"; }
tmp=$(mktemp -d /tmp/omasharp-shot.XXXXXX); nest_pid=; nest_sig=; app_pid=
# shellcheck disable=SC2013,SC2329 # child pids are single words; cleanup runs from the trap below
tree() { echo "$1"; for c in $(cat "/proc/$1/task/"*/children 2>/dev/null); do tree "$c"; done; }
# shellcheck disable=SC2329
cleanup() {
  procs=; [ -n "$app_pid" ] && procs=$(tree "$app_pid")
  for p in $procs; do kill "$p" 2>/dev/null; done; sleep 0.3; for p in $procs; do kill -9 "$p" 2>/dev/null; done
  [ -n "$nest_pid" ] && { kill "$nest_pid" 2>/dev/null; sleep 0.3; kill -9 "$nest_pid" 2>/dev/null; }
  [ -n "$nest_sig" ] && [ "$nest_sig" != "$main_sig" ] && rm -rf "$XDG_RUNTIME_DIR/hypr/$nest_sig"
  rm -rf "$tmp"
}
trap cleanup EXIT INT TERM

# 1. The nested compositor's window lives on a hidden special workspace of the user's session:
# no monitor is added (the user's layout and pointer are untouched) and it never shows.
ws="special:omasharp-shot"
{ echo '#!/bin/sh'; echo "echo \$\$ >$(q "$tmp/nest.pid")"
  echo "export HYPRLAND_NO_SD_VARS=1 HYPRLAND_NO_SD_NOTIFY=1 HYPRLAND_NO_CRASHREPORTER=1 WAYLAND_DISPLAY=$main_wl"
  echo 'unset HYPRLAND_INSTANCE_SIGNATURE DISPLAY'
  echo "exec Hyprland --config$(q "$here/shot-hyprland.lua") >$(q "$tmp/hyprland.log") 2>&1"; } > "$tmp/nest"
chmod +x "$tmp/nest"
main dispatch "hl.dsp.exec_cmd(\"$tmp/nest\", { workspace = \"$ws silent\" })" >/dev/null
# Find our instance by its pid (the script execs Hyprland), so parallel shots never mix up.
for _ in $(seq 100); do
  nest_pid=$(cat "$tmp/nest.pid" 2>/dev/null)
  [ -n "$nest_pid" ] && nest_sig=$(hyprctl instances -j | jq -r --argjson p "$nest_pid" '.[] | select(.pid == $p) | .instance')
  [ -n "$nest_sig" ] && break; sleep 0.1
done
[ -n "$nest_sig" ] || { echo "shot: nested Hyprland didn't start" >&2; tail -5 "$tmp/hyprland.log" >&2; exit 1; }
export HYPRLAND_INSTANCE_SIGNATURE="$nest_sig"
nest_wl=$(hyprctl instances -j | jq -r --arg s "$nest_sig" '.[] | select(.instance == $s) | .wl_socket')
export WAYLAND_DISPLAY="$nest_wl"
sleep 0.5

# 2. Its own headless output to draw the app on.
hyprctl output create headless shot >/dev/null
# The screen the app sees: OMASHARP_SHOT_SIZE logical pixels (default 1280x960) at 1.5x scale.
w=${size%x*}; h=${size#*x}
hyprctl eval "hl.monitor({ output = \"shot\", mode = \"$((w * 3 / 2))x$((h * 3 / 2))\", position = \"auto\", scale = 1.5 })" >/dev/null
sleep 0.3
hyprctl dispatch 'hl.dsp.focus({ monitor = "shot" })' >/dev/null
nws=$(hyprctl monitors -j | jq -r '.[] | select(.name=="shot") | .activeWorkspace.id')

# 3. The app.
{ echo '#!/bin/sh'; echo "echo \$\$ > '$tmp/app.pid'"
  echo "export OMASHARP_PANEL_PAYLOAD='{\"screen\":\"shot\"}' QT_FORCE_STDERR_LOGGING=1 OMASHARP_TEXT_DUMP=$(sq "$tmp/text.txt")"
  # Keep the app off the user's desktop beyond the display: no portal dialogs, browser tabs or
  # notifications there (its own D-Bus session; the generic Qt theme, so dialogs draw in-window).
  echo "export OMASHARP_PLATFORMTHEME=generic QT_QPA_PLATFORMTHEME=generic NO_AT_BRIDGE=1 BROWSER=true"
  extra=; [ -n "${OMASHARP_SHOT_ARGS_FILE:-}" ] && [ -s "$OMASHARP_SHOT_ARGS_FILE" ] && extra=$(cat "$OMASHARP_SHOT_ARGS_FILE")
  printf 'exec dbus-run-session --config-file=%s --' "$(sq "$here/shot-dbus.conf")"; for a in "$@"; do q "$a"; done
  set -f; for a in $extra; do q "$a"; done; set +f
  echo " >$(q "$log") 2>&1"; } > "$tmp/app"
chmod +x "$tmp/app"; : > "$tmp/text.txt"
hyprctl dispatch "hl.dsp.exec_cmd(\"$tmp/app\", { workspace = \"$nws silent\" })" >/dev/null
for _ in $(seq 100); do app_pid=$(cat "$tmp/app.pid" 2>/dev/null); [ -n "$app_pid" ] && break; sleep 0.05; done
sleep "${OMASHARP_SHOT_WAIT:-3}"
if [ -n "$keys" ]; then
  addr=$(hyprctl clients -j | jq -r ".[] | select(.workspace.id==$nws) | .address" | head -1)
  [ -n "$addr" ] && hyprctl dispatch "hl.dsp.focus({ window = \"address:$addr\" })" >/dev/null
  sleep 0.2; python3 "$here/keys.py" "$keys"; sleep 0.8
fi
sleep 0.3
fail=0
if grim -o shot "$out"; then echo "Screenshot: $out (open it and look)"; else echo "shot: screenshot failed" >&2; fail=1; fi
txt=${out%.png}.txt
if [ -s "$tmp/text.txt" ]; then cp "$tmp/text.txt" "$txt"; echo "--- visible text, top to bottom ($txt) ---"; cat "$txt"; fi
if [ -z "$app_pid" ] || ! kill -0 "$app_pid" 2>/dev/null; then echo "shot: the app didn't start or exited early (see its output below)" >&2; fail=1; fi
output=$(grep -vE "register with host portal|org.freedesktop.DBus.Error.ServiceUnknown|INFO.*(Launching config|Shell ID|Saving logs|Configuration Loaded)" "$log" 2>/dev/null | tail -40)
[ -n "$output" ] && { echo "--- app output ($log) ---"; echo "$output"; }
exit $fail
