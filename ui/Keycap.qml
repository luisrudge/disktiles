import QtQuick

// A key, as the key-hint bar and the review footer show it.
Rectangle {
  property alias text: cap.text
  implicitWidth: cap.implicitWidth + 8 * backend.uiZoom
  implicitHeight: cap.implicitHeight + 2 * backend.uiZoom
  color: backend.palette.inset
  border.color: backend.palette.border
  border.width: 1
  Label {
    id: cap
    anchors.centerIn: parent
    font.pixelSize: 11 * backend.uiZoom
    font.bold: true
    color: backend.palette.foreground
  }
}
