import QtQuick
import QtQuick.Controls

// The window. `backend` is Backend.cs; `theme` is the current Omarchy theme
// (theme.background, theme.foreground, theme.accent, ... follows theme switches live).
ApplicationWindow {
  id: win
  width: 420
  height: 300
  visible: true
  title: "DiskTiles"
  color: theme.background

  font.family: "monospace"

  Column {
    anchors.centerIn: parent
    spacing: 20

    Label {
      anchors.horizontalCenter: parent.horizontalCenter
      text: backend.label
      color: theme.foreground
      font.pixelSize: 22
    }

    Row {
      anchors.horizontalCenter: parent.horizontalCenter
      spacing: 12
      AppButton { text: "Count"; accent: true; onClicked: backend.increment() }
      AppButton { text: "Reset"; onClicked: backend.reset() }
    }
  }

  // Shortcuts fire even while a text field has focus, so use Ctrl+... for actions and keep
  // plain keys (Space, letters, Return) free for typing once the app has text input.
  Shortcut { sequence: "Ctrl+Return"; onActivated: backend.increment() }
  Shortcut { sequence: "Ctrl+R"; onActivated: backend.reset() }
  Shortcut { sequence: "Ctrl+Q"; onActivated: win.close() } // like omacalc; SUPER+W closes it too

  component AppButton: Rectangle {
    id: button
    property string text
    property bool accent: false
    signal clicked()
    width: 120; height: 40
    // Surfaces come from theme keys: Qt.lighter/darker do nothing on pure black or white themes.
    color: mouse.pressed ? (accent ? Qt.darker(theme.accent, 1.2) : theme.selection) : fill
    readonly property color fill: accent ? theme.accent : theme.lighterBackground
    border.color: theme.muted
    border.width: accent ? 0 : 1
    Label { anchors.centerIn: parent; text: button.text; color: button.accent ? theme.background : theme.foreground; font.pixelSize: 15 }
    MouseArea { id: mouse; anchors.fill: parent; onClicked: button.clicked() }
  }
}
