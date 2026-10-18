// The Omarchy theme for window apps, as `theme` in QML: every key in
// ~/.local/state/omarchy/current/theme/colors.toml (camelCased: dark_background
// -> theme.darkBackground) plus theme.dark. Follows theme switches live.
// (Shell panels get the same colors from the shell's own Color singleton.)
// bridge/cs/Theme.cs reads the same file for C#: keep defaults and parsing in step.
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QFileSystemWatcher>
#include <QQmlPropertyMap>
#include <QTimer>

static QString camel(const QString &key) {
    QString out;
    bool up = false;
    for (QChar c : key) {
        if (c == '_' || c == '-') {
            up = true;
            continue;
        }
        out += up ? c.toUpper() : c;
        up = false;
    }
    return out;
}

static void load(QQmlPropertyMap *theme, const QString &file) {
    // Defaults for machines without an Omarchy theme.
    QVariantHash v{{"mode", "dark"}, {"background", "#1a1b26"}, {"foreground", "#a9b1d6"}, {"accent", "#7aa2f7"}, {"selection", "#292e42"}, {"muted", "#414868"}, {"lighterBackground", "#24283b"}};
    QFile f(file);
    if (f.open(QIODevice::ReadOnly | QIODevice::Text)) {
        while (!f.atEnd()) {
            const QString line = QString::fromUtf8(f.readLine()).trimmed();
            const int eq = line.indexOf('=');
            if (line.isEmpty() || line.startsWith('#') || eq < 0) continue;
            QString value = line.mid(eq + 1).trimmed();
            if (value.size() >= 2 && (value.startsWith('"') || value.startsWith('\''))) value = value.mid(1, value.indexOf(value[0], 1) - 1);
            v.insert(camel(line.left(eq).trimmed()), value);
        }
    }
    v.insert("dark", v.value("mode").toString() != "light");
    for (auto it = v.begin(); it != v.end(); ++it)
        if (theme->value(it.key()) != it.value()) theme->insert(it.key(), it.value());
}

struct Theme : QQmlPropertyMap {
    explicit Theme(QObject *parent) : QQmlPropertyMap(this, parent) {}
};

QQmlPropertyMap *createTheme(QObject *parent) {
    auto *theme = new Theme(parent);
    const QString current = QDir::homePath() + "/.local/state/omarchy/current";
    const QString file = current + "/theme/colors.toml";
    load(theme, file);
    // Omarchy swaps current/theme for a new directory on a theme change, so watch
    // current/ as well as the theme directory, and re-add the watches after each change.
    auto *watcher = new QFileSystemWatcher(theme);
    auto rewatch = [watcher, current] {
        for (const QString &p : {current, current + "/theme", current + "/theme/colors.toml"})
            if (QFileInfo::exists(p) && !watcher->files().contains(p) && !watcher->directories().contains(p)) watcher->addPath(p);
    };
    rewatch();
    auto *debounce = new QTimer(theme);
    debounce->setSingleShot(true);
    debounce->setInterval(50);
    QObject::connect(debounce, &QTimer::timeout, theme, [theme, file, rewatch] { rewatch(); load(theme, file); });
    QObject::connect(watcher, &QFileSystemWatcher::fileChanged, debounce, qOverload<>(&QTimer::start));
    QObject::connect(watcher, &QFileSystemWatcher::directoryChanged, debounce, qOverload<>(&QTimer::start));
    return theme;
}
