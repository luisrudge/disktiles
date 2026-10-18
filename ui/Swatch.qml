import QtQuick

// A legend swatch: a colour, hatched for "Reclaimable" (plain items, no Canvas).
Rectangle {
  id: swatch
  property bool hatch: false
  property alias fill: swatch.color
  implicitWidth: 12 * backend.uiZoom
  implicitHeight: 12 * backend.uiZoom
  clip: hatch
  Repeater {
    model: swatch.hatch ? 8 : 0
    Rectangle {
      required property int index
      width: 1; height: swatch.height * 1.5
      x: index * 3 - swatch.height * 0.2
      y: -swatch.height * 0.25
      rotation: 45
      color: Qt.alpha(backend.palette.bright, 0.5)
    }
  }
}
