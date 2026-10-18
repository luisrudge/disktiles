import QtQuick

// A section caption: small, spaced, upper case, dim.
Label {
  property real zoom: backend.uiZoom
  font.pixelSize: 10.5 * zoom
  font.letterSpacing: 1
  font.capitalization: Font.AllUppercase
  color: backend.palette.foreground
  opacity: 0.65
  elide: Text.ElideRight
}
