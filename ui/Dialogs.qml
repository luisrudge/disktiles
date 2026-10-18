import QtQuick
import QtQuick.Dialogs

// The desktop's file dialogs, loaded the first time one is needed (QtQuick.Dialogs is
// a heavy import to pay for at startup).
Item {
  function localPath(url) {
    var s = url.toString();
    return s.startsWith("file://") ? decodeURIComponent(s.substring(7)) : s;
  }
  function pickFolder(dir) { folderDialog.currentFolder = "file://" + dir; folderDialog.open(); }
  function saveFile(dir, name) { saveDialog.currentFolder = "file://" + dir; saveDialog.selectedFile = "file://" + dir + "/" + name; saveDialog.open(); }
  FolderDialog {
    id: folderDialog
    title: "Choose a directory to scan"
    acceptLabel: "Scan"
    onAccepted: backend.folderChosen(localPath(selectedFolder))
  }
  FileDialog {
    id: saveDialog
    fileMode: FileDialog.SaveFile
    onAccepted: backend.saveListTo(localPath(selectedFile))
  }
}
