import QtQuick
import QtQuick.Layouts

// A whole-window page's title, its subtitle, and the hairline under them.
Rectangle {
  property string title
  property string subtitle
  Layout.fillWidth: true
  implicitHeight: row.implicitHeight + 28 * backend.uiZoom
  color: "transparent"
  Row {
    id: row
    x: 18 * backend.uiZoom
    anchors.verticalCenter: parent.verticalCenter
    spacing: 14 * backend.uiZoom
    Label { text: title; font.bold: true; font.pixelSize: 19 * backend.uiZoom; color: backend.palette.foreground }
    Label { text: subtitle; anchors.verticalCenter: parent.verticalCenter; font.pixelSize: 12.5 * backend.uiZoom; color: backend.palette.foreground; opacity: 0.65 }
  }
  Rectangle { anchors.bottom: parent.bottom; width: parent.width; height: 1; color: Qt.alpha(backend.palette.foreground, 0.12) }
}
