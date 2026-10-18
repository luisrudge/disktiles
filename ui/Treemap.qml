import QtQuick
import Omasharp

// The treemap: C#'s drawing (DrawView, native scene graph) at backend.view
// (zoom and pan), with the hover outline, the selection ring and the hover
// card as small items above it, so pointing never repaints the tiles. The
// pointer, the wheel and clicks go to backend, which hit-tests.
Item {
  id: map
  clip: true
  readonly property var p: backend.palette
  readonly property real zoom: backend.uiZoom

  // Once per change of size, however many of width and height moved, and only
  // once the window's layouts have placed it (`ready`, from the first frame):
  // before that, the sizes it passes through would each cost a layout and a paint.
  property bool ready: false
  function reportSize() { if (ready) backend.resize(width, height) }
  onWidthChanged: Qt.callLater(reportSize)
  onHeightChanged: Qt.callLater(reportSize)
  onReadyChanged: Qt.callLater(reportSize)

  Rectangle { anchors.fill: parent; color: map.p.inset }

  // Going into a directory or out of one, the new picture eases from where
  // that region was on screen (backend.transition).
  Item {
    id: stage
    width: map.width
    height: map.height
    transform: [
      Scale { id: grow; xScale: 1; yScale: 1 },
      Translate { id: shift; x: 0; y: 0 }
    ]
    property var transition: backend.transition
    onTransitionChanged: {
      if (!transition) { ease.stop(); grow.xScale = grow.yScale = 1; shift.x = shift.y = 0; return; }
      ease.stop();
      ease.duration = transition.duration;
      grow.xScale = grow.yScale = transition.k;
      shift.x = transition.x;
      shift.y = transition.y;
      ease.start();
    }
    ParallelAnimation {
      id: ease
      property int duration: 200
      NumberAnimation { target: grow; property: "xScale"; to: 1; duration: ease.duration; easing.type: Easing.OutCubic }
      NumberAnimation { target: grow; property: "yScale"; to: 1; duration: ease.duration; easing.type: Easing.OutCubic }
      NumberAnimation { target: shift; property: "x"; to: 0; duration: ease.duration; easing.type: Easing.OutCubic }
      NumberAnimation { target: shift; property: "y"; to: 0; duration: ease.duration; easing.type: Easing.OutCubic }
    }

    // The tiles: a native drawing from C# (App/Paint.cs), moved and scaled by `view`
    // until a sharp one for the settled view arrives.
    DrawView {
      anchors.fill: parent
      source: "treemap"
      view: backend.view
      // Rendered once into a texture and reused while only the items above it change
      // (the hover outline and card move every frame): like GTK's cached texture.
      layer.enabled: true
    }
  }

  // The pointer: hover, clicks (a second press soon after on the same spot
  // is a double click), the wheel. Back and forward buttons step history.
  MouseArea {
    id: pointer
    anchors.fill: parent
    hoverEnabled: true
    acceptedButtons: Qt.AllButtons
    property real lastPress: 0
    property point lastAt: Qt.point(-100, -100)
    onPositionChanged: (mouse) => backend.hover(mouse.x, mouse.y)
    onExited: backend.leave()
    onPressed: (mouse) => {
      var b = mouse.button === Qt.LeftButton ? 1 : mouse.button === Qt.MiddleButton ? 2 : mouse.button === Qt.RightButton ? 3
            : mouse.button === Qt.BackButton ? 8 : mouse.button === Qt.ForwardButton ? 9 : 0;
      var now = Date.now();
      var again = b === 1 && now - lastPress < Qt.styleHints.mouseDoubleClickInterval
                && Math.abs(mouse.x - lastAt.x) < 6 && Math.abs(mouse.y - lastAt.y) < 6;
      lastPress = again ? 0 : now;
      lastAt = Qt.point(mouse.x, mouse.y);
      if (b > 0) backend.click(mouse.x, mouse.y, b, again ? 2 : 1, mouse.modifiers);
    }
    // In notches, down positive. A touchpad sends pixels (Qt leaves pixelDelta empty for a
    // wheel): 24 px make a notch, as in the GTK original's surface units.
    onWheel: (wheel) => {
      var px = wheel.pixelDelta.x !== 0 || wheel.pixelDelta.y !== 0;
      backend.scroll(wheel.x, wheel.y, px ? -wheel.pixelDelta.x / 24 : -wheel.angleDelta.x / 120,
        px ? -wheel.pixelDelta.y / 24 : -wheel.angleDelta.y / 120, wheel.modifiers);
    }
  }

  // The selection's amber ring, then the hover outline, then the card.
  Rectangle {
    property var r: backend.selectRect
    visible: r.length === 4 && !backend.scan.visible
    x: visible ? r[0] : 0; y: visible ? r[1] : 0
    width: visible ? r[2] : 0; height: visible ? r[3] : 0
    color: "transparent"
    border.color: map.p.highlight
    border.width: 2
  }
  Rectangle {
    property var r: backend.hoverRect
    visible: r.length === 4 && !backend.scan.visible
    x: visible ? r[0] : 0; y: visible ? r[1] : 0
    width: visible ? r[2] : 0; height: visible ? r[3] : 0
    color: "transparent"
    border.color: map.p.outline
    border.width: 2
  }

  // The hover card, beside the pointer, flipped to stay inside the treemap.
  Rectangle {
    id: card
    property var c: backend.card
    property var at: backend.cardAt
    visible: c !== null && c !== undefined && !backend.scan.visible
    width: Math.min(cardText.implicitWidth, 460 * map.zoom) + 24 * map.zoom
    height: cardText.implicitHeight + 24 * map.zoom
    x: !visible || at.length < 2 ? 0 : Math.max(4, Math.min(at[0] + 18 + width > map.width ? at[0] - 18 - width : at[0] + 18, map.width - width - 4))
    y: !visible || at.length < 2 ? 0 : Math.max(4, Math.min(at[1] + 18 + height > map.height ? at[1] - 18 - height : at[1] + 18, map.height - height - 4))
    color: Qt.alpha(map.p.background, 0.93)
    border.color: Qt.alpha(map.p.foreground, 0.4)
    Column {
      id: cardText
      x: 12 * map.zoom; y: 12 * map.zoom
      width: Math.min(implicitWidth, 460 * map.zoom)
      spacing: 4 * map.zoom
      Label { text: card.visible ? card.c.name : ""; font.bold: true; font.pixelSize: 13.5 * map.zoom; color: map.p.foreground; elide: Text.ElideMiddle; width: Math.min(implicitWidth, 460 * map.zoom) }
      Label { visible: text.length > 0; text: card.visible ? card.c.path : ""; font.pixelSize: 11.5 * map.zoom; color: map.p.foreground; opacity: 0.6; elide: Text.ElideMiddle; width: Math.min(implicitWidth, 460 * map.zoom) }
      Row {
        spacing: 10 * map.zoom
        Label { text: card.visible ? card.c.size : ""; font.bold: true; font.pixelSize: 19 * map.zoom; color: map.p.foreground }
        Bar { width: 90 * map.zoom; anchors.verticalCenter: parent.verticalCenter; value: card.visible ? card.c.share : 0; fill: card.visible ? card.c.color : "transparent" }
        Label { text: card.visible ? card.c.percent : ""; anchors.verticalCenter: parent.verticalCenter; font.pixelSize: 11.5 * map.zoom; color: map.p.foreground; opacity: 0.6 }
      }
      Label { visible: text.length > 0; text: card.visible ? card.c.facts : ""; font.pixelSize: 11.5 * map.zoom; color: map.p.foreground }
      Label { visible: text.length > 0; text: card.visible ? card.c.badge : ""; font.pixelSize: 11.5 * map.zoom; color: map.p.danger }
      Label { visible: text.length > 0; text: card.visible ? card.c.hint : ""; font.pixelSize: 11.5 * map.zoom; color: map.p.foreground; opacity: 0.6 }
    }
  }

  // While a scan runs, or after one stopped or failed: what is being read
  // and how much so far.
  Rectangle {
    id: scanPanel
    anchors.fill: parent
    readonly property var s: backend.scan
    visible: s.visible
    color: map.p.inset
    MouseArea { anchors.fill: parent; hoverEnabled: true }
    Column {
      anchors.centerIn: parent
      width: Math.min(parent.width - 40, 560 * map.zoom)
      spacing: 18 * map.zoom
      // A spinner that stops dead when the scan lands (Controls' BusyIndicator fades out
      // for 250 ms, and every frame of that delays the first treemap).
      Item {
        anchors.horizontalCenter: parent.horizontalCenter
        width: 36 * map.zoom; height: width
        visible: scanPanel.s.scanning
        Rectangle {
          width: parent.width / 5; height: width; radius: width / 2
          color: map.p.highlight
          x: parent.width / 2 + Math.cos(spin.angle) * parent.width * 0.4 - width / 2
          y: parent.height / 2 + Math.sin(spin.angle) * parent.height * 0.4 - height / 2
        }
        NumberAnimation on rotation { id: spin; property real angle: 0; running: false }
        Timer { interval: 50; repeat: true; running: scanPanel.s.scanning && scanPanel.visible; onTriggered: spin.angle += 0.5 }
      }
      Label {
        anchors.horizontalCenter: parent.horizontalCenter
        width: parent.width; horizontalAlignment: Text.AlignHCenter; elide: Text.ElideMiddle
        text: scanPanel.s.title; font.bold: true; font.pixelSize: 21 * map.zoom; color: map.p.foreground
      }
      Row {
        anchors.horizontalCenter: parent.horizontalCenter
        spacing: 36 * map.zoom
        Repeater {
          model: [["files", scanPanel.s.files], ["directories", scanPanel.s.dirs],
                  ["measured", scanPanel.s.bytes], ["unreadable", scanPanel.s.errors]]
          Column {
            required property var modelData
            required property int index
            spacing: 2
            Label { text: modelData[0]; font.pixelSize: 11 * map.zoom; color: map.p.foreground; opacity: 0.6 }
            Label {
              text: modelData[1]; font.bold: true; font.pixelSize: 21 * map.zoom
              color: index === 3 && backend.scan.errorsAccent ? map.p.highlight : map.p.foreground
            }
          }
        }
      }
      Bar {
        anchors.horizontalCenter: parent.horizontalCenter
        visible: scanPanel.s.scanning
        width: 360 * map.zoom
        value: scanPanel.s.fraction
        fill: map.p.highlight
      }
      Label {
        anchors.horizontalCenter: parent.horizontalCenter
        width: parent.width; horizontalAlignment: Text.AlignHCenter; wrapMode: Text.Wrap
        text: scanPanel.s.note; font.pixelSize: 12 * map.zoom; color: map.p.foreground; opacity: 0.65
      }
      Btn {
        anchors.horizontalCenter: parent.horizontalCenter
        text: scanPanel.s.button
        onClicked: backend.scanButton()
      }
      Rectangle {
        visible: scanPanel.s.error.length > 0
        width: parent.width
        height: error.implicitHeight + 20
        color: "transparent"
        border.color: map.p.danger
        Label { id: error; x: 10; y: 10; width: parent.width - 20; wrapMode: Text.Wrap; text: scanPanel.s.error; color: map.p.danger }
      }
    }
  }

  // The volume picker: a card that owns the keys while open.
  Rectangle {
    id: picker
    anchors.fill: parent
    readonly property var v: backend.volumes
    visible: v.visible
    color: Qt.alpha(map.p.background, 0.5)
    MouseArea { anchors.fill: parent; onClicked: backend.closeVolumes() }
    Rectangle {
      anchors.centerIn: parent
      width: Math.min(parent.width - 40, 460 * map.zoom)
      height: volumeCard.implicitHeight + 36 * map.zoom
      color: map.p.background
      border.color: map.p.border
      MouseArea { anchors.fill: parent }
      Column {
        id: volumeCard
        x: 18 * map.zoom; y: 18 * map.zoom
        width: parent.width - 36 * map.zoom
        spacing: 10 * map.zoom
        Label { text: "Scan a volume"; font.bold: true; font.pixelSize: 16 * map.zoom; color: map.p.foreground }
        Label { text: "Up and down moves, Enter scans it, Escape stays here."; width: parent.width; wrapMode: Text.Wrap; font.pixelSize: 12 * map.zoom; color: map.p.foreground; opacity: 0.65 }
        Label { visible: text.length > 0; text: picker.v.status; color: map.p.foreground }
        Repeater {
          model: picker.v.rows
          Rectangle {
            required property string modelData
            required property int index
            width: volumeCard.width
            height: row.implicitHeight + 16 * map.zoom
            color: index === backend.volumes.selected ? Qt.alpha(map.p.foreground, 0.18) : rowMouse.containsMouse ? Qt.alpha(map.p.foreground, 0.08) : "transparent"
            Label { id: row; x: 10; anchors.verticalCenter: parent.verticalCenter; width: parent.width - 20; elide: Text.ElideMiddle; text: modelData; color: map.p.foreground }
            MouseArea { id: rowMouse; anchors.fill: parent; hoverEnabled: true; onClicked: backend.pickVolume(index) }
          }
        }
      }
    }
  }
}
