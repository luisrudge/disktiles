import QtQuick

// A keycap, then what it does.
Row {
  property string cap
  property string label
  spacing: 5 * backend.uiZoom
  Keycap { text: cap; anchors.verticalCenter: parent.verticalCenter }
  Label {
    text: label
    anchors.verticalCenter: parent.verticalCenter
    font.pixelSize: 12 * backend.uiZoom
    color: backend.palette.secondary
  }
}
