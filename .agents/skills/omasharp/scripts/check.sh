#!/bin/sh
# check.sh [options] [APP_DIR]: check an Omasharp app without touching the user's screen.
# Runs the unit tests (tests/), builds the app, then runs it in a private, invisible compositor
# (shot.sh): prints every visible text in the window, saves obj/shot.png, and shows the app's
# output. Exits non-zero if anything fails, including errors the app's QML throws while running.
#   --release        check the native AOT release build (dotnet publish); fails on IL2xxx/IL3xxx warnings
#   --keys KEYS      type into the app first: text plus {Tab} {Enter} {Ctrl+a} {Wait:500} ... (keys.py)
#   --args ARGS      command-line arguments for the app (split on whitespace; Bridge.Args)
#   --env KEY=VALUE  an environment variable for the app (repeatable); no shell sees the value
#   --size WxH       the screen size in logical pixels at 1.5x scale (default 1280x960); odd numbers
#                    are rounded down to even ones, which 1.5x needs
#   --float          float the window at its own size, centred, instead of tiling it to fill the
#                    screen (for apps that float on Omarchy, like omacalc)
#   --no-tests       skip the unit tests
#   --seed DIR       start the app's data from a copy of DIR: DIR/data, DIR/config, DIR/state and
#                    DIR/cache become its XDG_DATA_HOME, XDG_CONFIG_HOME, XDG_STATE_HOME, XDG_CACHE_HOME
#   --real-data      use your real XDG dirs instead of empty ones in obj/omasharp/xdg/ (careful: the
#                    installed app's data, which --keys would change)
#   --offscreen      window apps without a Hyprland session (CI): Qt's offscreen platform, visible
#                    text only (no screenshot, no keys)
set -u
here=$(cd "$(dirname "$0")" && pwd)
release=; keys=; args=; size=; float=; tests=1; offscreen=; seed=; real=; dir=.
nl='
'; envs=
# What keeps the app inside the private compositor (or offscreen): --env can't change these.
reserved='QT_QPA_PLATFORM WAYLAND_DISPLAY WAYLAND_SOCKET DISPLAY XDG_RUNTIME_DIR HYPRLAND_INSTANCE_SIGNATURE DBUS_SESSION_BUS_ADDRESS BROWSER QT_QPA_PLATFORMTHEME OMASHARP_PLATFORMTHEME OMASHARP_TEXT_DUMP'
while [ $# -gt 0 ]; do
  case $1 in
    --release) release=1 ;;
    --keys) keys=$2; shift ;;
    --args) args=$2; shift ;;
    --env)
      key=${2%%=*}
      { [ "$key" != "$2" ] && printf '%s' "$key" | grep -Eqx '[A-Za-z_][A-Za-z0-9_]*'; } || { echo "check: --env needs KEY=VALUE, not '$2'" >&2; exit 2; }
      case $2 in *"$nl"*) echo "check: an --env value can't contain a newline" >&2; exit 2 ;; esac
      case $key in XDG_DATA_HOME|XDG_CONFIG_HOME|XDG_STATE_HOME|XDG_CACHE_HOME) echo "check: --env can't set $key: the app gets empty XDG dirs in obj/omasharp/xdg/ (fill them with --seed DIR, or use --real-data)" >&2; exit 2 ;; esac
      case " $reserved " in *" $key "*) echo "check: --env can't set $key: the check sets it to keep the app off your screen" >&2; exit 2 ;; esac
      envs="$envs$2$nl"; shift ;;
    --size) size=$2; shift ;;
    --float) float=1 ;;
    --no-tests) tests= ;;
    --offscreen) offscreen=1 ;;
    --seed) seed=$(cd "$2" 2>/dev/null && pwd) || { echo "check: --seed: no folder '$2'" >&2; exit 2; }; shift ;;
    --real-data) real=1 ;;
    -h|--help) sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    -*) echo "check: unknown option $1 (see --help)" >&2; exit 2 ;;
    *) dir=$1 ;;
  esac
  shift
