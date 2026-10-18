import QtQuick
import QtQuick.Layouts
import "ui"

// disktiles: see what is using your disk as a map of tiles. `backend` is
// Backend.cs (all logic, in App/ and Core/); this is only what it looks
// like. Every key goes to backend.press, which owns the keymap.
Window {
  id: win
  width: 1440
  height: 900
  visible: true
  title: backend.title
  // Measuring: evaluated with the root's first bindings, once every object exists.
  readonly property bool perfBegin: { if (backend.perfOn) backend.perfMark("qml_bindings", 0, 0); return true }
  readonly property var p: backend.palette
  readonly property real zoom: backend.uiZoom
  color: p.background
  property bool shown: false
  /// The panel's width once dragged; 0 is its own (320 at 100%).
  property real panelWidth: 0
  /// `f` (debug builds): frames presented in the last second, and the time between them.
  property int frames: 0
  property real lastSwap: 0
  property real intervalSum: 0
  property real intervalMax: 0
  property string fpsLine: ""
  onFrameSwapped: {
    frames++;
    if (backend.fpsShown) {
      var now = Date.now();
      if (lastSwap > 0) { intervalSum += now - lastSwap; intervalMax = Math.max(intervalMax, now - lastSwap); }
      lastSwap = now;
    }
    if (backend.perfOn) backend.perfFrame();
    if (!shown) { shown = true; backend.firstFrame(); }
  }
  // Qt renders on its own thread, so this shows the interval between presented frames where
  // the GTK original showed each frame's CPU time.
  Timer {
    interval: 1000; repeat: true; running: backend.fpsShown
    onTriggered: {
      win.fpsLine = win.frames + " fps" + (win.frames > 1 ? " · interval " + (win.intervalSum / (win.frames - 1)).toFixed(1)
        + " ms · max " + win.intervalMax.toFixed(1) + " ms" : "");
      win.frames = 0; win.intervalSum = 0; win.intervalMax = 0; win.lastSwap = 0;
    }
  }

  // The palette is derived in C# from the bridge's Theme, which follows Omarchy switches itself.
  // Off Omarchy it follows the system's colour scheme, which only Qt knows.
  Connections { target: Qt.styleHints; function onColorSchemeChanged() { backend.reloadTheme(Qt.styleHints.colorScheme === Qt.ColorScheme.Dark) } }
  Component.onCompleted: {
    backend.reloadTheme(Qt.styleHints.colorScheme === Qt.ColorScheme.Dark);
    if (backend.perfOn) backend.perfMark("window", 0, 0);
  }
  // The measuring drivers' frame tick (DISKTILES_DRIVE=SWEEP or ZOOMSWEEP).
  FrameAnimation { running: backend.driveTicking; onTriggered: backend.driveTick() }

  // What only the window can do, asked by backend: quit, copy, a file dialog, a snapshot.
  Connections {
    target: backend
    function onRaised(name, e) {
      if (name === "quit") Qt.exit(e.text.length > 0 ? parseInt(e.text) : 0);
      else if (name === "copy") { clipboard.text = e.text; clipboard.selectAll(); clipboard.copy(); }
      else if (name === "pickFolder") { dialogs.active = true; dialogs.item.pickFolder(e.text); }
      else if (name === "saveFile") { dialogs.active = true; dialogs.item.saveFile(e.extra, e.text); }
      else if (name === "snapshot") snapshot(e.text);
    }
  }

  function localPath(url) {
    var s = url.toString();
    return s.startsWith("file://") ? decodeURIComponent(s.substring(7)) : s;
  }

  // --snapshot: the window as Qt draws it, then quit; 1 if it could not be saved.
  function snapshot(path) {
    keys.grabToImage(function (result) {
      var ok = result.saveToFile(path);
      console.log("snapshot " + path + ": " + (ok ? "saved" : "could not save"));
      Qt.exit(ok ? 0 : 1);
    });
  }

  TextEdit { id: clipboard; visible: false }
  Loader { id: dialogs; active: false; source: "ui/Dialogs.qml" }

  // The keyboard: everything to backend, which says whether it took the key.
  Item {
    id: keys
    anchors.fill: parent
    focus: true
    Keys.onPressed: (event) => {
      if (backend.press(event.key, event.modifiers, event.text)) event.accepted = true;
    }

    // The screens: the explore page, and the others built only while shown.
    Item {
      anchors.fill: parent

      // ── explore ──
      ColumnLayout {
        id: explore
        anchors.fill: parent
        visible: backend.screen === "Explore"
        spacing: 0
        Header {
          id: header
          Layout.fillWidth: true
          onSiblingsAnchor: (x, y) => { siblings.anchorX = x; siblings.anchorY = y; }
        }
        RowLayout {
          Layout.fillWidth: true
          Layout.fillHeight: true
          spacing: 0
          // Its size is final once the window's layouts have run (by the first frame).
          Treemap { Layout.fillWidth: true; Layout.fillHeight: true; ready: win.shown }
          // The divider: drag it to size the panel; a double click puts it back at its own width.
          Rectangle {
            visible: panel.visible
            Layout.fillHeight: true
            Layout.preferredWidth: 1
            color: Qt.alpha(win.p.foreground, divider.containsMouse || divider.pressed ? 0.4 : 0.12)
            MouseArea {
              id: divider
              anchors.fill: parent
              anchors.leftMargin: -4; anchors.rightMargin: -4
              hoverEnabled: true
              cursorShape: Qt.SplitHCursor
              property real grabbed: 0
              onPressed: (mouse) => grabbed = mapToItem(null, mouse.x, 0).x
              onPositionChanged: (mouse) => {
                if (!pressed) return;
                var x = mapToItem(null, mouse.x, 0).x;
                win.panelWidth = Math.max(240 * win.zoom, Math.min(win.width * 0.6, win.panelWidth - (x - grabbed)));
                grabbed = x;
              }
              onDoubleClicked: win.panelWidth = 0
            }
          }
          // `p` shows or hides it; a narrow window hides it (the mosaic
          // would be too narrow to read).
          Panel {
            id: panel
            visible: backend.panelShown && win.width >= 760 * win.zoom
            Layout.fillHeight: true
            Layout.preferredWidth: win.panelWidth > 0 ? win.panelWidth : 320 * win.zoom
          }
        }
        // The key-hint bar, most useful first; then the zoom, "? all keys" and the status.
        Rectangle {
          Layout.fillWidth: true
          implicitHeight: footerRow.implicitHeight + 8 * win.zoom
          color: "transparent"
          Rectangle { width: parent.width; height: 1; color: Qt.alpha(win.p.foreground, 0.12) }
          RowLayout {
            id: footerRow
            x: 16 * win.zoom
            width: parent.width - 32 * win.zoom
            anchors.verticalCenter: parent.verticalCenter
            spacing: 16 * win.zoom
            Item {
              Layout.fillWidth: true
              implicitHeight: hintRow.implicitHeight
              clip: true
              Row {
                id: hintRow
                spacing: 16 * win.zoom
                Repeater {
                  model: backend.hints.slice(0, backend.hints.length - 1)
                  Hint { required property var modelData; cap: modelData.cap; label: modelData.label }
                }
              }
            }
            Label { visible: text.length > 0; text: backend.scaleText; font.pixelSize: 12 * win.zoom; color: win.p.secondary }
            Hint { cap: "?"; label: "all keys" }
            Label { text: backend.status; font.pixelSize: 12 * win.zoom; color: Qt.alpha(win.p.secondary, 0.7) }
          }
        }
      }

      // Built when shown: nothing of them is needed to start.
      Loader { anchors.fill: parent; active: backend.screen === "Review"; sourceComponent: Component { ReviewScreen { } } }
      Loader { anchors.fill: parent; active: backend.screen === "Running"; sourceComponent: Component { RunningScreen { } } }
      Loader { anchors.fill: parent; active: backend.screen === "Done"; sourceComponent: Component { DoneScreen { } } }
    }

    // The siblings of a crumb, hanging from it: largest first, with a share bar each.
    Rectangle {
      anchors.fill: parent
      visible: siblings.s.visible
      color: "transparent"
      MouseArea { anchors.fill: parent; onClicked: backend.closeSiblings() }
    }
    Rectangle {
      id: siblings
      readonly property var s: backend.siblings
      property real anchorX: 10
      property real anchorY: 30
      visible: s.visible
      x: Math.max(4, Math.min(anchorX, win.width - width - 4))
      y: anchorY + 2
      width: 340 * win.zoom
      height: Math.min(menu.implicitHeight + 16 * win.zoom, win.height - y - 10)
      color: win.p.background
      border.color: Qt.alpha(win.p.foreground, 0.4)
      clip: true
      MouseArea { anchors.fill: parent }
      Column {
        id: menu
        x: 8 * win.zoom; y: 8 * win.zoom
        width: parent.width - 16 * win.zoom
        spacing: 2 * win.zoom
        Caps { text: siblings.s.title; width: parent.width; bottomPadding: 4 * win.zoom }
        Repeater {
          model: siblings.s.rows
          Rectangle {
            required property var modelData
            required property int index
            width: menu.width
            height: 24 * win.zoom
            color: index === siblings.s.highlighted ? Qt.alpha(win.p.foreground, 0.18) : rowMouse.containsMouse ? Qt.alpha(win.p.foreground, 0.08) : "transparent"
            RowLayout {
              anchors.fill: parent
              anchors.leftMargin: 8 * win.zoom; anchors.rightMargin: 8 * win.zoom
              spacing: 12 * win.zoom
              Label {
                Layout.fillWidth: true; elide: Text.ElideRight
                text: modelData.name; font.pixelSize: 12.5 * win.zoom
                font.bold: modelData.current; color: win.p.foreground; opacity: modelData.isDir ? 1 : 0.8
              }
              Bar { Layout.preferredWidth: 60 * win.zoom; value: modelData.fraction; fill: modelData.color }
              Label { Layout.preferredWidth: 76 * win.zoom; horizontalAlignment: Text.AlignRight; text: modelData.size; font.pixelSize: 12 * win.zoom; color: win.p.foreground; opacity: 0.7 }
            }
            MouseArea { id: rowMouse; anchors.fill: parent; hoverEnabled: true; onClicked: backend.pickSibling(index) }
          }
        }
        Label { visible: text.length > 0; text: siblings.s.more; leftPadding: 8 * win.zoom; font.pixelSize: 12 * win.zoom; color: win.p.foreground; opacity: 0.65 }
      }
    }

    // The frame-rate overlay (`f`, debug builds).
    Rectangle {
      visible: backend.fpsShown
      anchors.right: parent.right; anchors.top: parent.top; anchors.margins: 8
      width: fpsText.implicitWidth + 12; height: fpsText.implicitHeight + 12
      color: "#b3000000"; radius: 5
    Label {
      id: fpsText
      x: 6; y: 6
      text: win.fpsLine.length > 0 ? win.fpsLine : "… fps"
      font.bold: true
      color: "#8cf28c"
    }
    }

    // `?`: every key, grouped, with what it does. Built when opened.
    Loader {
      anchors.fill: parent
      active: backend.helpShown
      sourceComponent: helpCard
    }
    Component {
      id: helpCard
    Rectangle {
      anchors.fill: parent
      color: Qt.alpha(win.p.background, 0.86)
      MouseArea { anchors.fill: parent; onClicked: backend.closeHelp() }
      Rectangle {
        anchors.centerIn: parent
        width: Math.min(parent.width - 40, 560 * win.zoom)
        height: Math.min(parent.height - 40, help.implicitHeight + 48 * win.zoom)
        color: win.p.surface
        border.color: win.p.border
        clip: true
        Column {
          id: help
          x: 24 * win.zoom; y: 24 * win.zoom
          width: parent.width - 48 * win.zoom
          spacing: 14 * win.zoom
          Label { text: "Keyboard and mouse"; font.bold: true; font.pixelSize: 17 * win.zoom; color: win.p.foreground }
          Column {
            width: parent.width
            spacing: 6 * win.zoom
            Repeater {
              model: backend.helpRows
              Row {
                required property var modelData
                width: help.width
                spacing: 18 * win.zoom
                Label { width: 130 * win.zoom; text: modelData.keys; font.pixelSize: 11 * win.zoom; color: win.p.accent }
                Label { width: parent.width - 148 * win.zoom; wrapMode: Text.Wrap; text: modelData.does; font.pixelSize: 12.5 * win.zoom; color: win.p.foreground }
              }
            }
          }
          Label { width: parent.width; elide: Text.ElideMiddle; text: backend.helpFoot; font.pixelSize: 11.5 * win.zoom; color: win.p.foreground; opacity: 0.65 }
        }
      }
    }
    }
  }
}
