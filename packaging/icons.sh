#!/bin/sh
# Regenerate the Windows and macOS icons from disktiles.svg (run on a Mac
# with rsvg-convert and ImageMagick: brew install librsvg imagemagick).
# The results are committed, so a release needs none of these tools.
#
#   packaging/icons.sh
set -eu
here=$(cd "$(dirname "$0")" && pwd)
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# Windows: one .ico holding every size Explorer and the taskbar ask for.
for size in 16 24 32 48 64 128 256; do
    rsvg-convert -w $size -h $size "$here/disktiles.svg" -o "$work/$size.png"
done
magick "$work/16.png" "$work/24.png" "$work/32.png" "$work/48.png" "$work/64.png" "$work/128.png" "$work/256.png" \
    "$here/disktiles.ico"

# macOS: an iconset, each size at 1x and 2x, packed by iconutil.
set_dir=$work/disktiles.iconset
mkdir "$set_dir"
for size in 16 32 128 256 512; do
    rsvg-convert -w $size -h $size "$here/disktiles.svg" -o "$set_dir/icon_${size}x${size}.png"
    rsvg-convert -w $((size * 2)) -h $((size * 2)) "$here/disktiles.svg" -o "$set_dir/icon_${size}x${size}@2x.png"
done
iconutil -c icns "$set_dir" -o "$here/disktiles.icns"
echo "wrote $here/disktiles.ico and $here/disktiles.icns"
