import QtQuick

// A thin share bar: `value` of 0..1 filled in `fill`.
Rectangle {
  property real value: 0
  property color fill: backend.palette.accent
  implicitHeight: 6 * backend.uiZoom
  color: Qt.alpha(backend.palette.foreground, 0.08)
  Rectangle {
    width: parent.width * Math.max(0, Math.min(1, parent.value))
    height: parent.height
    color: parent.fill
  }
}
