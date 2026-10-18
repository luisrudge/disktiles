// check.sh support (the omasharp skill): with OMASHARP_TEXT_DUMP=<file>, write every visible text in the window
// (Label/Text/TextField/Button/ComboBox ...) to <file> a few times a second, so a check
// can read what the user would see, not just a screenshot.
// Keep the output in step with the panel version in bridge/shell/dev-shell.qml.
#include <QFile>
#include <QQmlApplicationEngine>
#include <QQuickItem>
#include <QQuickWindow>
#include <QTimer>

static QString kind(const QObject *o) {
    QString n = QString::fromLatin1(o->metaObject()->className());
    n.remove(QLatin1String("QQuick"));
    const int q = n.indexOf(QLatin1String("_QML"));
    return q > 0 ? n.left(q) : n;
}

static void walk(QQuickItem *item, int depth, QStringList &out) {
    if (!item || !item->isVisible() || item->opacity() <= 0.01) return;
    // Password fields: only the masked text, never the secret.
    const QVariant echo = item->property("echoMode");
    const bool masked = echo.isValid() && echo.toInt() != 0;
    QString text = masked ? QString() : item->property("text").toString();
    if (text.isEmpty()) text = item->property("displayText").toString();
    const QString placeholder = item->property("placeholderText").toString();
    if (text.isEmpty() && !placeholder.isEmpty()) text = "(placeholder) " + placeholder;
    if (!text.isEmpty() && kind(item) != QLatin1String("PlaceholderText")) {
        QString line = QString(depth * 2, ' ') + kind(item) + ": " + text.simplified();
        const QVariant checked = item->property("checked");
        if (checked.isValid() && item->property("checkable").toBool()) line += checked.toBool() ? " [checked]" : " [unchecked]";
        if (item->hasActiveFocus()) line += " [focus]";
        out << line;
    }
    for (QQuickItem *child : item->childItems()) walk(child, depth + (text.isEmpty() ? 0 : 1), out);
}

void startTextDump(QQmlApplicationEngine &engine) {
    const QString path = qEnvironmentVariable("OMASHARP_TEXT_DUMP");
    if (path.isEmpty()) return;
    auto *timer = new QTimer(&engine);
    QObject::connect(timer, &QTimer::timeout, &engine, [&engine, path] {
        QStringList out;
        for (QObject *root : engine.rootObjects())
            if (auto *w = qobject_cast<QQuickWindow *>(root)) walk(w->contentItem(), 0, out);
        QFile f(path + ".tmp");
        if (f.open(QIODevice::WriteOnly | QIODevice::Truncate)) {
            f.write(out.join('\n').toUtf8() + '\n');
            f.close();
            QFile::remove(path);
            f.rename(path);
        }
    });
    timer->start(250);
}
