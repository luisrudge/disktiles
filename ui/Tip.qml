import QtQuick

// A tooltip under its parent, shown after half a second of hovering (`shown`).
Rectangle {
  property string text
  property bool shown: false
  visible: opened && text.length > 0
  property bool opened: false
  Timer { interval: 500; running: parent.shown; onTriggered: parent.opened = true }
  onShownChanged: if (!shown) opened = false
  z: 100
  y: parent.height + 4
  width: tipText.implicitWidth + 12
  height: tipText.implicitHeight + 8
  color: backend.palette.background
  border.color: Qt.alpha(backend.palette.foreground, 0.4)
  Label { id: tipText; x: 6; y: 4; text: parent.text; font.pixelSize: 12 * backend.uiZoom }
}
