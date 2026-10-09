using Microsoft.Data.Sqlite;

namespace PokeStudio.Core.Changes;

public sealed record ChangeLogEntry(long Id, DateTimeOffset TimestampUtc, string Area, string Description, ChangeAction Action, bool Saved);

/// <summary>
/// Persistent history of edits in the project's history.db (SQLite). It records what happened for the
/// Changes/History screens and survives crashes. Undo itself is in-memory per session (Phase 1).
/// </summary>
public sealed class ChangeLog : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TimeProvider _clock;

    public ChangeLog(string databasePath, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false };
        _connection = new SqliteConnection(builder.ToString());
        _connection.Open();
        Execute("""
            PRAGMA journal_mode = DELETE;
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT OR IGNORE INTO meta (key, value) VALUES ('schema', '1');
            CREATE TABLE IF NOT EXISTS changes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts TEXT NOT NULL,
                area TEXT NOT NULL,
                description TEXT NOT NULL,
                action TEXT NOT NULL,
                saved INTEGER NOT NULL DEFAULT 0
            );
            """);
    }

    public void Record(IChange change, ChangeAction action)
    {
        using var cmd = _connection.CreateCommand();
        if (action == ChangeAction.Merged)
        {
            cmd.CommandText = "UPDATE changes SET description = $desc WHERE id = (SELECT MAX(id) FROM changes)";
            cmd.Parameters.AddWithValue("$desc", change.Description);
            cmd.ExecuteNonQuery();
            return;
        }

        cmd.CommandText = "INSERT INTO changes (ts, area, description, action) VALUES ($ts, $area, $desc, $action)";
        cmd.Parameters.AddWithValue("$ts", _clock.GetUtcNow().ToString("O"));
        cmd.Parameters.AddWithValue("$area", change.Area);
        cmd.Parameters.AddWithValue("$desc", change.Description);
        cmd.Parameters.AddWithValue("$action", action.ToString());
        cmd.ExecuteNonQuery();
    }

    /// <summary>Marks everything recorded so far as saved.</summary>
    public void MarkSaved() => Execute("UPDATE changes SET saved = 1 WHERE saved = 0");

    public IReadOnlyList<ChangeLogEntry> ReadRecent(int limit = 500)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, ts, area, description, action, saved FROM changes ORDER BY id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var list = new List<ChangeLogEntry>();
        while (reader.Read())
        {
            list.Add(new ChangeLogEntry(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(2),
                reader.GetString(3),
                Enum.Parse<ChangeAction>(reader.GetString(4)),
                reader.GetInt64(5) == 1));
        }

        return list;
    }

    /// <summary>Hooks the log to a stack so every apply/undo/redo is recorded.</summary>
    public void Attach(UndoRedoStack stack) => stack.Changed += (_, e) => Record(e.Change, e.Action);

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
