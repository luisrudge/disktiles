#!/bin/sh
# check.sh [options] [APP_DIR]: check an omasharp app without touching the user's screen.
# Runs the unit tests (tests/), builds the app, then runs it in a private, invisible compositor
# (shot.sh): prints every visible text in the window, saves obj/shot.png, and shows the app's
# output. Exits non-zero if anything fails, including errors the app's QML throws while running.
#   --release        check the native AOT release build (dotnet publish); fails on IL2xxx/IL3xxx warnings
#   --keys KEYS      type into the app first: text plus {Tab} {Enter} {Ctrl+a} {Wait:500} ... (keys.py)
#   --args ARGS      command-line arguments for the app (split on whitespace; Bridge.Args)
#   --size WxH       the screen size in logical pixels (default 1280x960)
#   --no-tests       skip the unit tests
#   --offscreen      window apps without a Hyprland session (CI): Qt's offscreen platform, visible
#                    text only (no screenshot, no keys)
set -u
here=$(cd "$(dirname "$0")" && pwd)
release=; keys=; args=; size=; tests=1; offscreen=; dir=.
while [ $# -gt 0 ]; do
  case $1 in
    --release) release=1 ;;
    --keys) keys=$2; shift ;;
    --args) args=$2; shift ;;
    --size) size=$2; shift ;;
    --no-tests) tests= ;;
    --offscreen) offscreen=1 ;;
    -h|--help) sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    -*) echo "check: unknown option $1 (see --help)" >&2; exit 2 ;;
    *) dir=$1 ;;
  esac
  shift
done
cd "$dir" || exit 2
set -- *.csproj
[ $# -eq 1 ] && [ -f "$1" ] || { echo "check: run it in the app's folder (the one with the app's .csproj)" >&2; exit 2; }
obj=$PWD/obj/omasharp; mkdir -p "$obj"

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

# The command that runs this build, one argument per line (written by the build).
set --; while IFS= read -r a; do set -- "$@" "$a"; done < "$obj/launch"
kind=$(cat "$obj/kind")

if [ -n "$offscreen" ]; then
  [ "$kind" = window ] || { echo "check: --offscreen works for window apps only" >&2; exit 2; }
  [ -z "$keys" ] || { echo "check: --offscreen can't type keys" >&2; exit 2; }
  echo "== run (offscreen)"
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
  OMASHARP_SHOT_SIZE=$size OMASHARP_SHOT_KEYS_FILE=$obj/keys OMASHARP_SHOT_ARGS_FILE=$obj/args \
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
