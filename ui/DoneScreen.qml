import QtQuick
import QtQuick.Layouts

// After a removal: what went, what it claimed, what the volume really
// gained (measured, not summed), and what could not be removed.
Rectangle {
  id: done
  readonly property var p: backend.palette
  readonly property real zoom: backend.uiZoom
  readonly property var d: backend.doneScreen
  color: p.background
  ColumnLayout {
    anchors.fill: parent
    spacing: 0
    ScreenHeader { title: "Done"; subtitle: "removal finished" }
    ColumnLayout {
      Layout.fillWidth: true
      Layout.fillHeight: true
      Layout.margins: 18 * done.zoom
      spacing: 12 * done.zoom
      Row {
        spacing: 48 * done.zoom
        Repeater {
          model: done.d.stats
          Column {
            required property var modelData
            spacing: 4 * done.zoom
            Caps { text: modelData.label }
            Label {
              text: modelData.value; font.bold: true; font.pixelSize: 34 * done.zoom
              color: modelData.style === "success" ? done.p.success : modelData.style === "danger" ? done.p.danger
                   : modelData.style === "warning" ? done.p.warning : done.p.foreground
            }
          }
        }
      }
      Label { text: "The treemap is being re-scanned so the numbers on screen match the disk again."; font.pixelSize: 12.5 * done.zoom; color: done.p.foreground; opacity: 0.65 }
      Column {
        visible: done.d.volume !== null && done.d.volume !== undefined
        Layout.fillWidth: true
        spacing: 6 * done.zoom
        Label { text: parent.visible ? done.d.volume.text : ""; font.bold: true; font.pixelSize: 14 * done.zoom; color: done.p.foreground }
        Bar { width: parent.width; value: parent.visible ? done.d.volume.used : 0; fill: Qt.alpha(done.p.foreground, 0.22) }
      }
      Label { visible: done.d.rescanning; text: "re-scanning…"; font.bold: true; color: done.p.highlight }
      Caps { visible: done.d.failures.length > 0; text: "What could not be removed"; topPadding: 10 * done.zoom }
      Repeater {
        model: done.d.failures
        Row {
          required property var modelData
          spacing: 8 * done.zoom
          Label { text: modelData.name; color: done.p.danger }
          Label { text: modelData.error; color: done.p.foreground; opacity: 0.65 }
        }
      }
      Item { Layout.fillHeight: true }
    }
    ScreenFooter {
      hints: [["enter", "continue"]]
      Btn { text: "Done"; kind: "suggested"; onClicked: backend.explore() }
    }
  }
}
