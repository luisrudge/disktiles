# DiskTiles

A C# + QML app for Omarchy, made with the omarchy-app template: logic in `Backend.cs`, UI in `Main.qml` (window) or `plugin/Panel.qml` (panel).

- Building, checking and installing it: the omasharp skill in `.agents/skills/omasharp/`.
- Never run `dotnet run` or `dotnet watch` as an agent: they open the app on the user's screen. Check with `sh .agents/skills/omasharp/scripts/check.sh`.
