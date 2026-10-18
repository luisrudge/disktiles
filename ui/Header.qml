import QtQuick
import QtQuick.Layouts

// Above the treemap: the trail at the start and the view controls (back and
// forward, Size/Files/Age, Hidden, Apparent, Depth) at the end; under them
// the scan totals, then the legend, or the filter while it is shown.
ColumnLayout {
  id: header
  spacing: 0
  readonly property var p: backend.palette
  readonly property real zoom: backend.uiZoom
  /// Where each crumb is, for the sibling menu that hangs from it.
  property var crumbItems: ({})
  signal siblingsAnchor(real x, real y)

  RowLayout {
    Layout.fillWidth: true
    // A fixed height: the trail filling in must not move the treemap below.
    Layout.preferredHeight: 34 * header.zoom
    Layout.topMargin: 4 * header.zoom
    Layout.leftMargin: 6 * header.zoom
    Layout.rightMargin: 10 * header.zoom
    spacing: 8 * header.zoom

    // The trail: short of room it gives up its start, never the crumb you are in.
    Item {
      id: trailClip
      Layout.fillWidth: true
      Layout.preferredHeight: trail.implicitHeight
      clip: true
      Row {
        id: trail
        x: Math.min(0, trailClip.width - implicitWidth)
        spacing: 2 * header.zoom
        Repeater {
          model: backend.trail
          Row {
            id: crumb
            required property var modelData
            spacing: 0
            Label {
              visible: crumb.modelData.kind === "sep"
              anchors.verticalCenter: parent.verticalCenter
              text: crumb.modelData.label
              font.pixelSize: 13 * header.zoom
              color: header.p.foreground
              opacity: 0.4
            }
            Rectangle {
              visible: crumb.modelData.kind !== "sep"
              implicitWidth: crumbLabel.implicitWidth + 12 * header.zoom
              implicitHeight: crumbLabel.implicitHeight + 6 * header.zoom
              color: crumbMouse.containsMouse ? Qt.alpha(header.p.foreground, 0.08) : "transparent"
              Label {
                id: crumbLabel
                anchors.centerIn: parent
                text: crumb.modelData.label
                font.pixelSize: 13 * header.zoom
                font.bold: crumb.modelData.current
                color: crumb.modelData.pending ? header.p.highlight : header.p.foreground
                opacity: crumb.modelData.pending || crumb.modelData.current ? 1 : crumb.modelData.kind === "above" ? 0.5 : 0.75
              }
              Tip { text: crumb.modelData.kind === "above" ? "Scan from here" : ""; shown: crumbMouse.containsMouse }
              MouseArea {
                id: crumbMouse
                anchors.fill: parent
                hoverEnabled: true
                cursorShape: Qt.PointingHandCursor
                onClicked: {
                  var at = crumb.mapToItem(null, 0, crumb.height);
                  header.siblingsAnchor(at.x, at.y);
                  backend.crumb(crumb.modelData.index);
                }
              }
            }
            Rectangle {
              visible: crumb.modelData.hasMenu
              implicitWidth: chevron.implicitWidth + 8 * header.zoom
              implicitHeight: chevron.implicitHeight + 6 * header.zoom
              color: chevronMouse.containsMouse ? Qt.alpha(header.p.foreground, 0.08) : "transparent"
              Label { id: chevron; anchors.centerIn: parent; text: "▾"; font.pixelSize: 11 * header.zoom; color: header.p.foreground; opacity: 0.6 }
              MouseArea {
                id: chevronMouse
                anchors.fill: parent
                hoverEnabled: true
                onClicked: {
                  var at = crumb.mapToItem(null, 0, crumb.height);
                  header.siblingsAnchor(at.x, at.y);
                  backend.crumbMenu(crumb.modelData.index);
                }
              }
            }
          }
        }
      }
    }

    // Back and forward.
    Row {
      Btn {
        text: "<"; kind: "flat"; active: backend.backButton.enabled; tip: backend.backButton.tooltip
        onClicked: backend.back()
      }
      Btn {
        text: ">"; kind: "flat"; active: backend.forwardButton.enabled; tip: backend.forwardButton.tooltip
        onClicked: backend.forward()
      }
    }

    // Size | Files | Age: what areas and colours say.
    Row {
      Repeater {
        model: ["Size", "Files", "Age"]
        Btn {
          required property string modelData
          required property int index
          text: modelData
          tip: "Size, files or age: what areas and colours say · t"
          kind: backend.mode === index ? "checked" : ""
          onClicked: backend.setMode(index)
        }
      }
    }

    Check { text: "Hidden files"; checked: backend.includeHidden; onToggled: backend.toggleHidden() }
    Check { text: "Apparent size"; checked: backend.apparentSize; onToggled: backend.toggleApparent() }

    // Depth N, then − and +, framed together.
    Rectangle {
      implicitWidth: depthRow.implicitWidth
      implicitHeight: depthRow.implicitHeight
      color: "transparent"
      border.color: header.p.border
      Row {
        id: depthRow
        Label {
          text: "Depth " + backend.depth
          anchors.verticalCenter: parent.verticalCenter
          leftPadding: 8 * header.zoom; rightPadding: 8 * header.zoom
          font.pixelSize: 12.5 * header.zoom
          color: header.p.foreground
        }
        Btn { text: "−"; kind: "flat"; tip: "Levels drawn at once · [ and ]"; active: backend.depth > backend.minDepth; onClicked: backend.stepDepth(-1) }
        Btn { text: "+"; kind: "flat"; tip: "Levels drawn at once · [ and ]"; active: backend.depth < backend.maxDepth; onClicked: backend.stepDepth(1) }
      }
    }
  }

  // The totals, then the legend or, while it is shown, the filter.
  RowLayout {
    Layout.fillWidth: true
    Layout.leftMargin: 10 * header.zoom
    Layout.rightMargin: 10 * header.zoom
    Layout.topMargin: 2 * header.zoom
    Layout.bottomMargin: 4 * header.zoom
    spacing: 16 * header.zoom

    Row {
      spacing: 6 * header.zoom
      Label { text: backend.totals.bytes; font.pixelSize: 12.5 * header.zoom; color: header.p.foreground }
      Label { text: backend.totals.counts; font.pixelSize: 11.5 * header.zoom; color: header.p.foreground; opacity: 0.65; anchors.verticalCenter: parent.verticalCenter }
      Label { visible: text.length > 0; text: backend.totals.unreadable; font.pixelSize: 11.5 * header.zoom; color: header.p.warning; anchors.verticalCenter: parent.verticalCenter }
    }

    // The legend, or in its place while it is shown, the name filter ("/").
    Item {
      Layout.fillWidth: true
      Layout.preferredHeight: legendFlow.implicitHeight
      // The key to the kind colours; Age mode shows its ramp instead.
      Flow {
        id: legendFlow
        width: parent.width
        // Hidden, not removed, while the filter shows: the row keeps its
        // height, so the treemap below does not jump while you type.
        opacity: backend.filterBar.visible ? 0 : 1
        spacing: 14 * header.zoom
        clip: true
        Repeater {
          model: backend.legend
          Row {
            required property var modelData
            spacing: 6 * header.zoom
            Swatch { anchors.verticalCenter: parent.verticalCenter; fill: modelData.color; hatch: modelData.hatch }
            Label { text: modelData.label; font.pixelSize: 12 * header.zoom; color: header.p.foreground }
          }
        }
      }
      Rectangle {
        readonly property var f: backend.filterBar
        visible: f.visible
        anchors.verticalCenter: parent.verticalCenter
        implicitWidth: filterRow.implicitWidth + 16 * header.zoom
        implicitHeight: filterRow.implicitHeight + 4 * header.zoom
        color: "transparent"
        border.color: f.applied ? header.p.highlight : Qt.alpha(header.p.foreground, f.open ? 0.7 : 0.2)
        Row {
          id: filterRow
          x: 8 * header.zoom
          anchors.verticalCenter: parent.verticalCenter
          spacing: 8 * header.zoom
          Label { text: "⌕"; font.pixelSize: 13 * header.zoom; color: parent.parent.f.applied ? header.p.highlight : header.p.foreground; opacity: 0.7 }
          Label {
            text: parent.parent.f.text + (parent.parent.f.open ? "▏" : "")
            font.pixelSize: 12.5 * header.zoom
            color: header.p.foreground
            opacity: parent.parent.f.placeholder ? 0.7 : 1
          }
          Label {
            visible: text.length > 0; text: parent.parent.f.summary
            anchors.verticalCenter: parent.verticalCenter
            font.pixelSize: 11 * header.zoom
            color: parent.parent.f.applied ? header.p.highlight : header.p.foreground
            opacity: parent.parent.f.applied ? 1 : 0.7
          }
          Label { text: parent.parent.f.hint; anchors.verticalCenter: parent.verticalCenter; font.pixelSize: 11 * header.zoom; color: header.p.foreground; opacity: 0.45 }
        }
      }
    }
  }

  // A check box that never takes the keyboard.
  component Check: Item {
    id: check
    property string text
    property bool checked
    signal toggled()
    implicitWidth: checkRow.implicitWidth
    implicitHeight: checkRow.implicitHeight
    Row {
      id: checkRow
      spacing: 6 * backend.uiZoom
      Rectangle {
        anchors.verticalCenter: parent.verticalCenter
        width: 14 * backend.uiZoom; height: width
        color: check.checked ? backend.palette.accent : "transparent"
        border.color: Qt.alpha(backend.palette.foreground, 0.4)
        Label { anchors.centerIn: parent; visible: check.checked; text: "✓"; font.pixelSize: 11 * backend.uiZoom; color: backend.palette.onAccent }
      }
      Label { text: check.text; anchors.verticalCenter: parent.verticalCenter; font.pixelSize: 12.5 * backend.uiZoom; color: backend.palette.foreground }
    }
    MouseArea { anchors.fill: parent; cursorShape: Qt.PointingHandCursor; onClicked: check.toggled() }
  }
}
