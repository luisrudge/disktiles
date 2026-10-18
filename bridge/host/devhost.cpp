// Dev mode. Two pieces the release build never uses:
//  - runManaged: start the normal .NET runtime through hostfxr and run the app's
//    managed Main (bridge/cs/Dev.cs), which calls back into omasharp_host_run.
//    Running Main (rather than just fetching a function pointer) is what makes
//    startup hooks run, and with them `dotnet watch` hot reload.
//  - watchQml: reload the window when a .qml file under --qml-dir is saved.
#include <QDir>
#include <QDateTime>
#include <QDirIterator>
#include <QHash>
#include <QFileSystemWatcher>
#include <QGuiApplication>
#include <QQmlApplicationEngine>
#include <QTimer>
#include <QVersionNumber>

#include <cstdio>
#include <cstdlib>
#include <dlfcn.h>

extern "C" {
typedef void *hostfxr_handle;
struct hostfxr_initialize_parameters {
    size_t size;
    const char *host_path;
    const char *dotnet_root;
};
typedef int (*hostfxr_initialize_for_dotnet_command_line_fn)(int argc, const char **argv,
                                                             const hostfxr_initialize_parameters *parameters, hostfxr_handle *handle);
typedef int (*hostfxr_run_app_fn)(hostfxr_handle handle);
typedef int (*hostfxr_close_fn)(hostfxr_handle handle);
}

static QString dotnetRoot() {
    QStringList roots{qEnvironmentVariable("DOTNET_ROOT"), QStringLiteral("/usr/share/dotnet"),
                      QStringLiteral("/usr/lib/dotnet"), QDir::homePath() + "/.dotnet"};
    for (const QString &r : roots)
        if (!r.isEmpty() && QDir(r + "/host/fxr").exists()) return r;
    return {};
}

static QString hostfxrPath(const QString &root) {
    QVersionNumber best;
    QString path;
    for (const QString &v : QDir(root + "/host/fxr").entryList(QDir::Dirs | QDir::NoDotAndDotDot)) {
        const QVersionNumber n = QVersionNumber::fromString(v);
        if (n > best && QFile::exists(root + "/host/fxr/" + v + "/libhostfxr.so")) {
            best = n;
            path = root + "/host/fxr/" + v + "/libhostfxr.so";
        }
    }
    return path;
}

int runManaged(const char *dll, const char *exe) {
    const QString root = dotnetRoot();
    const QString fxr = root.isEmpty() ? QString() : hostfxrPath(root);
    if (fxr.isEmpty()) {
        fprintf(stderr, "omasharp: no .NET runtime found (set DOTNET_ROOT)\n");
        return 1;
    }
    void *h = dlopen(QFile::encodeName(fxr).constData(), RTLD_NOW | RTLD_LOCAL);
    if (!h) {
        fprintf(stderr, "omasharp: %s\n", dlerror());
        return 1;
    }
    auto init = reinterpret_cast<hostfxr_initialize_for_dotnet_command_line_fn>(dlsym(h, "hostfxr_initialize_for_dotnet_command_line"));
    auto run = reinterpret_cast<hostfxr_run_app_fn>(dlsym(h, "hostfxr_run_app"));
    auto close = reinterpret_cast<hostfxr_close_fn>(dlsym(h, "hostfxr_close"));
    if (!init || !run || !close) {
        fprintf(stderr, "omasharp: unexpected hostfxr at %s\n", qPrintable(fxr));
        return 1;
    }

    const QByteArray rootBytes = QFile::encodeName(root);
    const hostfxr_initialize_parameters params{sizeof(hostfxr_initialize_parameters), exe, rootBytes.constData()};
    const char *argv[] = {dll};
    hostfxr_handle ctx = nullptr;
    int rc = init(1, argv, &params, &ctx);
    if (rc < 0 || !ctx) {
        fprintf(stderr, "omasharp: hostfxr init failed for %s (0x%x)\n", dll, unsigned(rc));
        return 1;
    }
    rc = run(ctx);
    close(ctx);
    return rc;
}

// The .qml/.js files under dir (not bin/ or obj/), with their modification times.
static QHash<QString, QDateTime> qmlFiles(const QString &dir) {
    QHash<QString, QDateTime> files;
    QDirIterator it(dir, {QStringLiteral("*.qml"), QStringLiteral("*.js")}, QDir::Files, QDirIterator::Subdirectories);
    while (it.hasNext()) {
        const QString f = it.next();
        if (!f.contains("/bin/") && !f.contains("/obj/")) files.insert(f, it.fileInfo().lastModified());
    }
    return files;
}

void watchQml(QQmlApplicationEngine &engine, const QString &dir, const QUrl &url) {
    auto *watcher = new QFileSystemWatcher(&engine);
    auto *seen = new QHash<QString, QDateTime>(qmlFiles(dir));
    auto watchAll = [watcher, dir, seen] {
        watcher->addPath(dir);
        for (const QString &f : seen->keys())
            if (!watcher->files().contains(f)) watcher->addPath(f);
    };
    watchAll();

    auto *debounce = new QTimer(&engine);
    debounce->setSingleShot(true);
    debounce->setInterval(80);
    QObject::connect(debounce, &QTimer::timeout, &engine, [&engine, url, watchAll, seen, dir] {
        // Only QML/JS changes matter (the folder also holds Backend.cs, obj/, ...).
        const auto now = qmlFiles(dir);
        if (now == *seen) return;
        *seen = now;
        // Editors often save by replacing the file, which drops the watch: re-add.
        watchAll();
        qGuiApp->setQuitOnLastWindowClosed(false);
        for (QObject *root : engine.rootObjects()) delete root;
        engine.clearComponentCache();
        engine.load(url);
        if (engine.rootObjects().isEmpty())
            fprintf(stderr, "omasharp: QML has errors; fix and save to reload\n");
        else {
            qGuiApp->setQuitOnLastWindowClosed(true);
            fprintf(stderr, "omasharp: reloaded QML\n");
        }
    });
    QObject::connect(watcher, &QFileSystemWatcher::fileChanged, debounce, qOverload<>(&QTimer::start));
    QObject::connect(watcher, &QFileSystemWatcher::directoryChanged, debounce, qOverload<>(&QTimer::start));
}
