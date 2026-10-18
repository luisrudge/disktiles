import QtQuick
import QtQuick.Layouts

// The side panel: the selection, "Worth a look", the marked list and the
// notice, which scroll; the disk, pinned below, with the free-after-marks
// projection and the way to the review.
Rectangle {
  id: panel
  readonly property var p: backend.palette
  readonly property real zoom: backend.uiZoom
  color: p.surface

  ColumnLayout {
    anchors.fill: parent
    spacing: 0

    Flickable {
      id: panelFlick
      Layout.fillWidth: true
      Layout.fillHeight: true
      contentHeight: sections.implicitHeight + 36 * panel.zoom
      clip: true
      boundsBehavior: Flickable.StopAtBounds

      Column {
        id: sections
        x: 18 * panel.zoom; y: 18 * panel.zoom
        width: panel.width - 36 * panel.zoom
        spacing: 6 * panel.zoom

        // ── selection ──
        readonly property var s: backend.selection
        Caps { text: "Selection" }
        Row {
          visible: sections.s.visible
          width: parent.width
          spacing: 10 * panel.zoom
          Rectangle { width: 5 * panel.zoom; height: 26 * panel.zoom; color: sections.s.color; anchors.verticalCenter: parent.verticalCenter }
          Label {
            width: parent.width - 20 * panel.zoom
            text: sections.s.name; elide: Text.ElideRight
            font.bold: true; font.pixelSize: 19 * panel.zoom; color: panel.p.foreground
          }
        }
        Label { visible: sections.s.visible; width: parent.width; text: sections.s.path; elide: Text.ElideMiddle; font.pixelSize: 12 * panel.zoom; color: panel.p.foreground; opacity: 0.65 }
        Row {
          visible: sections.s.visible
          spacing: 8 * panel.zoom
          topPadding: 6 * panel.zoom
          Label { id: bigSize; text: sections.s.size; font.bold: true; font.pixelSize: 34 * panel.zoom; color: panel.p.foreground }
          Label { text: sections.s.unit; anchors.baseline: bigSize.baseline; font.pixelSize: 16 * panel.zoom; color: panel.p.foreground; opacity: 0.8 }
        }
        Bar { visible: sections.s.visible; width: parent.width; value: sections.s.share; fill: sections.s.color }
        Grid {
          visible: sections.s.visible
          width: parent.width
          columns: 2
          rowSpacing: 4 * panel.zoom
          topPadding: 10 * panel.zoom
          Caps { width: sections.width / 2; text: "Of scan" }
          Caps { width: sections.width / 2; text: "Files" }
          Fact { text: sections.s.ofScan }
          Fact { text: sections.s.files }
          Caps { width: sections.width / 2; text: "Last write"; topPadding: 10 * panel.zoom }
          Caps { width: sections.width / 2; text: "Kind"; topPadding: 10 * panel.zoom }
          Fact { text: sections.s.lastWrite }
          Fact { text: sections.s.kind }
        }
        Label { visible: sections.s.visible && text.length > 0; text: sections.s.reclaim; font.pixelSize: 12 * panel.zoom; color: panel.p.foreground; opacity: 0.65 }
        // What git says, for a checkout.
        Column {
          visible: sections.s.visible && sections.s.git.visible
          topPadding: 10 * panel.zoom
          spacing: 4 * panel.zoom
          Caps { text: "Git" }
          Label { text: sections.s.git.text; font.bold: true; font.pixelSize: 14 * panel.zoom; color: sections.s.git.clean ? panel.p.success : panel.p.foreground }
        }
        // Badges and the one action that matters.
        Column {
          visible: sections.s.visible && sections.s.mark.visible
          topPadding: 8 * panel.zoom
          spacing: 6 * panel.zoom
          Row {
            spacing: 6 * panel.zoom
            Repeater { model: sections.s.mark.chips; Chip { required property string modelData; text: modelData } }
          }
          Btn {
            text: sections.s.mark.label
            kind: sections.s.mark.filled ? "suggested" : ""
            onClicked: backend.markSelection()
          }
        }

        // ── worth a look ──
        Divider { }
        Row {
          width: parent.width
          Caps { width: parent.width - worthTotal.width; text: "Worth a look" }
          Label { id: worthTotal; text: backend.worth.total; font.bold: true; font.pixelSize: 12 * panel.zoom; color: panel.p.highlight }
        }
        Repeater {
          model: backend.worth.rows
          Rectangle {
            required property var modelData
            required property int index
            width: sections.width
            height: worthRow.implicitHeight + 12 * panel.zoom
            color: worthMouse.containsMouse ? Qt.alpha(panel.p.foreground, 0.08) : "transparent"
            Column {
              id: worthRow
              x: 4 * panel.zoom; y: 6 * panel.zoom
              width: parent.width - 8 * panel.zoom
              spacing: 3 * panel.zoom
              Row {
                width: parent.width
                Label { width: parent.width - worthSize.width; text: modelData.title; elide: Text.ElideMiddle; font.bold: true; font.pixelSize: 12.5 * panel.zoom; color: panel.p.foreground }
                Label { id: worthSize; text: modelData.size; font.pixelSize: 12.5 * panel.zoom; color: panel.p.foreground }
              }
              Label { text: modelData.reason; font.pixelSize: 11.5 * panel.zoom; color: panel.p.foreground; opacity: 0.65 }
              Bar { width: parent.width; value: modelData.fraction; fill: panel.p.highlight }
            }
            MouseArea { id: worthMouse; anchors.fill: parent; hoverEnabled: true; cursorShape: Qt.PointingHandCursor; onClicked: backend.openWorth(index) }
          }
        }

        // ── marked ──
        Divider { }
        readonly property var m: backend.marked
        Row {
          width: parent.width
          Caps { width: parent.width - markedTotal.width; text: sections.m.title }
          Label { id: markedTotal; text: sections.m.total; font.pixelSize: 12 * panel.zoom; color: panel.p.foreground; opacity: 0.65 }
        }
        Label { visible: sections.m.rows.length === 0; text: "Space marks the tile you point at"; font.pixelSize: 12 * panel.zoom; color: panel.p.foreground; opacity: 0.65 }
        Repeater {
          model: sections.m.rows
          Row {
            required property var modelData
            width: sections.width
            spacing: 8 * panel.zoom
            Rectangle { width: 8 * panel.zoom; height: width; radius: width / 2; color: modelData.color; anchors.verticalCenter: parent.verticalCenter }
            Label { width: parent.width - 16 * panel.zoom - markedSize.width - unmark.width - 16 * panel.zoom; text: modelData.tilde; elide: Text.ElideMiddle; font.pixelSize: 11.5 * panel.zoom; color: panel.p.foreground; anchors.verticalCenter: parent.verticalCenter }
            Label { id: markedSize; text: modelData.size; font.pixelSize: 11.5 * panel.zoom; color: panel.p.foreground; opacity: 0.65; anchors.verticalCenter: parent.verticalCenter }
            Btn { id: unmark; text: "×"; kind: "flat"; onClicked: backend.unmark(modelData.path) }
          }
        }
        Label { visible: text.length > 0; text: sections.m.more; font.pixelSize: 11.5 * panel.zoom; color: panel.p.foreground; opacity: 0.65 }
        Label { visible: text.length > 0; text: sections.m.nested; font.pixelSize: 11.5 * panel.zoom; color: panel.p.foreground; opacity: 0.65 }

        // ── the notice: what marking or the filter said last ──
        Notice { width: parent.width; notice: backend.notice }
        Label {
          visible: text.length > 0
          width: parent.width; wrapMode: Text.Wrap
          text: backend.filterBar.notice
          font.pixelSize: 12 * panel.zoom; color: panel.p.highlight
        }
      }
    }

    // ── the disk, pinned ──
    Rectangle { Layout.fillWidth: true; Layout.preferredHeight: 1; color: Qt.alpha(panel.p.foreground, 0.12) }
    Column {
      readonly property var d: backend.disk
      readonly property var j: backend.projection
      id: disk
      Layout.fillWidth: true
      Layout.margins: 18 * panel.zoom
      Layout.topMargin: 12 * panel.zoom
      spacing: 6 * panel.zoom
      Row {
        width: parent.width
        spacing: 10 * panel.zoom
        Caps { text: "Disk"; anchors.verticalCenter: parent.verticalCenter }
        Label { width: parent.width - 120 * panel.zoom; text: disk.d.device; elide: Text.ElideMiddle; font.pixelSize: 11.5 * panel.zoom; color: panel.p.foreground; opacity: 0.65; anchors.verticalCenter: parent.verticalCenter }
        Btn { text: "Volumes"; fontSize: 11; onClicked: backend.openVolumes() }
      }
      Row {
        spacing: 8 * panel.zoom
        Label { id: freeNumber; text: disk.d.free; font.bold: true; font.pixelSize: 34 * panel.zoom; color: panel.p.foreground }
        Label { text: disk.d.unit; anchors.baseline: freeNumber.baseline; font.pixelSize: 16 * panel.zoom; color: panel.p.foreground; opacity: 0.8 }
      }
      Bar { width: parent.width; value: disk.d.used; fill: Qt.alpha(panel.p.foreground, 0.22) }
      Row {
        width: parent.width
        Label { width: parent.width / 2; text: disk.d.usedText; font.pixelSize: 11.5 * panel.zoom; color: panel.p.foreground; opacity: 0.65 }
        Label { width: parent.width / 2; horizontalAlignment: Text.AlignRight; text: disk.d.total; font.pixelSize: 11.5 * panel.zoom; color: panel.p.foreground; opacity: 0.65 }
      }
      // Free space once the marks go: the slice they give back, hatched.
      Column {
        visible: disk.j.visible
        width: parent.width
        spacing: 6 * panel.zoom
        topPadding: 4 * panel.zoom
        Label { visible: text.length > 0; width: parent.width; horizontalAlignment: Text.AlignRight; text: disk.j.after; font.bold: true; font.pixelSize: 17 * panel.zoom; color: panel.p.highlight }
        Loader {
          active: disk.j.after.length > 0
          visible: active
          width: parent.width; height: 8 * panel.zoom
          sourceComponent: Canvas {
          id: meter
          width: parent ? parent.width : 0; height: 8 * panel.zoom
          property var j: disk.j
          onJChanged: requestPaint()
          onWidthChanged: requestPaint()
          onPaint: {
            var ctx = getContext("2d"); var P = backend.palette;
            ctx.reset();
            ctx.fillStyle = Qt.alpha(P.foreground, 0.08); ctx.fillRect(0, 0, width, height);
            ctx.fillStyle = Qt.alpha(P.foreground, 0.22); ctx.fillRect(0, 0, width * j.usedAfter, height);
            var x = width * j.usedAfter, w = width * (j.usedNow - j.usedAfter);
            ctx.fillStyle = Qt.alpha(P.highlight, 0.25); ctx.fillRect(x, 0, w, height);
            ctx.save(); ctx.beginPath(); ctx.rect(x, 0, w, height); ctx.clip();
            ctx.strokeStyle = Qt.alpha(P.highlight, 0.5); ctx.lineWidth = 1; ctx.beginPath();
            for (var hx = x - height; hx < x + w; hx += 4) { ctx.moveTo(hx, height); ctx.lineTo(hx + height, 0); }
            ctx.stroke(); ctx.restore();
          }
          }
        }
        Label { visible: text.length > 0; width: parent.width; wrapMode: Text.Wrap; text: disk.j.leftOut; font.pixelSize: 11.5 * panel.zoom; color: panel.p.foreground; opacity: 0.65 }
        Rectangle {
          width: parent.width
          height: reviewRow.implicitHeight + 12 * panel.zoom
          color: Qt.alpha(panel.p.highlight, reviewMouse.containsMouse ? 0.18 : 0.1)
          border.color: panel.p.highlight
          Row {
            id: reviewRow
            x: 10 * panel.zoom; anchors.verticalCenter: parent.verticalCenter
            spacing: 8 * panel.zoom
            Label { width: Math.min(implicitWidth, disk.width - 60 * panel.zoom); text: disk.j.review; elide: Text.ElideRight; font.pixelSize: 12.5 * panel.zoom; color: panel.p.highlight; anchors.verticalCenter: parent.verticalCenter }
            Keycap { text: "c"; anchors.verticalCenter: parent.verticalCenter }
          }
          MouseArea { id: reviewMouse; anchors.fill: parent; hoverEnabled: true; cursorShape: Qt.PointingHandCursor; onClicked: backend.openReview() }
        }
      }
    }
  }

  Scroller { target: panelFlick }

  component Fact: Label {
    width: parent ? parent.width / 2 : 0
    font.bold: true
    font.pixelSize: 15 * backend.uiZoom
    color: backend.palette.foreground
    elide: Text.ElideRight
  }

  component Divider: Item {
    width: parent ? parent.width : 0
    height: 21 * backend.uiZoom
    Rectangle { anchors.verticalCenter: parent.verticalCenter; width: parent.width; height: 1; color: Qt.alpha(backend.palette.foreground, 0.12) }
  }
}
