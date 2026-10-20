// The Omasharp host: runs Main.qml with the app's C# Backend exposed to QML as
// `backend`. Properties live in a QQmlPropertyMap (so bindings work); methods are
// JS functions on the same map (backend.increment(), backend.add(1, 2)).
//
// The C# side is loaded in-process:
//   release  lib<exe name>.so next to the executable, compiled with native AOT
//   dev      --dev <App.dll>: the normal .NET runtime through hostfxr (devhost.cpp);
//            the app's managed Main calls back into omasharp_host_run()
// QML comes from <exe name>.rcc next to the executable (the app's QML, fonts and
// images, built by rcc), or from --qml-dir <dir> (dev). The host itself is the
// same binary for every app: built once per machine and Qt version.
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QGuiApplication>
#include <QIcon>
#include <QJsonArray>
#include <QJsonDocument>
#include <QJsonObject>
#include <QQmlApplicationEngine>
#include <QQmlContext>
#include <QQmlError>
#include <QQmlPropertyMap>
#include <QResource>
#include <QUrl>

#include <dlfcn.h>
#include <unistd.h>

#include "omasharp.h"

static const omasharp_api *api = nullptr;
static QJSEngine *jsEngine = nullptr;

static QByteArray take(char *p) {
    if (!p) return {};
    QByteArray s(p);
    api->free(p);
    return s;
}

static QVariant fromJson(const QByteArray &json) {
    return QJsonDocument::fromJson("[" + json + "]").array().at(0).toVariant();
}

static QByteArray toJson(const QVariant &v) {
    QJsonArray a;
    a.append(QJsonValue::fromVariant(v));
    QByteArray s = QJsonDocument(a).toJson(QJsonDocument::Compact);
    return s.mid(1, s.size() - 2);
}

class Backend : public QQmlPropertyMap {
    Q_OBJECT
public:
    explicit Backend(QObject *parent) : QQmlPropertyMap(this, parent) {}

    // backend.call("name", [args]); the named functions on the map wrap this.
    Q_INVOKABLE QVariant call(const QString &name, const QVariantList &args = {}) {
        int err = 0;
        const QByteArray argsJson = QJsonDocument(QJsonArray::fromVariantList(args)).toJson(QJsonDocument::Compact);
        const QByteArray r = take(api->call(name.toUtf8().constData(), argsJson.constData(), &err));
        if (err == 2) return QVariant::fromValue(deferred(r.toLongLong()));
        if (err) {
            jsEngine->throwError(QStringLiteral("backend.%1(): %2").arg(name, QString::fromUtf8(r)));
            return {};
        }
        return fromJson(r);
    }

    // Async C# methods (Task, Task<T>): QML gets a Promise, settled when C# reports back.
    QHash<long long, QJSValue> pending;
    QJSValue makeDeferred;
    QJSValue deferred(long long id) {
        if (makeDeferred.isUndefined())
            makeDeferred = jsEngine->evaluate(QStringLiteral(
                "(function() { var d = {}; d.promise = new Promise(function(ok, fail) { d.ok = ok; d.fail = fail; }); return d; })"));
        QJSValue d = makeDeferred.call();
        pending.insert(id, d);
        return d.property(QStringLiteral("promise"));
    }
    void settle(long long id, const QByteArray &text, bool isError) {
        QJSValue d = pending.take(id);
        if (d.isUndefined()) return;
        if (isError)
            d.property(QStringLiteral("fail")).call({jsEngine->newErrorObject(QJSValue::GenericError, QString::fromUtf8(text))});
        else
            d.property(QStringLiteral("ok")).call({jsEngine->toScriptValue(fromJson(text))});
    }

signals:
    // C# called Raise(name, value).
    void raised(const QString &name, const QVariant &value);

protected:
    // QML assigned backend.<key>: hand it to C#, then keep whatever C# ended up with.
    QVariant updateValue(const QString &key, const QVariant &input) override {
        int err = 0;
        const QByteArray name = key.toUtf8();
        const QByteArray r = take(api->set(name.constData(), toJson(input).constData(), &err));
        if (err) qWarning().noquote() << QStringLiteral("backend.%1:").arg(key) << r;
        const QByteArray now = take(api->get(name.constData(), &err));
        return err ? value(key) : fromJson(now);
    }
};

