#!/bin/sh
# desktop.sh: the desktop entry part of `dotnet publish -t:Package` (and so of -t:Install; window apps).
#   desktop.sh SRC OUT EXEC APPID ICONS   write the app's own entry (app.desktop) to OUT: @EXEC@
#       becomes EXEC, quoted, and [Desktop Entry] gets StartupWMClass=APPID, plus Icon=APPID
#       when ICONS isn't 0 (the app ships icon.svg or icon.png), replacing the ones already there.
#       desktop-file-validate checks it first, when it's installed: an invalid entry leaves OUT as it was.
#       Package passes EXEC=@EXEC@, and install.sh puts the installed binary there.
set -eu
src=$1; out=$2; exe=$3; id=$4; icons=$5
case $exe in *[\"\`\$\\]*) echo "desktop: can't write '$exe' into Exec" >&2; exit 1 ;; esac
# Written and checked in a folder of its own, under OUT's name (the validator checks the name too).
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT; new=$tmp/$(basename "$out")
EXE="\"$exe\"" ID=$id ICON=$([ "$icons" = 0 ] && echo 0 || echo 1) awk '
  function exec_path(s,   i, r) { r = ""; while ((i = index(s, "@EXEC@")) > 0) { r = r substr(s, 1, i - 1) ENVIRON["EXE"]; s = substr(s, i + 6) } return r s }
  /^\[/ { group = $0; print
          if (group == "[Desktop Entry]") { print "StartupWMClass=" ENVIRON["ID"]; if (ENVIRON["ICON"] == 1) print "Icon=" ENVIRON["ID"] }
          next }
  group == "[Desktop Entry]" && /^StartupWMClass[ \t]*=/ { next }
  group == "[Desktop Entry]" && ENVIRON["ICON"] == 1 && /^Icon[ \t]*=/ { next }
  { print exec_path($0) }' "$src" > "$new"
if command -v desktop-file-validate >/dev/null; then
  status=0; report=$(desktop-file-validate "$new" 2>&1) || status=$?
  # Its messages name the file it checked: point them at the app's own.
  [ -z "$report" ] || printf '%s\n' "$report" | NEW=$new SRC=$src awk '{ if (index($0, ENVIRON["NEW"]) == 1) $0 = ENVIRON["SRC"] substr($0, length(ENVIRON["NEW"]) + 1); print }'
  [ "$status" -eq 0 ] || { echo "$src: error: not a valid desktop entry (above)" >&2; exit 1; }
fi
mkdir -p "$(dirname "$out")"
mv -f "$new" "$out"
