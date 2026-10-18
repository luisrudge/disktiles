// The C ABI between the host and the C# side. Must match bridge/cs/Native.cs.
#pragma once

// Bump on both sides whenever either struct changes; the host refuses a mismatched app.
#define OMASHARP_ABI 3

extern "C" {

struct omasharp_host {
    void (*notify)(const char *name, const char *json);                 // UI thread only
    void (*post)(long long token);                                      // any thread; run api->run_posted(token) on the UI thread
    void (*method)(const char *name);                                   // dev hot reload: a method appeared on Backend
    void (*raised)(const char *name, const char *json);                 // C# Raise(): QML gets backend.raised(name, value)
    const char *args;                                                   // JSON array: the app's arguments (host options removed)
    void (*draw)(const char *name, const unsigned char *data, int len); // Bridge.Show: a Drawing for DrawView
    double mono_advance;                                                // monospace character width per pixel of font size
    void (*resolve)(long long id, const char *text, int is_error);      // an async call finished (UI thread)
};

struct omasharp_api {
    int version;
    char *(*app_info)();                                              // {"id":..,"name":..,"runtime":..}
    char *(*start)(const omasharp_host *host);                        // describe JSON, or null on failure
    char *(*get)(const char *name, int *is_error);                    // JSON value | error message
    char *(*set)(const char *name, const char *json, int *is_error);  // null | error message
    char *(*call)(const char *name, const char *args, int *is_error); // JSON value | error message | (is_error 2) async call id
    void (*free)(char *p);
    void (*run_posted)(long long token);
    void (*shutdown)();
};

typedef const omasharp_api *(*omasharp_api_fn)();
}
