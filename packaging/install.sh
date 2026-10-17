#!/bin/sh
# Install disktiles for this user on Linux or macOS. No sudo; nothing else is
# touched. (Windows: install.ps1.)
#
#   curl -fsSL https://github.com/luisrudge/disktiles/releases/latest/download/install.sh | sh
#   curl -fsSL …/install.sh | sh -s uninstall      remove it again
#
# Linux: the executable in ~/.local/bin, a launcher entry and an icon under
# ~/.local/share. macOS: disktiles.app in ~/Applications and the `disktiles`
# command in ~/.local/bin; GTK comes from Homebrew (brew install gtk4).
#
# The download is checked against the release's SHA256SUMS. Next to an
# unpacked release, or with DISKTILES_FROM=dir (a folder holding `disktiles`,
# or on macOS `disktiles.app`), it installs from there instead.
# DISKTILES_VERSION=v0.1.0 picks a release; DISKTILES_URL=… downloads the
# assets from somewhere else (a mirror, or file:// for a test).
set -eu

id=io.github.luisrudge.disktiles
repo=luisrudge/disktiles
bin_dir=${XDG_BIN_HOME:-$HOME/.local/bin}
exe=$bin_dir/disktiles
data=${XDG_DATA_HOME:-$HOME/.local/share}
apps=$data/applications
icons=$data/icons/hicolor
entry=$apps/$id.desktop
icon=$icons/scalable/apps/$id.svg
app=$HOME/Applications/disktiles.app

die() { echo "$*" >&2; exit 1; }

case $(uname -s) in
    Linux) platform=linux ;;
    Darwin) platform=osx ;;
    *) die "this script is for Linux and macOS; on Windows, use install.ps1 (see the README)" ;;
esac

refresh() {
    command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$apps" 2>/dev/null || true
    command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q "$icons" 2>/dev/null || true
}

if [ "${1:-}" = uninstall ]; then
    if [ $platform = osx ]; then
        rm -rf "$app"
        [ -L "$exe" ] && rm -f "$exe"
        echo "removed $app and $exe"
    else
        rm -f "$exe" "$entry" "$icon"
        refresh
        echo "removed $exe, $entry and $icon"
    fi
    exit 0
fi

# Where the files come from: DISKTILES_FROM, this script's own folder, or a
# download. Piped into sh, $0 is the shell, not a file: then only a download.
payload=disktiles
[ $platform = osx ] && payload=disktiles.app
here=
[ -f "$0" ] && here=$(cd "$(dirname "$0")" && pwd)
from=${DISKTILES_FROM:-}
if [ -z "$from" ] && [ -n "$here" ] && [ -e "$here/$payload" ]; then
    from=$here
fi
if [ -z "$from" ]; then
    case $(uname -m) in
        x86_64) arch=x64 ;;
        aarch64 | arm64) arch=arm64 ;;
        *) die "no disktiles release for $(uname -m)" ;;
    esac
    [ "$platform-$arch" = osx-x64 ] && die "no release for Intel Macs yet; build from source (see the README)"
    asset=disktiles-$platform-$arch.tar.gz
    base=https://github.com/$repo/releases/latest/download
    [ -n "${DISKTILES_VERSION:-}" ] && base=https://github.com/$repo/releases/download/$DISKTILES_VERSION
    base=${DISKTILES_URL:-$base}
    tmp=$(mktemp -d)
    trap 'rm -rf "$tmp"' EXIT
    echo "downloading $base/$asset"
    curl -fsSL -o "$tmp/$asset" "$base/$asset"
    curl -fsSL -o "$tmp/SHA256SUMS" "$base/SHA256SUMS"
    expected=$(grep " $asset\$" "$tmp/SHA256SUMS" | cut -d' ' -f1 || true)
    if command -v sha256sum >/dev/null 2>&1; then
        actual=$(sha256sum "$tmp/$asset" | cut -d' ' -f1)
    else
        actual=$(shasum -a 256 "$tmp/$asset" | cut -d' ' -f1)
    fi
    [ -n "$expected" ] && [ "$actual" = "$expected" ] || die "$asset does not match the release's SHA256SUMS; nothing was installed"
    mkdir "$tmp/unpacked"
    tar -xzf "$tmp/$asset" -C "$tmp/unpacked"
    from=$tmp/unpacked
fi
[ -e "$from/$payload" ] || die "no $payload in $from"
mkdir -p "$bin_dir"

if [ $platform = osx ]; then
    [ -e /opt/homebrew/lib/libgtk-4.1.dylib ] || [ -e /usr/local/lib/libgtk-4.1.dylib ] \
        || echo "note: GTK 4 is not installed; disktiles needs it: brew install gtk4" >&2
    run=$app/Contents/MacOS/disktiles
    # --version exits before GTK loads, so this works without it.
    old=$("$run" --version 2>/dev/null || true)
    mkdir -p "$HOME/Applications"
    rm -rf "$app.new"
    cp -R "$from/disktiles.app" "$app.new"
    # A browser's quarantine mark would make Gatekeeper stop it: the user
    # chose to install it by running this.
    xattr -dr com.apple.quarantine "$app.new" 2>/dev/null || true
    rm -rf "$app"
    mv "$app.new" "$app"
    ln -sf "$run" "$exe"
else
    run=$exe
    old=$("$run" --version 2>/dev/null || true)
    mkdir -p "$apps" "$(dirname "$icon")"
    # Copied to a new name, then moved: a running disktiles keeps its old file.
    cp "$from/disktiles" "$exe.new" && chmod 755 "$exe.new" && mv -f "$exe.new" "$exe"
    svg=$from/disktiles.svg
    [ -f "$svg" ] || svg=$from/../packaging/disktiles.svg
    [ -f "$svg" ] && cp "$svg" "$icon" && chmod 644 "$icon"
    # The entry's file name is the Wayland app id, so Hyprland's window
    # class, the portal and the launcher all find it. Version= is the Desktop
    # Entry spec's version, not disktiles'. Exec's path is quoted, as the
    # spec asks; no MimeType, so folders still open in the file manager.
    quoted=$(printf '%s' "$exe" | sed 's/[\\"`$]/\\&/g')
    cat >"$entry" <<EOF
[Desktop Entry]
Type=Application
Version=1.0
Name=disktiles
GenericName=Disk space treemap
Comment=See what is using your disk, mark what should go, remove it
Exec="$quoted" %f
Icon=$id
Terminal=false
Categories=System;Filesystem;
Keywords=disk;space;usage;du;treemap;cleanup;
StartupNotify=true
StartupWMClass=$id
Actions=disk;

[Desktop Action disk]
Name=Scan the whole disk
Exec="$quoted" --disk
EOF
    chmod 644 "$entry"
    refresh
fi

new=$("$run" --version 2>/dev/null || echo "disktiles (version unknown)")
if [ -n "$old" ] && [ "$old" != "$new" ]; then
    echo "updated $old -> ${new#disktiles }: $exe"
else
    echo "installed $new: $exe"
fi
case ":$PATH:" in *":$bin_dir:"*) ;; *) echo "note: $bin_dir is not on your PATH" ;; esac
if [ $platform = osx ]; then
    echo "open disktiles from Launchpad or Spotlight, or run: disktiles [FOLDER]"
else
    echo "open it from the app launcher, or run: disktiles [FOLDER]"
fi
