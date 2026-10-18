import QtQuick

// The last thing marking said, framed in its tone's colour.
Rectangle {
  id: box
  property var notice: null
  readonly property var p: backend.palette
  readonly property color tint: !notice ? p.secondary
    : notice.status === "success" ? p.success : notice.status === "warning" ? p.warning
    : notice.status === "error" ? p.danger : p.secondary
  visible: notice !== null && notice !== undefined
  implicitHeight: visible ? text.implicitHeight + 8 * backend.uiZoom : 0
  color: "transparent"
  border.color: Qt.alpha(tint, 0.5)
  Label {
    id: text
    x: 8 * backend.uiZoom; y: 4 * backend.uiZoom
    width: box.width - 16 * backend.uiZoom
    wrapMode: Text.Wrap
    text: box.notice ? box.notice.text : ""
    font.pixelSize: 12 * backend.uiZoom
    color: box.tint
  }
}