static Backend *backend = nullptr;

static void onNotify(const char *name, const char *json) {
    backend->insert(QString::fromUtf8(name), fromJson(json));
}

static void onResolve(long long id, const char *text, int isError) {
    backend->settle(id, QByteArray(text), isError != 0);
}

static void onRaised(const char *name, const char *json) {
    emit backend->raised(QString::fromUtf8(name), fromJson(json));
}

static void onPost(long long token) {
    QMetaObject::invokeMethod(qApp, [token] { api->run_posted(token); }, Qt::QueuedConnection);
}

static QString exePath() {
    char buf[4096];
    const ssize_t n = readlink("/proc/self/exe", buf, sizeof buf - 1);
    return n > 0 ? QString::fromUtf8(buf, n) : QString();
}

static bool loadNative(const QString &exe) {
    const QFileInfo fi(exe);
    QString lib = qEnvironmentVariable("OMASHARP_LIB");
    if (lib.isEmpty()) lib = fi.absolutePath() + "/lib" + fi.fileName() + ".so";
    void *h = dlopen(QFile::encodeName(lib).constData(), RTLD_NOW | RTLD_LOCAL);
    if (!h) {
        fprintf(stderr, "omasharp: can't load %s: %s\n", qPrintable(lib), dlerror());
        return false;
    }
    auto fn = reinterpret_cast<omasharp_api_fn>(dlsym(h, "omasharp_api"));
    if (!fn) {
        fprintf(stderr, "omasharp: %s has no omasharp_api\n", qPrintable(lib));
        return false;
    }
    api = fn();
    return api != nullptr;
}

int runManaged(const char *dll, const char *exe);                               // devhost.cpp
QQmlPropertyMap *createTheme(QObject *parent);                                  // theme.cpp
void startTextDump(QQmlApplicationEngine &engine);                              // textdump.cpp
void omasharpShowDrawing(const char *name, const unsigned char *data, int len); // drawview.cpp
void registerDrawView();                                                        // drawview.cpp
double omasharpMonoAdvance();                                                   // drawview.cpp

static int s_argc;
static char **s_argv;

static QString option(const char *name) {
    for (int i = 1; i + 1 < s_argc; i++)
        if (qstrcmp(s_argv[i], name) == 0) return QString::fromLocal8Bit(s_argv[i + 1]);
    return {};
}