done
cd "$dir" || exit 2
set -- *.csproj
[ $# -eq 1 ] && [ -f "$1" ] || { echo "check: run it in the app's folder (the one with the app's .csproj)" >&2; exit 2; }
obj=$PWD/obj/omasharp; mkdir -p "$obj"
# The app's data, settings, state and cache: empty folders in obj/omasharp/xdg/ (plus --seed), never
# the installed app's. The unit tests and the build (a panel's build runs Backend's constructor) use
# them too; they're emptied again before the app starts, and left after the run to look at. HOME
# stays, so the Omarchy theme still applies, and the user's fonts and font settings are linked in.
xdg=$obj/xdg; fonts=${XDG_DATA_HOME:-$HOME/.local/share}/fonts; fontconfig=${XDG_CONFIG_HOME:-$HOME/.config}/fontconfig
fresh() {
  { rm -rf "$xdg" && mkdir -p "$xdg/data" "$xdg/config" "$xdg/state" "$xdg/cache"; } || { echo "check: couldn't empty $xdg: remove it, then check again" >&2; exit 1; }
  [ -z "$seed" ] || cp -R "$seed/." "$xdg/" || { echo "check: couldn't copy --seed $seed" >&2; exit 1; }
  [ -e "$xdg/data/fonts" ] || [ ! -d "$fonts" ] || ln -s "$fonts" "$xdg/data/fonts"
  [ -e "$xdg/config/fontconfig" ] || [ ! -d "$fontconfig" ] || ln -s "$fontconfig" "$xdg/config/fontconfig"
}
if [ -z "$real" ]; then
  fresh
  envs="${envs}XDG_DATA_HOME=$xdg/data${nl}XDG_CONFIG_HOME=$xdg/config${nl}XDG_STATE_HOME=$xdg/state${nl}XDG_CACHE_HOME=$xdg/cache$nl"
  # Not XDG_CACHE_HOME for dotnet: the build keeps the host it compiled there.
  export XDG_DATA_HOME="$xdg/data" XDG_CONFIG_HOME="$xdg/config" XDG_STATE_HOME="$xdg/state"
fi
printf '%s' "$envs" > "$obj/env"

if [ -n "$tests" ] && ls tests/*.csproj >/dev/null 2>&1; then
  echo "== unit tests"
  dotnet run --project tests || { echo "check: unit tests failed" >&2; exit 1; }
fi

if [ -n "$release" ]; then echo "== release build (native AOT)"; build=publish; else echo "== dev build"; build=build; fi
dotnet $build -nologo -v quiet -t:OmasharpLaunch > "$obj/build.log" 2>&1; status=$?
grep -vE '^\s*$|Time Elapsed|^Build succeeded|^\s+[0-9]+ (Warning|Error)\(s\)' "$obj/build.log"
[ $status -eq 0 ] || { echo "check: build failed" >&2; exit 1; }
if [ -n "$release" ] && grep -qE 'warning IL[0-9]+' "$obj/build.log"; then
  echo "check: IL2xxx/IL3xxx warnings above: this code can fail in the release build. Fix them." >&2; exit 1
fi

[ -n "$real" ] || fresh

# The command that runs this build, one argument per line (written by the build).
set --; while IFS= read -r a; do set -- "$@" "$a"; done < "$obj/launch"
kind=$(cat "$obj/kind")

if [ -n "$offscreen" ]; then
  [ "$kind" = window ] || { echo "check: --offscreen works for window apps only" >&2; exit 2; }
  [ -z "$keys" ] || { echo "check: --offscreen can't type keys" >&2; exit 2; }
  echo "== run (offscreen)"
  # --env: through env(1), one argument per variable, so no shell sees the values.
  if [ -n "$envs" ]; then
    set -- env; while IFS= read -r e; do set -- "$@" "$e"; done < "$obj/env"
    while IFS= read -r a; do set -- "$@" "$a"; done < "$obj/launch"
  fi
  txt=$PWD/obj/shot.txt; rm -f "$txt"
  # shellcheck disable=SC2086
  QT_QPA_PLATFORM=offscreen QT_QUICK_BACKEND=software OMASHARP_TEXT_DUMP="$txt" timeout 15 "$@" $args > "$obj/run.log" 2>&1 &
  pid=$!
  for _ in $(seq 100); do [ -s "$txt" ] && break; sleep 0.1; done
  sleep 0.5; alive=; kill -0 "$pid" 2>/dev/null && alive=1
  kill "$pid" 2>/dev/null; wait "$pid" 2>/dev/null
  if [ -s "$txt" ]; then echo "--- visible text, top to bottom ($txt) ---"; cat "$txt"; fi
  out=$(tail -40 "$obj/run.log"); [ -n "$out" ] && { echo "--- app output ---"; echo "$out"; }
  [ -n "$alive" ] && [ -s "$txt" ] || { echo "check: the app didn't start or exited early" >&2; exit 1; }
else
  echo "== run (private compositor)"
  printf '%s' "$keys" > "$obj/keys"; printf '%s' "$args" > "$obj/args"
  OMASHARP_SHOT_SIZE=$size OMASHARP_SHOT_FLOAT=$float OMASHARP_SHOT_KEYS_FILE=$obj/keys OMASHARP_SHOT_ARGS_FILE=$obj/args OMASHARP_SHOT_ENV_FILE=$obj/env \
    sh "$here/shot.sh" "$PWD/obj/shot.png" "$obj/run.log" "$@" || exit 1
fi

# The app's own QML failing at run time (a binding or handler threw, a value came out undefined):
# the window can look fine while values are missing. Only lines that point at the app's files
# (file://<app>/ in dev, qrc:/ in release, @plugin/ in panels), so Qt's and the shell's own don't count.
errs=$(sed 's/\x1b\[[0-9;]*m//g' "$obj/run.log" 2>/dev/null | awk -v app="file://$PWD/" '
  (index($0, app) || (index($0, "qrc:/") && !index($0, "qrc:/qt-project.org/")) || index($0, "@plugin/")) &&
  /(Type|Reference|Syntax|Range|URI|Eval)?Error: |Unable to assign/ && !seen[$0]++')
if [ -n "$errs" ]; then
  echo "--- QML errors ---"; echo "$errs"
  echo "check: the app's QML failed while running (above): fix each one, then check again." >&2; exit 1
fi

if [ -n "$release" ]; then
  echo "Checked the release (native AOT) build. It must show the same values as the dev build."
else
  look=" Then open obj/shot.png."; [ -n "$offscreen" ] && look=
  echo "Checked the dev build. Read the visible text above: are the values right?$look When it all works, finish with: sh $0 --release"
fi
