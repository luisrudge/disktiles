import QtQuick
import QtQuick.Layouts

// The review: everything marked, what will happen to it, and the one
// commitment. m trash · p permanent · ! unmark all · s save list · a copy as
// prompt · enter commits · esc goes back (the keys are backend's).
Rectangle {
  id: review
  readonly property var p: backend.palette
  readonly property real zoom: backend.uiZoom
  readonly property var r: backend.review
  color: p.background

  ColumnLayout {
    anchors.fill: parent
    spacing: 0

    ScreenHeader { title: "Review"; subtitle: review.r.subtitle }

    RowLayout {
      Layout.fillWidth: true
      Layout.fillHeight: true
      Layout.margins: 18 * review.zoom
      spacing: 18 * review.zoom

      // The marked list; fixed lanes, so sizes line up down the list.
      Rectangle {
        Layout.fillWidth: true
        Layout.fillHeight: true
        color: review.p.inset
        border.color: Qt.alpha(review.p.foreground, 0.12)
        Scroller { target: list; z: 1 }
        ListView {
          id: list
          anchors.fill: parent
          anchors.margins: 1
          clip: true
          boundsBehavior: Flickable.StopAtBounds
          model: review.r.rows
          header: Label {
            visible: review.r.rows.length === 0
            height: visible ? implicitHeight + 48 * review.zoom : 0
            leftPadding: 24 * review.zoom; topPadding: 24 * review.zoom
            text: "Nothing is marked. Go back and mark what should go."
            color: review.p.foreground; opacity: 0.65
          }
          footer: Label {
            visible: review.r.more.length > 0
            height: visible ? implicitHeight + 16 : 0
            padding: 12 * review.zoom
            width: list.width; wrapMode: Text.Wrap
            text: review.r.more; color: review.p.foreground; opacity: 0.65
          }
          delegate: Rectangle {
            required property var modelData
            width: list.width
            height: rowContent.implicitHeight + 16 * review.zoom
            color: "transparent"
            Rectangle { anchors.bottom: parent.bottom; width: parent.width; height: 1; color: Qt.alpha(review.p.foreground, 0.06) }
            RowLayout {
              id: rowContent
              anchors.verticalCenter: parent.verticalCenter
              x: 12 * review.zoom
              width: parent.width - 24 * review.zoom
              spacing: 12 * review.zoom
              Label { text: modelData.isDir ? "▸" : "·"; Layout.preferredWidth: 14 * review.zoom; color: review.p.foreground; opacity: 0.65 }
              Column {
                Layout.fillWidth: true
                spacing: 2
                Label {
                  width: parent.width; elide: Text.ElideRight
                  text: modelData.name
                  font.bold: !(modelData.covered || modelData.blocked.length > 0)
                  font.pixelSize: 13 * review.zoom
                  color: review.p.foreground
                  opacity: modelData.covered || modelData.blocked.length > 0 ? 0.55 : 1
                }
                Label { width: parent.width; elide: Text.ElideMiddle; text: modelData.tilde; font.pixelSize: 11.5 * review.zoom; color: review.p.foreground; opacity: 0.65 }
              }
              Chip { visible: modelData.covered; text: "Inside a marked directory"; tint: review.p.secondary }
              Chip { visible: modelData.blocked.length > 0; text: modelData.blocked; tint: review.p.warning; Layout.maximumWidth: 280 * review.zoom }
              Bar { visible: list.width > 520 * review.zoom; Layout.preferredWidth: 80 * review.zoom; value: modelData.fraction }
              Label { Layout.preferredWidth: 76 * review.zoom; horizontalAlignment: Text.AlignRight; text: modelData.size; font.bold: true; font.pixelSize: 14 * review.zoom; color: review.p.foreground }
              Btn { text: "Unmark"; onClicked: backend.unmark(modelData.path) }
            }
          }
        }
      }

      // What happens, the totals, the volume, what is kept back, the actions.
      Rectangle {
        Layout.preferredWidth: Math.min(380 * review.zoom, review.width * 0.4)
        Layout.fillHeight: true
        color: review.p.surface
        border.color: Qt.alpha(review.p.foreground, 0.12)
        ColumnLayout {
          anchors.fill: parent
          anchors.margins: 18 * review.zoom
          spacing: 18 * review.zoom

          Column {
            Layout.fillWidth: true
            spacing: 8 * review.zoom
            Caps { text: "What happens" }
            // One either-or choice, the reversible option first.
            Row {
              width: parent.width
              Btn {
                width: parent.width / 2; text: "Move to trash"
                active: review.r.trashAvailable
                kind: review.r.mode === "Trash" ? "checked" : ""
                onClicked: backend.useTrash(true)
              }
              Btn {
                width: parent.width / 2; text: "Delete permanently"
                kind: review.r.mode === "Permanent" ? "checked" : ""
                onClicked: backend.useTrash(false)
              }
            }
            Label { width: parent.width; wrapMode: Text.Wrap; text: review.r.explanation; font.pixelSize: 12 * review.zoom; color: review.p.foreground; opacity: 0.65 }
          }

          Column {
            Layout.fillWidth: true
            spacing: 4 * review.zoom
            Caps { text: "Totals"; bottomPadding: 4 * review.zoom }
            Repeater {
              model: review.r.totals
              Row {
                required property var modelData
                width: parent.width
                Label { width: parent.width - value.width; text: modelData.label; font.pixelSize: 12.5 * review.zoom; color: review.p.foreground; opacity: 0.65 }
                Label { id: value; text: modelData.value; font.bold: true; font.pixelSize: 13.5 * review.zoom; color: review.p.foreground }
              }
            }
          }

          Column {
            visible: review.r.volume !== null && review.r.volume !== undefined
            Layout.fillWidth: true
            spacing: 6 * review.zoom
            Caps { text: "Volume" }
            Label { text: parent.visible ? review.r.volume.text : ""; font.bold: true; font.pixelSize: 13.5 * review.zoom; color: review.p.foreground }
            Bar { width: parent.width; value: parent.visible ? review.r.volume.used : 0; fill: Qt.alpha(review.p.foreground, 0.22) }
          }

          Column {
            visible: review.r.kept.length > 0
            Layout.fillWidth: true
            spacing: 4 * review.zoom
            Caps { text: "Kept back" }
            Repeater {
              model: review.r.kept
              Label { required property string modelData; width: parent.width; wrapMode: Text.Wrap; text: modelData; font.pixelSize: 12 * review.zoom; color: review.p.warning }
            }
          }

          Item { Layout.fillHeight: true }
          Notice { Layout.fillWidth: true; notice: review.r.notice }

          // The list handed on instead of acted on.
          Row {
            Layout.alignment: Qt.AlignRight
            spacing: 8 * review.zoom
            Btn { text: "Save list…"; active: review.r.canExport; onClicked: backend.saveList() }
            Btn { text: "Copy as prompt"; active: review.r.canExport; onClicked: backend.copyPrompt() }
          }
          // The trash is the primary action; a permanent deletion is the
          // danger action and asks first, as its ellipsis says.
          Row {
            Layout.alignment: Qt.AlignRight
            spacing: 8 * review.zoom
            Btn { text: "Back"; onClicked: backend.explore() }
            Btn {
              text: review.r.commitLabel
              kind: review.r.destructive ? "destructive" : "suggested"
              active: review.r.canCommit
              onClicked: backend.commit()
            }
          }
        }
      }
    }

    ScreenFooter {
      hints: [["enter", review.r.commitHint], ["m", "trash"], ["p", "permanent"], ["!", "unmark all"], ["s", "save list"], ["a", "copy as prompt"], ["esc", "back"]]
      end: review.r.available
    }
  }

  // The permanent-delete confirmation, over the page: Enter deletes, Escape cancels.
  Rectangle {
    readonly property var d: backend.dialog
    anchors.fill: parent
    visible: d.visible
    color: Qt.alpha(review.p.background, 0.7)
    MouseArea { anchors.fill: parent }
    Rectangle {
      anchors.centerIn: parent
      width: Math.min(parent.width - 40, 440 * review.zoom)
      height: dialog.implicitHeight + 40 * review.zoom
      color: review.p.background
      border.color: review.p.border
      Column {
        id: dialog
        x: 20 * review.zoom; y: 20 * review.zoom
        width: parent.width - 40 * review.zoom
        spacing: 12 * review.zoom
        Label { width: parent.width; wrapMode: Text.Wrap; text: parent.parent.parent.d.title; font.bold: true; font.pixelSize: 16 * review.zoom; color: review.p.foreground }
        Label { width: parent.width; wrapMode: Text.Wrap; text: parent.parent.parent.d.body; font.pixelSize: 12.5 * review.zoom; color: review.p.foreground; opacity: 0.65 }
        Row {
          anchors.right: parent.right
          spacing: 8 * review.zoom
          Btn { text: "Cancel"; onClicked: backend.closeDialog() }
          Btn { text: "Delete"; kind: "destructive"; onClicked: backend.confirm() }
        }
      }
    }
  }
}
