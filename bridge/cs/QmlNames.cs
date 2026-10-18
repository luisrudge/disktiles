// How Backend's member names look in QML, and the names QML can't take. One copy: the bridge
// checks names when it starts (Model.cs), and the Omasharp.Analyzers package compiles this same
// file to flag them while you type (OMASHARP001). Keep it plain C# (the analyzer is netstandard2.0).
using System.Collections.Generic;

namespace Omasharp;

static class QmlNames
{
    /// <summary>Count -> count, Increment -> increment.</summary>
    public static string Of(string name) =>
        name.Length == 0 || char.IsLower(name[0]) ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);

    // JS reserved words (a panel's Backend.qml declares `function delete()`), the members the bridge
    // itself puts on `backend` in either transport, and QQmlPropertyMap's own.
    static readonly HashSet<string> s_reserved = new HashSet<string>
    {
        "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete",
        "do", "else", "enum", "export", "extends", "false", "finally", "for", "function", "if", "import",
        "in", "instanceof", "let", "new", "null", "return", "static", "super", "switch", "this", "throw",
        "true", "try", "typeof", "var", "void", "while", "with", "yield",
        "active", "call", "command", "exited", "lastError", "program", "raised", "ready", "restart", "running",
        "deleteLater", "destroyed", "keys", "objectName", "objectNameChanged", "valueChanged",
    };

    public static bool IsReserved(string qmlName) =>
        s_reserved.Contains(qmlName) || (qmlName.Length > 0 && qmlName[0] == '_');

    public static string ReservedMessage(string typeName, string csName, string qmlName) =>
        $"{typeName}.{csName} becomes '{qmlName}' in QML, which is reserved there; rename it (Delete → Remove, Running → IsRunning)";
}
