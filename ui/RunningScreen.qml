import QtQuick
import QtQuick.Layouts

// The removal, as it goes: how far, and each item's outcome, newest first.
Rectangle {
  id: running
  readonly property var p: backend.palette
  readonly property real zoom: backend.uiZoom
  readonly property var r: backend.runningScreen
  color: p.background
  ColumnLayout {
    anchors.fill: parent
    spacing: 0
    ScreenHeader { title: "Removing"; subtitle: running.r.subtitle }
    ColumnLayout {
      Layout.fillWidth: true
      Layout.fillHeight: true
      Layout.margins: 18 * running.zoom
      spacing: 12 * running.zoom
      Caps { text: "Progress" }
      Label { text: running.r.progress; font.bold: true; font.pixelSize: 15 * running.zoom; color: running.p.foreground }
      Bar { Layout.fillWidth: true; value: running.r.fraction; fill: running.r.danger ? running.p.danger : running.p.accent }
      Rectangle {
        Layout.fillWidth: true
        Layout.fillHeight: true
        color: running.p.inset
        border.color: Qt.alpha(running.p.foreground, 0.12)
        ListView {
          anchors.fill: parent
          anchors.margins: 1
          clip: true
          model: running.r.log
          delegate: Row {
            required property var modelData
            leftPadding: 12 * running.zoom; topPadding: 3 * running.zoom; bottomPadding: 3 * running.zoom
            spacing: 8 * running.zoom
            Label { text: modelData.ok ? "✓" : "✗"; color: modelData.ok ? running.p.success : running.p.danger }
            Label { text: modelData.name; font.pixelSize: 12 * running.zoom; color: modelData.ok ? running.p.foreground : running.p.danger }
            Label { visible: !modelData.ok; text: modelData.error; font.pixelSize: 12 * running.zoom; color: running.p.warning }
          }
        }
      }
    }
    ScreenFooter { hints: [["esc", "stop after the current item"]] }
  }
}
