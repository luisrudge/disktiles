#!/usr/bin/env bash
# disktiles.exe with the GTK it loads, from an MSYS2 UCRT64 install, in the
# layout GTK looks for next to its own DLLs: bin\ (the exe and every DLL),
# share\ (GLib's compiled schemas, the Adwaita and hicolor icons) and lib\
# (gdk-pixbuf's image loaders). Windows has no GTK of its own.
#
#   packaging/windows-bundle.sh OUT EXE    in an MSYS2 UCRT64 shell
set -euo pipefail
out=$1
exe=$2
prefix=${MINGW_PREFIX:-/ucrt64}

rm -rf "$out"
mkdir -p "$out/bin" "$out/lib" "$out/share/glib-2.0" "$out/share/icons"
cp "$exe" "$out/bin/"

# What Gir.Core opens by name, the image loaders, and everything they load
# in turn (ldd lists the whole closure), from this prefix only.
roots=(libgtk-4-1.dll libgio-2.0-0.dll libglib-2.0-0.dll libgobject-2.0-0.dll libgmodule-2.0-0.dll
    libcairo-2.dll libcairo-gobject-2.dll libpango-1.0-0.dll libpangocairo-1.0-0.dll libgdk_pixbuf-2.0-0.dll
    libgraphene-1.0-0.dll libharfbuzz-0.dll libharfbuzz-gobject-0.dll libfreetype-6.dll)
loaders=$prefix/lib/gdk-pixbuf-2.0/2.10.0/loaders
{
    for lib in "${roots[@]}"; do
        echo "$prefix/bin/$lib"
    done
    for file in "${roots[@]/#/$prefix/bin/}" "$loaders"/*.dll; do
        [ -e "$file" ] && ldd "$file" | awk '{ print $3 }'
    done
} | grep -i "^$prefix/bin/" | sort -u | while read -r dll; do
    cp "$dll" "$out/bin/"
done

cp -r "$prefix/lib/gdk-pixbuf-2.0" "$out/lib/"
mkdir -p "$out/share/glib-2.0/schemas"
cp "$prefix/share/glib-2.0/schemas/gschemas.compiled" "$out/share/glib-2.0/schemas/"
cp -r "$prefix/share/icons/Adwaita" "$prefix/share/icons/hicolor" "$out/share/icons/"
echo "bundled $(ls "$out/bin" | wc -l) files in bin, $(du -sh "$out" | cut -f1) in all"
