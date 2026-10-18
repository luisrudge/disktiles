import QtQuick
import QtQuick.Layouts

// A page's key hints ([key, what] pairs), and a quiet line at the right.
Rectangle {
  property var hints: []
  property string end: ""
  default property alias leading: lead.data
  Layout.fillWidth: true
  implicitHeight: row.implicitHeight + 16 * backend.uiZoom
  color: "transparent"
  Rectangle { width: parent.width; height: 1; color: Qt.alpha(backend.palette.foreground, 0.12) }
  RowLayout {
    id: row
    x: 18 * backend.uiZoom
    width: parent.width - 36 * backend.uiZoom
    anchors.verticalCenter: parent.verticalCenter
    spacing: 18 * backend.uiZoom
    Row { id: lead; spacing: 8 * backend.uiZoom }
    Repeater {
      model: hints
      Hint { required property var modelData; cap: modelData[0]; label: modelData[1] }
    }
    Item { Layout.fillWidth: true }
    Label { text: end; font.pixelSize: 12 * backend.uiZoom; color: backend.palette.foreground; opacity: 0.65 }
  }
}
