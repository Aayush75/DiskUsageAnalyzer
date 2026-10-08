using System.Text.Json;
using DiskUsageAnalyzer.Core;
using Microsoft.Data.Sqlite;

namespace DiskUsageAnalyzer.Storage;

public sealed class ScanStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _sync = new();
    private const int ApplicationId = 0x44555341; // DUSA: never adopt an unrelated database.
    public string DatabasePath { get; }
    public ScanStore(string path)
    {
        DatabasePath = path;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bool existing = File.Exists(path);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, Mode = existing ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate }.ToString());
        try
        {
            _db.Open();
            var version = Convert.ToInt64(Scalar("PRAGMA user_version;"));
            if (version > 1) throw new InvalidDataException("This database needs a newer application. It has not been modified.");
            if (existing)
            {
                if (Convert.ToInt64(Scalar("PRAGMA application_id;")) != ApplicationId)
                    throw new InvalidDataException("The selected data folder contains an unrecognized scan.db. It has not been modified. Choose a new empty data folder; preserve existing recovery/history files.");
                _db.Dispose();
                _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadWrite }.ToString());
                _db.Open();
                if (Convert.ToInt64(Scalar("PRAGMA application_id;")) != ApplicationId || Convert.ToInt64(Scalar("PRAGMA user_version;")) > 1)
                    throw new InvalidDataException("The database changed while opening. It has not been adopted; choose a new empty data folder.");
            }
            Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA cache_size=-8192; PRAGMA busy_timeout=5000;");
            Execute("""
            CREATE TABLE IF NOT EXISTS files(id INTEGER PRIMARY KEY,parent INTEGER,path TEXT NOT NULL,name TEXT NOT NULL,dir INTEGER,bytes INTEGER,allocated INTEGER,identity TEXT,exclusion TEXT,rule TEXT,explanation TEXT,risk INTEGER,count INTEGER DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_parent ON files(parent,bytes DESC);
            CREATE INDEX IF NOT EXISTS ix_size ON files(dir,bytes DESC);
            CREATE INDEX IF NOT EXISTS ix_rule ON files(rule);
            CREATE TABLE IF NOT EXISTS queue(id INTEGER PRIMARY KEY,path TEXT,parent INTEGER,done INTEGER DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_queue ON queue(done,id);
            CREATE TABLE IF NOT EXISTS history(id INTEGER PRIMARY KEY,operation TEXT,original TEXT,recovery TEXT,identity TEXT,status TEXT,detail TEXT,time TEXT);
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT);
            PRAGMA user_version=1;
            PRAGMA application_id=1146442561;
            """);
        }
        catch { _db.Dispose(); throw; }
    }

    private object? Scalar(string sql) { using var c = _db.CreateCommand(); c.CommandText = sql; return c.ExecuteScalar(); }
    private void Execute(string sql) { using var c = _db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
    public void BeginScan(string root)
    {
        lock (_sync)
        {
            Execute("DELETE FROM files; DELETE FROM queue;"); // App-owned indexes only; never filesystem deletion.
            using var c = _db.CreateCommand(); c.CommandText = "INSERT OR REPLACE INTO meta VALUES('root',$root);"; c.Parameters.AddWithValue("$root", root); c.ExecuteNonQuery();
        }
    }
    public void Enqueue(long id, string path, long parent)
    {
        lock (_sync)
        {
            using var c = _db.CreateCommand(); c.CommandText = "INSERT INTO queue(id,path,parent) VALUES($id,$path,$parent)";
            c.Parameters.AddWithValue("$id", id); c.Parameters.AddWithValue("$path", path); c.Parameters.AddWithValue("$parent", parent); c.ExecuteNonQuery();
        }
    }
    public (long Id, string Path, long Parent)? Dequeue()
    {
        lock (_sync)
        {
            using var c = _db.CreateCommand(); c.CommandText = "SELECT id,path,parent FROM queue WHERE done=0 ORDER BY id LIMIT 1";
            (long Id, string Path, long Parent) result;
            using (var r = c.ExecuteReader()) { if (!r.Read()) return null; result = (r.GetInt64(0), r.GetString(1), r.GetInt64(2)); }
            using var u = _db.CreateCommand(); u.CommandText = "UPDATE queue SET done=1 WHERE id=$id"; u.Parameters.AddWithValue("$id", result.Id); u.ExecuteNonQuery();
            return result;
        }
    }
    public void InsertBatch(IEnumerable<FileEntry> entries)
    {
        lock (_sync)
        {
            using var tx = _db.BeginTransaction();
            using var c = _db.CreateCommand(); c.Transaction = tx;
            c.CommandText = "INSERT OR REPLACE INTO files VALUES($id,$parent,$path,$name,$dir,$bytes,$allocated,$identity,$exclusion,$rule,$explanation,$risk,$count)";
            foreach (var n in new[] { "id", "parent", "path", "name", "dir", "bytes", "allocated", "identity", "exclusion", "rule", "explanation", "risk", "count" }) c.Parameters.Add(new SqliteParameter("$" + n, DBNull.Value));
            foreach (var e in entries)
            {
                object?[] values = [e.Id, e.ParentId, e.Path, e.Name, e.IsDirectory ? 1 : 0, e.Bytes, e.Allocated, e.Identity is null ? null : JsonSerializer.Serialize(e.Identity), e.Exclusion, e.Rule, e.Explanation, (int)e.Risk, e.FileCount];
                for (int i = 0; i < values.Length; i++) c.Parameters[i].Value = values[i] ?? DBNull.Value;
                c.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
    public void Aggregate()
    {
        // Directory IDs precede their descendants, so descending IDs give bottom-up aggregation.
        long before = long.MaxValue;
        while (true)
        {
            lock (_sync)
            {
                using var q = _db.CreateCommand(); q.CommandText = "SELECT id FROM files WHERE dir=1 AND id<$before ORDER BY id DESC LIMIT 256"; q.Parameters.AddWithValue("$before", before);
                List<long> ids = []; using (var r = q.ExecuteReader()) while (r.Read()) ids.Add(r.GetInt64(0));
                if (ids.Count == 0) return;
                using var tx = _db.BeginTransaction();
                foreach (var id in ids)
                {
                    using var c = _db.CreateCommand(); c.Transaction = tx;
                    c.CommandText = "UPDATE files SET bytes=(SELECT COALESCE(SUM(bytes),0) FROM files WHERE parent=$id),allocated=(SELECT CASE WHEN COUNT(*)=COUNT(allocated) THEN COALESCE(SUM(allocated),0) ELSE NULL END FROM files WHERE parent=$id),count=(SELECT COALESCE(SUM(CASE WHEN dir=1 THEN count ELSE 1 END),0) FROM files WHERE parent=$id) WHERE id=$id";
                    c.Parameters.AddWithValue("$id", id); c.ExecuteNonQuery();
                }
                tx.Commit(); before = ids[^1];
            }
        }
    }
    public FileEntry? Get(long id) => Query("id=$id", [("$id", id)], 1).FirstOrDefault();
    public IReadOnlyList<FileEntry> Children(long parent, string filter = "", int offset = 0, int limit = 200)
        => Query("parent=$parent AND name LIKE $filter ESCAPE '\\'", [("$parent", parent), ("$filter", "%" + EscapeLike(filter) + "%")], limit, offset);
    public IReadOnlyList<FileEntry> Largest(string filter = "", int offset = 0, int limit = 200)
        => Query("dir=0 AND path LIKE $filter ESCAPE '\\'", [("$filter", "%" + EscapeLike(filter) + "%")], limit, offset);
    public IReadOnlyList<FileEntry> RuleFiles(string rule, int offset = 0, int limit = 200)
        => Query("dir=0 AND rule=$rule", [("$rule", rule)], limit, offset);
    public IReadOnlyList<FileEntry> FilesAfter(long id, int limit = 512)
        => Query("dir=0 AND id>$after", [("$after", id)], limit, 0, "id");
    public IReadOnlyList<FileEntry> EligibleBySize(long size, int offset = 0, int limit = 200)
        => Query("dir=0 AND exclusion IS NULL AND bytes=$size", [("$size", size)], limit, offset);
    public IReadOnlyList<long> DuplicateSizes()
    {
        lock (_sync)
        {
            using var c = _db.CreateCommand(); c.CommandText = "SELECT bytes FROM files WHERE dir=0 AND exclusion IS NULL AND bytes>0 GROUP BY bytes HAVING COUNT(*)>1 ORDER BY bytes DESC LIMIT 5000";
            List<long> sizes = []; using var r = c.ExecuteReader(); while (r.Read()) sizes.Add(r.GetInt64(0)); return sizes;
        }
    }
    public IReadOnlyList<(string Extension, long Bytes, long Count)> Types()
    {
        // The scanner stores normalized extensions in a separate aggregate, avoiding loading file records here.
        lock (_sync)
        {
            using var c = _db.CreateCommand(); c.CommandText = "SELECT value FROM meta WHERE key='types'";
            return JsonSerializer.Deserialize<List<(string Extension, long Bytes, long Count)>>(c.ExecuteScalar()?.ToString() ?? "[]", new JsonSerializerOptions { IncludeFields = true }) ?? [];
        }
    }
    public void SaveTypes(IEnumerable<(string Extension, long Bytes, long Count)> types)
    {
        lock (_sync)
        {
            using var c = _db.CreateCommand(); c.CommandText = "INSERT OR REPLACE INTO meta VALUES('types',$v)";
            c.Parameters.AddWithValue("$v", JsonSerializer.Serialize(types, new JsonSerializerOptions { IncludeFields = true })); c.ExecuteNonQuery();
        }
    }
    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    private List<FileEntry> Query(string where, (string, object)[] args, int limit, int offset = 0, string order = "bytes DESC,id")
    {
        if (limit is < 1 or > 10000 || offset < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_sync)
        {
            using var c = _db.CreateCommand(); c.CommandText = $"SELECT * FROM files WHERE {where} ORDER BY {order} LIMIT $limit OFFSET $offset";
            foreach (var (name, value) in args) c.Parameters.AddWithValue(name, value);
            c.Parameters.AddWithValue("$limit", limit); c.Parameters.AddWithValue("$offset", offset);
            List<FileEntry> rows = []; using var r = c.ExecuteReader();
            while (r.Read()) rows.Add(new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetInt64(4) != 0, r.GetInt64(5), r.IsDBNull(6) ? null : r.GetInt64(6),
                r.IsDBNull(7) ? null : JsonSerializer.Deserialize<FileIdentity>(r.GetString(7)), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), (Risk)r.GetInt32(11), r.GetInt64(12)));
            return rows;
        }
    }
    public void SetRule(long id, string? rule, string? explanation, Risk risk)
    {
        lock (_sync)
        {
            using var c = _db.CreateCommand(); c.CommandText = "UPDATE files SET rule=$rule,explanation=$e,risk=$r WHERE id=$id";
            c.Parameters.AddWithValue("$rule", (object?)rule ?? DBNull.Value); c.Parameters.AddWithValue("$e", (object?)explanation ?? DBNull.Value); c.Parameters.AddWithValue("$r", (int)risk); c.Parameters.AddWithValue("$id", id); c.ExecuteNonQuery();
        }
    }
    public long Journal(string operation, ManifestEntry entry, string? recovery)
    {
        lock (_sync)
        {
            using var c = _db.CreateCommand(); c.CommandText = "INSERT INTO history(operation,original,recovery,identity,status,detail,time) VALUES($op,$p,$recovery,$i,'Pending','Operation prepared; outcome not yet recorded.',$t); SELECT last_insert_rowid();";
            c.Parameters.AddWithValue("$op", operation); c.Parameters.AddWithValue("$p", entry.Path); c.Parameters.AddWithValue("$recovery", (object?)recovery ?? DBNull.Value); c.Parameters.AddWithValue("$i", JsonSerializer.Serialize(entry.Identity)); c.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O")); return Convert.ToInt64(c.ExecuteScalar());
        }
    }
    public void CompleteJournal(long id, string status, string detail)
    {
        lock (_sync)
        {
            using var c = _db.CreateCommand(); c.CommandText = "UPDATE history SET status=$s,detail=$d WHERE id=$id";
            c.Parameters.AddWithValue("$s", status); c.Parameters.AddWithValue("$d", detail); c.Parameters.AddWithValue("$id", id); c.ExecuteNonQuery();
        }
    }
    public IReadOnlyList<HistoryEntry> History(int limit = 200)
    {
        lock (_sync)
        {
            using var c = _db.CreateCommand(); c.CommandText = "SELECT * FROM history ORDER BY id DESC LIMIT $limit"; c.Parameters.AddWithValue("$limit", limit);
            List<HistoryEntry> entries = []; using var r = c.ExecuteReader();
            while (r.Read()) entries.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), JsonSerializer.Deserialize<FileIdentity>(r.GetString(4))!, r.GetString(5), r.GetString(6), DateTimeOffset.Parse(r.GetString(7))));
            return entries;
        }
    }
    public HistoryEntry? HistoryById(long id)
    {
        lock (_sync)
        {
            using var c = _db.CreateCommand(); c.CommandText = "SELECT * FROM history WHERE id=$id"; c.Parameters.AddWithValue("$id", id);
            using var r = c.ExecuteReader();
            return r.Read() ? new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), JsonSerializer.Deserialize<FileIdentity>(r.GetString(4))!, r.GetString(5), r.GetString(6), DateTimeOffset.Parse(r.GetString(7))) : null;
        }
    }
    public void Dispose() { lock (_sync) _db.Dispose(); }
}
