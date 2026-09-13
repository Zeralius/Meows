using System.Globalization;
using System.Text.Json;
using Meows.Plugins.Abstractions;
using Microsoft.Data.Sqlite;

namespace Meows.Services;

/// <summary>
/// The one SQLite file under <c>%APPDATA%\Meows</c>, and the schema in it.
///
/// A schema outlives the code that made it, so it is versioned from the first write through
/// <c>PRAGMA user_version</c>, and <see cref="Migrate"/> is the only place it changes. A
/// connection is opened per call rather than held: the file is small, the calls are few, and a
/// held connection is what stops the folder being deleted to reset Meows.
/// </summary>
public sealed class MeowsStore
{
    /// <summary>Bump when <see cref="Migrate"/> gains a step. Never edit an earlier step.</summary>
    public const int SchemaVersion = 1;

    private readonly string _path;
    private readonly Action<string> _log;

    public MeowsStore(string root, Action<string> log)
    {
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "meows.db");
        _log = log;

        try
        {
            using var connection = Open();
            Migrate(connection);
        }
        catch (Exception ex)
        {
            _log($"The store at {_path} could not be opened: {ex.Message}");
        }
    }

    public string FilePath => _path;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Migrate(SqliteConnection connection)
    {
        var version = (long)(Scalar(connection, "PRAGMA user_version") ?? 0L);

        if (version < 1)
        {
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS events (
                    id INTEGER PRIMARY KEY,
                    at TEXT NOT NULL,
                    plugin TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    subject TEXT NOT NULL,
                    detail TEXT,
                    data TEXT
                );
                CREATE INDEX IF NOT EXISTS events_plugin_at ON events(plugin, at DESC);
                CREATE INDEX IF NOT EXISTS events_kind ON events(plugin, kind, at DESC);

                CREATE TABLE IF NOT EXISTS facts (
                    plugin TEXT NOT NULL,
                    key TEXT NOT NULL,
                    value TEXT NOT NULL,
                    PRIMARY KEY (plugin, key)
                );

                CREATE TABLE IF NOT EXISTS seen (
                    hash TEXT PRIMARY KEY,
                    plugin TEXT NOT NULL,
                    at TEXT NOT NULL,
                    note TEXT
                );
                """);
            Execute(connection, "PRAGMA user_version = 1");
        }
    }

    /// <summary>The view one plugin gets: its own events and facts, and the shared seen table.</summary>
    public IMeowsStore For(string pluginId) => new PluginStore(this, pluginId);

    // ---- Events ----------------------------------------------------------------------------------

    public void Record(string plugin, string kind, string subject, string? detail, IReadOnlyDictionary<string, string>? data)
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO events (at, plugin, kind, subject, detail, data) VALUES ($at, $plugin, $kind, $subject, $detail, $data)";
            command.Parameters.AddWithValue("$at", Stamp(DateTime.UtcNow));
            command.Parameters.AddWithValue("$plugin", plugin);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$subject", subject);
            command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
            command.Parameters.AddWithValue("$data", data is null ? DBNull.Value : JsonSerializer.Serialize(data));
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _log($"Could not record '{kind}' for {plugin}: {ex.Message}");
        }
    }

    /// <summary>Events, newest first. Plugin, kind and text are each optional filters.</summary>
    public IReadOnlyList<StoredEvent> Events(string? plugin, string? kind, string? text, int limit)
    {
        var events = new List<StoredEvent>();
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();

            var where = new List<string>();
            if (plugin is not null)
            {
                where.Add("plugin = $plugin");
                command.Parameters.AddWithValue("$plugin", plugin);
            }
            if (kind is not null)
            {
                where.Add("kind = $kind");
                command.Parameters.AddWithValue("$kind", kind);
            }
            if (text is { Length: > 0 })
            {
                where.Add("(subject LIKE $text OR detail LIKE $text OR kind LIKE $text OR plugin LIKE $text)");
                command.Parameters.AddWithValue("$text", "%" + text + "%");
            }

            command.CommandText = "SELECT id, at, plugin, kind, subject, detail, data FROM events"
                                  + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
                                  + " ORDER BY id DESC LIMIT $limit";
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var json = reader.IsDBNull(6) ? null : reader.GetString(6);
                var data = json is null
                    ? new Dictionary<string, string>()
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];

                events.Add(new StoredEvent(
                    reader.GetInt64(0),
                    Parse(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    data));
            }
        }
        catch (Exception ex)
        {
            _log($"Could not read events: {ex.Message}");
        }

        return events;
    }

    /// <summary>The plugins that have ever written anything, for a filter.</summary>
    public IReadOnlyList<string> Plugins()
    {
        var plugins = new List<string>();
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT plugin FROM events ORDER BY plugin";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                plugins.Add(reader.GetString(0));
        }
        catch (Exception ex)
        {
            _log($"Could not list plugins in the store: {ex.Message}");
        }

        return plugins;
    }

    public long Count()
    {
        try
        {
            using var connection = Open();
            return (long)(Scalar(connection, "SELECT COUNT(*) FROM events") ?? 0L);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // ---- Keeping it small --------------------------------------------------------------------------
    //
    // The journal grows forever by design: nothing in the app deletes a line. That is right for a
    // record and wrong for a file, so the History tab says how big the file is and offers the two
    // things worth doing about it. Facts and the seen table are never touched here: the hashes are
    // the point of the seen table, and a fact forgotten is a plugin misremembering.

    /// <summary>The database file's size on disk, or 0 when it cannot be read.</summary>
    public long FileSize
    {
        get
        {
            try
            {
                return new FileInfo(_path).Length;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    /// <summary>How many lines are older than a cutoff, which is what a confirmation has to say.</summary>
    public long CountOlderThan(DateTime cutoffUtc)
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM events WHERE at < $cutoff";
            command.Parameters.AddWithValue("$cutoff", Stamp(cutoffUtc));
            return (long)(command.ExecuteScalar() ?? 0L);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Removes lines older than a cutoff and says how many went. Not undoable, which is why it asks first.</summary>
    public long Forget(DateTime cutoffUtc)
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM events WHERE at < $cutoff";
            command.Parameters.AddWithValue("$cutoff", Stamp(cutoffUtc));
            var gone = command.ExecuteNonQuery();
            _log($"Forgot {gone} history line(s) older than {cutoffUtc:yyyy-MM-dd}");
            return gone;
        }
        catch (Exception ex)
        {
            _log($"Could not forget old history: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Gives the room back. SQLite keeps deleted pages for reuse rather than shrinking the file,
    /// so forgetting on its own changes the count and not the size; this does the size.
    /// </summary>
    public bool Compact()
    {
        try
        {
            using var connection = Open();
            Execute(connection, "VACUUM");
            return true;
        }
        catch (Exception ex)
        {
            _log($"Could not compact the store: {ex.Message}");
            return false;
        }
    }

    // ---- Facts -----------------------------------------------------------------------------------

    public string? Get(string plugin, string key)
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM facts WHERE plugin = $plugin AND key = $key";
            command.Parameters.AddWithValue("$plugin", plugin);
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
        catch (Exception ex)
        {
            _log($"Could not read fact '{key}' for {plugin}: {ex.Message}");
            return null;
        }
    }

    public void Set(string plugin, string key, string value)
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO facts (plugin, key, value) VALUES ($plugin, $key, $value) ON CONFLICT(plugin, key) DO UPDATE SET value = excluded.value";
            command.Parameters.AddWithValue("$plugin", plugin);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _log($"Could not write fact '{key}' for {plugin}: {ex.Message}");
        }
    }

    public void Remove(string plugin, string key)
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM facts WHERE plugin = $plugin AND key = $key";
            command.Parameters.AddWithValue("$plugin", plugin);
            command.Parameters.AddWithValue("$key", key);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _log($"Could not remove fact '{key}' for {plugin}: {ex.Message}");
        }
    }

    // ---- Seen ------------------------------------------------------------------------------------

    public void MarkSeen(string plugin, string hash, string? note)
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            // First sighting wins. Who saw it first is the interesting answer.
            command.CommandText = "INSERT OR IGNORE INTO seen (hash, plugin, at, note) VALUES ($hash, $plugin, $at, $note)";
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$plugin", plugin);
            command.Parameters.AddWithValue("$at", Stamp(DateTime.UtcNow));
            command.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _log($"Could not mark {hash} seen: {ex.Message}");
        }
    }

    public SeenRecord? Seen(string hash)
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT hash, plugin, at, note FROM seen WHERE hash = $hash";
            command.Parameters.AddWithValue("$hash", hash);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;
            return new SeenRecord(reader.GetString(0), reader.GetString(1), Parse(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3));
        }
        catch (Exception ex)
        {
            _log($"Could not look up {hash}: {ex.Message}");
            return null;
        }
    }

    // ---- Plumbing --------------------------------------------------------------------------------

    private static string Stamp(DateTime utc) => utc.ToString("O", CultureInfo.InvariantCulture);

    private static DateTime Parse(string stamp) =>
        DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
            ? when.ToLocalTime()
            : DateTime.MinValue;

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>The scoped view. Every call carries the plugin id; nothing else is remembered.</summary>
    private sealed class PluginStore(MeowsStore store, string plugin) : IMeowsStore
    {
        public void Record(string kind, string subject, string? detail = null, IReadOnlyDictionary<string, string>? data = null) =>
            store.Record(plugin, kind, subject, detail, data);

        public IReadOnlyList<StoredEvent> Recent(int limit = 100, string? kind = null) =>
            store.Events(plugin, kind, null, limit);

        public IReadOnlyList<StoredEvent> Search(string text, int limit = 100) =>
            store.Events(plugin, null, text, limit);

        public string? Get(string key) => store.Get(plugin, key);

        public void Set(string key, string value) => store.Set(plugin, key, value);

        public void Remove(string key) => store.Remove(plugin, key);

        public void MarkSeen(string hash, string? note = null) => store.MarkSeen(plugin, hash, note);

        public SeenRecord? Seen(string hash) => store.Seen(hash);
    }
}
