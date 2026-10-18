import QtQuick

// Text in the Omarchy font. The app doesn't load QtQuick.Controls (its Label would be
// this plus a style): that import costs about 20 ms at every start.
Text {
  font.family: "monospace"
  color: backend.palette.foreground
}
