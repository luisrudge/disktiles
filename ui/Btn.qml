import QtQuick

// A flat, square button as Omarchy's shell draws them. `kind`: "" plain,
// "flat" (no frame), "suggested" (the highlight: what Enter does),
// "destructive" (the one that cannot be undone), "checked" (a toggle that is on).
// Never takes the keyboard: the keys stay the treemap's.
Rectangle {
  id: button
  property string text
  property string kind: ""
  property bool active: true
  property real zoom: backend.uiZoom
  property real fontSize: 12.5
  property string tip: ""
  signal clicked()
  readonly property var p: backend.palette
  implicitWidth: label.implicitWidth + 20 * zoom
  implicitHeight: label.implicitHeight + 10 * zoom
  opacity: active ? 1 : 0.4
  color: kind === "suggested" ? (mouse.containsMouse ? Qt.alpha(p.highlight, 0.85) : p.highlight)
       : kind === "destructive" ? Qt.alpha(p.danger, mouse.containsMouse ? 0.2 : 0.12)
       : kind === "flat" ? (mouse.containsMouse && active ? Qt.alpha(p.foreground, 0.08) : "transparent")
       : Qt.alpha(p.foreground, kind === "checked" ? 0.18 : mouse.containsMouse && active ? 0.08 : 0.04)
  border.width: kind === "flat" ? 0 : 1
  border.color: kind === "suggested" ? p.highlight : kind === "destructive" ? Qt.alpha(p.danger, 0.6) : Qt.alpha(p.foreground, 0.4)
  Label {
    id: label
    anchors.centerIn: parent
    text: button.text
    font.pixelSize: button.fontSize * button.zoom
    font.bold: button.kind === "suggested"
    color: button.kind === "suggested" ? button.p.onHighlight : button.kind === "destructive" ? button.p.danger : button.p.foreground
  }
  Tip { text: button.tip; shown: mouse.containsMouse }
  MouseArea {
    id: mouse
    anchors.fill: parent
    hoverEnabled: true
    cursorShape: button.active ? Qt.PointingHandCursor : Qt.ArrowCursor
    onClicked: if (button.active) button.clicked()
  }
}
