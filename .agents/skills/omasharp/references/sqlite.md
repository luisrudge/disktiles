# Storing data: SQLite

Use the system's SQLite through SQLitePCLRaw. Qt itself depends on `libsqlite3`, so every machine that runs the app has it, and the app ships nothing extra. It's native-AOT clean and adds about 50 KB. It works the same in window apps and panels: dev mode, the release build, after `-t:Install`, and in unit tests.

In the app's `.csproj`:

```xml
  <ItemGroup>
    <PackageReference Include="SQLitePCLRaw.core" Version="3.0.5" />
    <PackageReference Include="SQLitePCLRaw.provider.sqlite3" Version="3.0.5" />
  </ItemGroup>
```

**Not `Microsoft.Data.Sqlite`.** It bundles its own `libe_sqlite3.so` (2 MB more), and a window app's `-t:Install` copies only the app's own files, so the installed app fails with `DllNotFoundException: e_sqlite3`. If you want its ADO.NET API anyway, reference `Microsoft.Data.Sqlite.Core` and `SQLitePCLRaw.bundle_sqlite3` (2.1.x) instead. That also uses the system library and needs no setup code, but makes the app about 0.5 MB larger than the setup here.

## Db.cs

A small helper over SQLite's own API. Copy it into the app as `Db.cs`:

```csharp
using SQLitePCL;

// SQLite through the system's libsqlite3 (SQLitePCLRaw.provider.sqlite3): nothing to ship.
sealed class Db : IDisposable
{
    static Db() => raw.SetProvider(new SQLite3Provider_sqlite3());

    readonly sqlite3 _db;

    public Db(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Check(raw.sqlite3_open(path, out _db));
        // A second copy of the app may be writing: wait up to 3 s for it instead of failing.
        Check(raw.sqlite3_busy_timeout(_db, 3000));
        // Fewer disk syncs per write; a crash of the app can't corrupt the file.
        Exec("PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL");
    }

    // Statements without parameters or results (CREATE TABLE ..., several at once).
    public void Exec(string sql) => Check(raw.sqlite3_exec(_db, sql));

    // INSERT/UPDATE/DELETE with ? parameters; returns the rows it changed.
    public int Run(string sql, params ReadOnlySpan<object?> args)
    {
        using var st = Prepare(sql, args);
        Check(raw.sqlite3_step(st), raw.SQLITE_DONE);
        return raw.sqlite3_changes(_db);
    }

    // SELECT with ? parameters: `row` turns the current row into a T.
    public List<T> Query<T>(string sql, Func<sqlite3_stmt, T> row, params ReadOnlySpan<object?> args)
    {
        using var st = Prepare(sql, args);
        var rows = new List<T>();
        int rc;
        while ((rc = raw.sqlite3_step(st)) == raw.SQLITE_ROW) rows.Add(row(st));
        Check(rc, raw.SQLITE_DONE);
        return rows;
    }

    // All of `work` is written, or none of it (one disk sync instead of one per statement).
    public void Transaction(Action work)
    {
        Exec("BEGIN IMMEDIATE");
        try { work(); Exec("COMMIT"); }
        catch
        {
            if (raw.sqlite3_get_autocommit(_db) == 0) Exec("ROLLBACK"); // some errors roll back by themselves
            throw;
        }
    }

    public long LastInsertId => raw.sqlite3_last_insert_rowid(_db);

    public void Dispose() => _db.Dispose();

    sqlite3_stmt Prepare(string sql, ReadOnlySpan<object?> args)
    {
        Check(raw.sqlite3_prepare_v2(_db, sql, out var st));
        try
        {
            for (var i = 0; i < args.Length; i++)
                Check(args[i] switch
                {
                    null => raw.sqlite3_bind_null(st, i + 1),
                    long v => raw.sqlite3_bind_int64(st, i + 1, v),
                    int v => raw.sqlite3_bind_int(st, i + 1, v),
                    bool v => raw.sqlite3_bind_int(st, i + 1, v ? 1 : 0),
                    double v => raw.sqlite3_bind_double(st, i + 1, v),
                    string v => raw.sqlite3_bind_text(st, i + 1, v),
                    byte[] v => raw.sqlite3_bind_blob(st, i + 1, v),
                    var v => throw new ArgumentException($"SQLite can't store a {v.GetType().Name}"),
                });
            return st;
        }
        catch { st.Dispose(); throw; }
    }

    void Check(int rc, int ok = raw.SQLITE_OK)
    {
        if (rc != ok) throw new InvalidOperationException($"SQLite: {raw.sqlite3_errmsg(_db).utf8_to_string()}");
    }
}
```