// Runs the app once the C# side is loaded. In dev mode the managed Main calls this.
extern "C" __attribute__((visibility("default"))) int omasharp_host_run(const omasharp_api *loaded) {
    api = loaded;
    if (api->version != OMASHARP_ABI) {
        fprintf(stderr, "omasharp: the host and the C# side are from different bridge versions (ABI %d vs %d); "
                        "merge any bridge/**/*.upstream files and rebuild\n",
                OMASHARP_ABI, api->version);
        return 1;
    }
    const QString exe = exePath();
    QGuiApplication app(s_argc, s_argv);
    const QJsonObject info = QJsonDocument::fromJson(take(api->app_info())).object();
    const QString id = info.value("id").toString();
    app.setApplicationName(id);
    app.setDesktopFileName(id); // the Wayland app_id, which window rules match on
    app.setWindowIcon(QIcon::fromTheme(id));

    Backend map(&app);
    backend = &map;
    // The app's own arguments: Qt already removed its options; drop ours (dev mode).
    QJsonArray args;
    const QStringList all = app.arguments();
    for (int i = 1; i < all.size(); i++) {
        if (all[i] == QLatin1String("--dev") || all[i] == QLatin1String("--qml-dir")) {
            i++;
            continue;
        }
        args.append(all[i]);
    }
    static QByteArray argsJson;
    argsJson = QJsonDocument(args).toJson(QJsonDocument::Compact);
    static omasharp_host host{onNotify, onPost, onRaised, nullptr, omasharpShowDrawing, 0, onResolve};
    host.args = argsJson.constData();
    host.mono_advance = omasharpMonoAdvance();
    registerDrawView();
    const QByteArray describe = take(api->start(&host));
    if (describe.isEmpty()) {
        fprintf(stderr, "omasharp: Backend failed to start\n");
        return 1;
    }
    const QJsonObject d = QJsonDocument::fromJson(describe).object();
    const QJsonObject props = d.value("props").toObject();
    for (auto it = props.begin(); it != props.end(); ++it) map.insert(it.key(), it.value().toVariant());
    QObject::connect(&app, &QCoreApplication::aboutToQuit, [] { api->shutdown(); });

    QQmlApplicationEngine engine;
    jsEngine = &engine;
    QObject::connect(&engine, &QQmlApplicationEngine::warnings, &app, [](const QList<QQmlError> &warnings) {
        for (const QQmlError &w : warnings) qWarning().noquote() << w.toString();
    });

    // One JS function per C# method, so QML can write backend.increment().
    const QJSValue makeMethod = engine.evaluate(QStringLiteral(
        "(function(b, n) { return function() { return b.call(n, Array.prototype.slice.call(arguments)) } })"));
    const QJSValue self = engine.newQObject(&map);
    QQmlEngine::setObjectOwnership(&map, QQmlEngine::CppOwnership);
    for (const QJsonValue &m : d.value("methods").toArray())
        if (!map.contains(m.toString())) map.insert(m.toString(), QVariant::fromValue(makeMethod.call({self, m.toString()})));
    map.freeze();

    engine.rootContext()->setContextProperty(QStringLiteral("backend"), &map);
    engine.rootContext()->setContextProperty(QStringLiteral("theme"), createTheme(&app));

    const QString qmlDir = option("--qml-dir");
    QUrl url;
    const QString rcc = QFileInfo(exe).absolutePath() + "/" + QFileInfo(exe).fileName() + ".rcc";
    if (!qmlDir.isEmpty())
        url = QUrl::fromLocalFile(QDir(qmlDir).absoluteFilePath("Main.qml"));
    else if (QFile::exists(QStringLiteral(":/Main.qml")) || QResource::registerResource(rcc))
        url = QUrl(QStringLiteral("qrc:/Main.qml"));
    else
        url = QUrl::fromLocalFile(QFileInfo(exe).absolutePath() + "/qml/Main.qml");
    engine.load(url);
    if (engine.rootObjects().isEmpty()) {
        fprintf(stderr, "omasharp: could not load %s\n", qPrintable(url.toString()));
        return 1;
    }
    startTextDump(engine);
    const int rc = app.exec();
    map.pending.clear();
    map.makeDeferred = QJSValue();
    return rc;
}

int main(int argc, char *argv[]) {
    s_argc = argc;
    s_argv = argv;
    // Omarchy sets QT_QPA_PLATFORMTHEME=gtk3 for every Qt app, which loads GTK 3 into the
    // process: about 55 ms before the window. These apps draw their own look; the portal
    // theme still gives the desktop's file chooser and colour scheme. OMASHARP_PLATFORMTHEME
    // overrides (set it to gtk3 to keep GTK's).
    const QByteArray theme = qgetenv("OMASHARP_PLATFORMTHEME");
    if (!theme.isEmpty())
        qputenv("QT_QPA_PLATFORMTHEME", theme);
    else if (qgetenv("QT_QPA_PLATFORMTHEME") == "gtk3")
        qputenv("QT_QPA_PLATFORMTHEME", "xdgdesktopportal");
    const QString dll = option("--dev");
    if (!dll.isEmpty() || !option("--qml-dir").isEmpty())
        qputenv("QT_FORCE_STDERR_LOGGING", "1"); // dev: QML errors to the terminal, not the journal
    if (!dll.isEmpty()) return runManaged(QFile::encodeName(dll).constData(), QFile::encodeName(exePath()).constData());
    if (!loadNative(exePath())) return 1;
    return omasharp_host_run(api);
}

#include "main.moc"
