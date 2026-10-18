import QtQuick

// A small framed badge in one colour.
Rectangle {
  property alias text: chip.text
  property color tint: backend.palette.danger
  implicitWidth: Math.min(chip.implicitWidth, 260 * backend.uiZoom) + 12 * backend.uiZoom
  implicitHeight: chip.implicitHeight + 2 * backend.uiZoom
  color: "transparent"
  border.color: Qt.alpha(tint, 0.6)
  radius: 3
  Label {
    id: chip
    anchors.centerIn: parent
    width: Math.min(implicitWidth, 260 * backend.uiZoom)
    elide: Text.ElideRight
    font.pixelSize: 10.5 * backend.uiZoom
    color: parent.tint
  }
}