## Using it from Backend

```csharp
using Omasharp;
using SQLitePCL;

public record Note(long Id, string Text);

public sealed class Backend : BackendBase, IDisposable
{
    readonly Db _db;
    List<Note> _notes;

    // $XDG_DATA_HOME/<AppId>/app.db, usually ~/.local/share/<AppId>/app.db.
    public Backend() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        AppInfo.Id, "app.db")) { }

    // Tests pass a temporary file.
    internal Backend(string path)
    {
        _db = new Db(path);
        Migrate();
        _notes = Load();
    }

    // One step per schema change, each setting user_version. All in one transaction that reads the
    // version itself: an app stopped halfway redoes nothing, and a second copy starting at the same
    // time waits for this one, then skips the steps.
    void Migrate() => _db.Transaction(() =>
    {
        var version = _db.Query("PRAGMA user_version", r => raw.sqlite3_column_int(r, 0))[0];
        if (version < 1) _db.Exec("CREATE TABLE notes(id INTEGER PRIMARY KEY, text TEXT NOT NULL); PRAGMA user_version = 1");
        // Later: if (version < 2) _db.Exec("ALTER TABLE notes ADD COLUMN done INTEGER NOT NULL DEFAULT 0; PRAGMA user_version = 2");
    });

    public List<Note> Notes { get => _notes; private set => Set(ref _notes, value); }

    public void Add(string text)
    {
        _db.Run("INSERT INTO notes(text) VALUES (?)", text);
        Notes = Load();
    }

    public void Remove(long id)
    {
        _db.Run("DELETE FROM notes WHERE id = ?", id);
        Notes = Load();
    }

    public void Dispose() => _db.Dispose();

    List<Note> Load() => _db.Query("SELECT id, text FROM notes ORDER BY id",
        r => new Note(raw.sqlite3_column_int64(r, 0), raw.sqlite3_column_text(r, 1).utf8_to_string()));
}
```

QML gets `backend.notes` as a JS array of `{ id, text }` and calls `backend.add(text)` and `backend.remove(id)`.

- **Where the database goes:** `$XDG_DATA_HOME/<AppId>/`, as above. `LocalApplicationData` is `$XDG_DATA_HOME` (or `~/.local/share`) on Linux, and `AppInfo.Id` is the csproj's `AppId`. Keep `SpecialFolderOption.DoNotVerify`: without it, `GetFolderPath` returns `""` when the folder doesn't exist yet, and the database lands in whatever the current directory is.
- **Checks** (`check.sh`) start every run with an empty `$XDG_DATA_HOME`, so the app makes a new database, and `--keys` never reach the user's notes. To check with rows already there, seed it with `--seed DIR` ([checking.md](checking.md)).
- **Panels:** the build runs `Backend`'s constructor once, to write `plugin/Backend.qml`, so it opens (and creates) the database too. Keep the constructor to opening, migrating and reading.
- **Always use `?` parameters** for values. Never build SQL from strings.
- **Map rows by hand, no reflection ORMs.** Read columns with `raw.sqlite3_column_int64`, `_int`, `_double`, `_text(r, i).utf8_to_string()` or `_blob(r, i).ToArray()` into your own records, as in `Load()`. EF Core and Dapper rely on reflection and runtime code generation, which native AOT doesn't have.
- **Speed:** opening the database and running the first query take about 0.6 ms, so small queries on the UI thread are fine. Each statement outside a transaction is its own commit (a disk sync), so wrap bulk writes in `_db.Transaction(() => ...)`. Run long queries with `Task.Run` ([backend.md](backend.md)), and use a `Db` from one thread at a time.
- **Schema changes:** add a step to `Migrate()` with the next `user_version`. Never edit a step that has shipped: databases out there already ran it. Keep the version read inside the transaction.
- **Closing:** the bridge calls `Backend.Dispose()` when the app exits (window closed, or the shell closed the panel).
- **Unit tests:** `tests/*.csproj` compiles the app's files itself. Add `../Db.cs` to its `Compile` line and the same two `PackageReference`s. The tests also have to declare `AppInfo` themselves, as they do `AppBackend`: add `tests/AppInfo.cs` containing `namespace Omasharp { static class AppInfo { public const string Id = "tests"; } }`, and test with `new Backend(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db"))`.
