#!/bin/sh
# update.sh [APP_DIR]: bring the bridge (bridge/) and the Omasharp skill (.agents/skills/omasharp/)
# from the installed omarchy-app template into an app (shadcn-style: they're plain files you own).
# `dotnet build -t:UpdateBridge` runs it.
#   - files you never changed: replaced with the new version
#   - files you changed: kept as they are; the new version is saved next to them as
#     <file>.upstream and the diff is printed. Move your change into the new code (or the new
#     code into yours), then delete the .upstream file.
#   - files added upstream: copied in; files removed upstream: deleted if you never changed them
# "Changed" means its content differs from bridge/MANIFEST, the fingerprints of what this app
# last received (paths relative to the app folder).
set -eu
# Run from a copy: this very file may be replaced below.
if [ -z "${OMASHARP_UPDATE_COPY:-}" ]; then
  tmp=$(mktemp /tmp/omasharp-update.XXXXXX); cp "$0" "$tmp"
  OMASHARP_UPDATE_COPY=1 exec sh "$tmp" "$@"
fi
app=$(cd "${1:-.}" && pwd); manifest="$app/bridge/MANIFEST"
[ -f "$manifest" ] || { echo "update: $manifest is missing (was this app made from the omarchy-app template?)" >&2; exit 1; }
work=$(mktemp -d /tmp/omasharp-update.XXXXXX); trap 'rm -rf "$work" "$0"' EXIT
( cd "$work" && dotnet new omarchy-app -n Tmp -o app ${OMASHARP_TEMPLATE_HIVE:+--debug:custom-hive "$OMASHARP_TEMPLATE_HIVE"} >/dev/null ) || { echo "update: 'dotnet new omarchy-app' failed; is the template installed?" >&2; exit 1; }
new="$work/app"
old_version=$(cat "$app/bridge/VERSION" 2>/dev/null || echo "?"); new_version=$(cat "$new/bridge/VERSION")
echo "omasharp $old_version -> $new_version"
base_hash() { awk -v f="$1" '$2 == f { print $1 }' "$manifest"; }
hash() { sha256sum "$1" | cut -d' ' -f1; }
updated=0; added=0; conflicts=0; removed=0; same=0
cd "$new"
for f in $(find bridge .agents/skills/omasharp -type f ! -name MANIFEST | sort); do
  if [ ! -e "$app/$f" ]; then
    mkdir -p "$(dirname "$app/$f")"; cp -p "$f" "$app/$f"; echo "  added    $f"; added=$((added+1))
  elif [ "$(hash "$f")" = "$(hash "$app/$f")" ]; then
    same=$((same+1))
  elif [ "$(hash "$app/$f")" = "$(base_hash "$f")" ]; then
    cp -p "$f" "$app/$f"; echo "  updated  $f"; updated=$((updated+1))
  elif [ "$(hash "$f")" = "$(base_hash "$f")" ]; then
    echo "  kept     $f (your customization; unchanged upstream)"; same=$((same+1))
  else
    cp -p "$f" "$app/$f.upstream"; conflicts=$((conflicts+1))
    echo "  CHANGED  $f: you customized it; the new version is in $f.upstream"
    if command -v diff >/dev/null; then diff -u "$app/$f" "$app/$f.upstream" | sed 's/^/    /' || true
    else echo "    (install diffutils to see the diff)"; fi
  fi
done
# Files this app received before that aren't upstream any more.
while read -r h f; do
  [ -e "$new/$f" ] || [ ! -e "$app/$f" ] && continue
  if [ "$(hash "$app/$f")" = "$h" ]; then rm -f "$app/$f"; echo "  removed  $f"; removed=$((removed+1))
  else echo "  CHANGED  $f: removed upstream, but you customized it; kept"; conflicts=$((conflicts+1)); fi
done < "$manifest"
cp -p "$new/bridge/MANIFEST" "$manifest"
echo "omasharp $new_version: $updated updated, $added added, $removed removed, $same unchanged, $conflicts to merge by hand"
[ "$conflicts" -eq 0 ] || echo "Merge each <file>.upstream into <file> (keep your customization), then delete the .upstream files."
