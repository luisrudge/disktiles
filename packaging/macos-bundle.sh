#!/bin/sh
# disktiles.app from the native executable: Spotlight, Launchpad and the Dock
# find it, with its icon. GTK comes from Homebrew (brew install gtk4), which
# the dynamic loader does not search by default on Apple silicon, so the
# bundle's executable is a launcher that points it there.
#
#   packaging/macos-bundle.sh OUT EXE VERSION    writes OUT/disktiles.app
set -eu
out=$1
exe=$2
version=$3
here=$(cd "$(dirname "$0")" && pwd)
app=$out/disktiles.app

rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$exe" "$app/Contents/MacOS/disktiles-bin"
cp "$here/disktiles.icns" "$app/Contents/Resources/disktiles.icns"

cat >"$app/Contents/MacOS/disktiles" <<'EOF'
#!/bin/sh
# Homebrew's GTK, on Apple silicon and on Intel, then disktiles itself. Run
# through a link (~/.local/bin/disktiles) too: the link is resolved first.
self=$(readlink -f "$0" 2>/dev/null || echo "$0")
here=$(dirname "$self")
export DYLD_FALLBACK_LIBRARY_PATH="${DYLD_FALLBACK_LIBRARY_PATH:+$DYLD_FALLBACK_LIBRARY_PATH:}/opt/homebrew/lib:/usr/local/lib:/usr/lib"
# Finder may still pass a process serial number; disktiles has no use for it.
for argument do
    shift
    case $argument in -psn_*) ;; *) set -- "$@" "$argument" ;; esac
done
exec "$here/disktiles-bin" "$@"
EOF
chmod 755 "$app/Contents/MacOS/disktiles" "$app/Contents/MacOS/disktiles-bin"

cat >"$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key><string>io.github.luisrudge.disktiles</string>
    <key>CFBundleName</key><string>disktiles</string>
    <key>CFBundleDisplayName</key><string>disktiles</string>
    <key>CFBundleExecutable</key><string>disktiles</string>
    <key>CFBundleIconFile</key><string>disktiles</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$version</string>
    <key>CFBundleVersion</key><string>$version</string>
    <key>LSMinimumSystemVersion</key><string>13.0</string>
    <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
EOF
plutil -lint "$app/Contents/Info.plist" >/dev/null
echo "wrote $app"
