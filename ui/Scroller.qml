import QtQuick

// A thin scroll position indicator at the right edge of its parent, for a Flickable or
// ListView (`target`) that fills it: wheel and drag scroll it; this only shows where you are.
Rectangle {
  property Flickable target
  visible: target && target.visibleArea.heightRatio < 1
  x: parent.width - width - 2
  y: target ? target.y + target.visibleArea.yPosition * target.height : 0
  width: 4
  height: target ? target.visibleArea.heightRatio * target.height : 0
  radius: 2
  color: Qt.alpha(backend.palette.foreground, 0.3)
}
