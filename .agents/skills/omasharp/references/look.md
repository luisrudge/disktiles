# Look like Omarchy

## Window apps

- `theme.*` holds every key in Omarchy's `colors.toml`, camelCased: `theme.background`, `theme.foreground`, `theme.accent`, `theme.selection`, `theme.muted`, `theme.darkBackground`, `theme.red`, … plus `theme.dark`. It updates live when the user switches themes. `Main.qml` sets the Omarchy font (`monospace`) on the window, so use `Label` (from QtQuick.Controls, which inherits it) rather than `Text`. Keep the layout simple and flat, like omacalc.
- Secondary text (captions, hints): `Qt.alpha(theme.foreground, 0.6)`. `theme.muted` is meant for borders and is too dim for text.
- Surfaces: `theme.lighterBackground` (fields, buttons), `theme.selection` (pressed, selected rows), `theme.muted` (borders). Don't derive them with `Qt.lighter(theme.background, …)`: that does nothing on Omarchy's pure black and pure white themes.
- A themed text field, with the content selected on focus so typing replaces it:
  ```qml
  TextField {
    color: theme.foreground; selectedTextColor: theme.background; selectionColor: theme.accent
    background: Rectangle { color: theme.lighterBackground; border.color: parent.activeFocus ? theme.accent : theme.muted }
    onActiveFocusChanged: if (activeFocus) selectAll()
  }
  ```

## Theme colors in C#

For colors you compute in C# (e.g. a `Drawing`): `Theme.Color("accent")`, `Theme.Get("darkBackground")`, `Theme.Dark`, all keys in `Theme.Colors`. `Theme.Changed` fires on the UI thread when the user switches themes: redraw then. Works in panels too.

## Panels

- Use the shell's own `Commons.Color.*` and `Style.*` (from `qs.Commons`), as `Panel.qml` already does. Always write `Commons.Color`, with `import qs.Commons as Commons`: since Qt 6.12, QtQuick has its own `Color` type, and a bare `Color.popups.background` is undefined. The colors that exist: `Commons.Color.foreground`, `Commons.Color.background`, `Commons.Color.accent`, `Commons.Color.urgent` (errors, warnings), `Commons.Color.muted`, and `Commons.Color.popups.background` / `.text` / `.border` (use these for a panel). There is no `Commons.Color.red`, `Commons.Color.green` and so on: an unknown name gives "Unable to assign [undefined] to QColor" in the check output.
- Fonts: `Style.font.family`, sizes `Style.font.caption` / `.body` / `.title` / `.heading` / `.display` (they follow Omarchy's text size setting; don't hard-code pixel sizes), spacing `Style.space(px)`. Button fills: `Style.normalFill`, `Style.hoverFill`, `Style.normalBorderColor`.
- **The shell's own controls** (`import qs.Ui`) make a panel look exactly like Omarchy's panels: `TextField`, `Button`, `Toggle`, `ToggleSwitch`, `Dropdown`, `NumberField`, `PanelSlider`, `PanelSectionHeader`, `PanelSeparator`, `PopupCard`, … Read their source in `/usr/share/omarchy/shell/Ui/` for properties. If you also need `QtQuick.Controls`, import it with a qualifier (`import QtQuick.Controls as QQC`) so its `TextField`/`Button` don't shadow the shell's.
