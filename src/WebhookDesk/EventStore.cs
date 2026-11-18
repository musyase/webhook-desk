using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace WebhookDesk;

public sealed record StoredEvent(string Id, string Source, string? ExternalId, string ReceivedAt,
    string ContentType, string Sha256, byte[] Body, bool Duplicate = false);

public sealed record EventSummary(string Id, string Source, string? ExternalId, string ReceivedAt,
    string ContentType, string Sha256, int Bytes);

public sealed record ReplayAttempt(long Id, string EventId, string AttemptedAt, int? StatusCode, string? Error);

public sealed class EventStore(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false
    }.ToString();

    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public void Initialize()
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS events (
                id TEXT PRIMARY KEY,
                source TEXT NOT NULL,
                external_id TEXT,
                received_at TEXT NOT NULL,
                content_type TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                body BLOB NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS events_external_id
                ON events(source, external_id) WHERE external_id IS NOT NULL;
            CREATE TABLE IF NOT EXISTS attempts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL REFERENCES events(id),
                attempted_at TEXT NOT NULL,
                status_code INTEGER,
                error TEXT
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public StoredEvent Add(string source, string? externalId, string contentType, byte[] body)
    {
        var item = new StoredEvent(Guid.NewGuid().ToString("N"), source, externalId,
            DateTimeOffset.UtcNow.ToString("O"), contentType,
            Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant(), body);
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO events(id, source, external_id, received_at, content_type, sha256, body)
            VALUES ($id, $source, $external, $time, $type, $hash, $body)
            """;
        cmd.Parameters.AddWithValue("$id", item.Id);
        cmd.Parameters.AddWithValue("$source", item.Source);
        cmd.Parameters.AddWithValue("$external", (object?)item.ExternalId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$time", item.ReceivedAt);
        cmd.Parameters.AddWithValue("$type", item.ContentType);
        cmd.Parameters.AddWithValue("$hash", item.Sha256);
        cmd.Parameters.AddWithValue("$body", item.Body);
        if (cmd.ExecuteNonQuery() == 1) return item;

        using var existing = db.CreateCommand();
        existing.CommandText = "SELECT id FROM events WHERE source = $source AND external_id = $external";
        existing.Parameters.AddWithValue("$source", source);
        existing.Parameters.AddWithValue("$external", externalId!);
        var id = (string?)existing.ExecuteScalar() ?? throw new InvalidOperationException("Duplicate event disappeared.");
        return Get(id)! with { Duplicate = true };
    }

    public StoredEvent? Get(string id)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, source, external_id, received_at, content_type, sha256, body
            FROM events WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var row = cmd.ExecuteReader();
        if (!row.Read()) return null;
        return new StoredEvent(row.GetString(0), row.GetString(1), row.IsDBNull(2) ? null : row.GetString(2),
            row.GetString(3), row.GetString(4), row.GetString(5), row.GetFieldValue<byte[]>(6));
    }

    public IReadOnlyList<EventSummary> List(string? source, int limit)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, source, external_id, received_at, content_type, sha256, length(body)
            FROM events WHERE ($source IS NULL OR source = $source)
            ORDER BY received_at DESC, id DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$source", (object?)source ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var row = cmd.ExecuteReader();
        var result = new List<EventSummary>();
        while (row.Read())
            result.Add(new EventSummary(row.GetString(0), row.GetString(1),
                row.IsDBNull(2) ? null : row.GetString(2), row.GetString(3), row.GetString(4),
                row.GetString(5), row.GetInt32(6)));
        return result;
    }

    public void RecordAttempt(string eventId, int? statusCode, string? error)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO attempts(event_id, attempted_at, status_code, error)
            VALUES ($event, $time, $status, $error)
            """;
        cmd.Parameters.AddWithValue("$event", eventId);
        cmd.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$status", (object?)statusCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ReplayAttempt> Attempts(string eventId)
    {
        using var db = Connect();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, event_id, attempted_at, status_code, error
            FROM attempts WHERE event_id = $event ORDER BY id DESC
            """;
        cmd.Parameters.AddWithValue("$event", eventId);
        using var row = cmd.ExecuteReader();
        var result = new List<ReplayAttempt>();
        while (row.Read())
            result.Add(new ReplayAttempt(row.GetInt64(0), row.GetString(1), row.GetString(2),
                row.IsDBNull(3) ? null : row.GetInt32(3), row.IsDBNull(4) ? null : row.GetString(4)));
        return result;
    }
}
