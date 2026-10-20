// Dev mode, which the release build never uses: runManaged starts the normal .NET
// runtime through hostfxr and runs the app's managed Main (bridge/cs/Dev.cs), which
// calls back into omasharp_host_run. Running Main is the plain `dotnet App.dll` path,
// so the app starts the way any .NET program does.
#include <QDir>
#include <QFile>
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
