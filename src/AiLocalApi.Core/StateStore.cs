using AiLocalApi.Contracts;
using Microsoft.Data.Sqlite;

namespace AiLocalApi.Core;

public sealed record Settings(string? ModelDirectory = null, string? DefaultModel = null, int Port = 17840, int IdleSeconds = 300, bool KeepLoaded = false, string Backend = "cpu", string? ApiKey = null, int RetentionDays = 7, long MaxUploadBytes = 536870912, int MaxDurationSeconds = 7200, int MaxQueued = 100, long MaxTemporaryBytes = 2147483648, int MaxExecutionSeconds = 7200, string DownloadHost = "huggingface.co");
public sealed record ModelInfo(string Id, string Path, string Sha256, long Size, bool Multilingual, string Quantization, string Source);
public sealed record Job(string Id, string Model, string Input, TranscriptionOptions Options, string State, string Stage, DateTimeOffset Created, DateTimeOffset Updated, int Attempts = 0, string? Error = null, Transcript? Result = null);

public sealed class StateStore
{
    private readonly AppPaths paths;
    private readonly object sync = new();
    private long revision;
    public long Revision => Interlocked.Read(ref revision);
    public StateStore(AppPaths paths)
    {
        this.paths = paths; paths.Initialize();
        using var db = Open();
        var version = Convert.ToInt32(Scalar(db, "PRAGMA user_version"));
        if (version > 2) throw new AppException("database_newer", "数据库版本较新，请使用兼容版本；未执行写入。", 503);
        if (version < 2)
        {
            if (new FileInfo(paths.Database).Length > 0)
            {
                using var backup = new SqliteConnection($"Data Source={paths.Database}.v{version}.{DateTime.UtcNow:yyyyMMddHHmmssfff}.bak");
                backup.Open(); db.BackupDatabase(backup);
            }
            using var transaction = db.BeginTransaction();
            try
            {
                Execute(db, "CREATE TABLE IF NOT EXISTS settings(id INTEGER PRIMARY KEY CHECK(id=1), json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS models(id TEXT PRIMARY KEY,json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY,state TEXT NOT NULL,created TEXT NOT NULL,json TEXT NOT NULL); CREATE INDEX IF NOT EXISTS jobs_state_created ON jobs(state,created); CREATE INDEX IF NOT EXISTS jobs_created ON jobs(created DESC,id); CREATE INDEX IF NOT EXISTS jobs_expiry ON jobs(state,json_extract(json,'$.updated')); PRAGMA user_version=2;", transaction);
                transaction.Commit();
            }
            catch { paths.Log("database_migration_failed"); throw; }
        }
        Execute(db, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;");
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.Database, Pooling = false }.ToString());
        db.Open(); return db;
    }
    private static object? Scalar(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private static void Execute(SqliteConnection db, string sql, SqliteTransaction? transaction = null)
    { using var command = db.CreateCommand(); command.CommandText = sql; command.Transaction = transaction; command.ExecuteNonQuery(); }
    private T? Read<T>(string table, string id)
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = $"SELECT json FROM {table} WHERE id=$id"; command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string json ? Wire.Decode<T>(json) : default;
    }
    private void Write<T>(string table, string id, T value)
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = $"INSERT INTO {table}(id,json) VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$json", Wire.Encode(value)); command.ExecuteNonQuery(); Interlocked.Increment(ref revision);
    }
    public Settings Settings { get { lock (sync) return Read<Settings>("settings", "1") ?? new(); } }
    public void SaveSettings(Settings value)
    {
        if (value.Port is < 1024 or > 65535 || value.IdleSeconds < 0 || value.RetentionDays < 1 || value.MaxQueued < 1 || value.MaxUploadBytes < 1 || value.MaxTemporaryBytes < value.MaxUploadBytes || value.MaxDurationSeconds is < 1 or > 14400 || value.MaxExecutionSeconds < 1)
            throw new AppException("invalid_settings", "端口、保留期或资源限制无效（最长音频 4 小时）。");
        if (value.Backend is not ("cpu" or "auto" or "cuda" or "vulkan")) throw new AppException("invalid_backend", "后端必须为 cpu/auto/cuda/vulkan。");
        if (value.DownloadHost is not ("huggingface.co" or "hf-mirror.com")) throw new AppException("invalid_download_host", "请选择支持的模型下载源。");
        lock (sync) Write("settings", "1", value);
    }
    public ModelInfo? Model(string id) { lock (sync) return Read<ModelInfo>("models", id); }
    public void SaveModel(ModelInfo model) { lock (sync) Write("models", model.Id, model); }
    public ModelInfo[] Models() { lock (sync) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT json FROM models ORDER BY id"; using var reader = command.ExecuteReader(); var result = new List<ModelInfo>(); while (reader.Read()) result.Add(Wire.Decode<ModelInfo>(reader.GetString(0))); return result.ToArray(); } }
    public void RemoveModel(string id) { lock (sync) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "DELETE FROM models WHERE id=$id"; command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery(); Interlocked.Increment(ref revision); } }
    public Job? Job(string id) { lock (sync) return Read<Job>("jobs", id); }
    public Job[] Jobs(int page = 0, int size = 50, string? state = null)
    {
        lock (sync)
        {
            using var db = Open(); using var command = db.CreateCommand();
            command.CommandText = "SELECT json FROM jobs WHERE ($state IS NULL OR state=$state) ORDER BY created DESC,id LIMIT $size OFFSET $offset";
            command.Parameters.AddWithValue("$state", (object?)state ?? DBNull.Value); command.Parameters.AddWithValue("$size", Math.Clamp(size, 1, 100)); command.Parameters.AddWithValue("$offset", Math.Max(0, page) * (long)Math.Clamp(size, 1, 100));
            using var reader = command.ExecuteReader(); var list = new List<Job>(); while (reader.Read()) list.Add(Wire.Decode<Job>(reader.GetString(0))); return list.ToArray();
        }
    }
    public int Count(string state) { lock (sync) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT count(*) FROM jobs WHERE state=$state"; command.Parameters.AddWithValue("$state", state); return Convert.ToInt32(command.ExecuteScalar()); } }
    public Job[] ExpiredBefore(DateTimeOffset cutoff)
    {
        lock (sync)
        {
            using var db = Open(); using var command = db.CreateCommand();
            command.CommandText = "SELECT json FROM jobs WHERE state IN ('succeeded','failed','cancelled') AND json_extract(json,'$.updated') < $cutoff ORDER BY created LIMIT 100";
            command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
            using var reader = command.ExecuteReader(); var jobs = new List<Job>(); while (reader.Read()) jobs.Add(Wire.Decode<Job>(reader.GetString(0))); return jobs.ToArray();
        }
    }
    private void WriteJob(Job job)
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "INSERT INTO jobs(id,state,created,json) VALUES($id,$state,$created,$json) ON CONFLICT(id) DO UPDATE SET state=excluded.state,json=excluded.json";
        command.Parameters.AddWithValue("$id", job.Id); command.Parameters.AddWithValue("$state", job.State); command.Parameters.AddWithValue("$created", job.Created.ToString("O")); command.Parameters.AddWithValue("$json", Wire.Encode(job)); command.ExecuteNonQuery(); Interlocked.Increment(ref revision);
    }
    public void AddJob(Job job, int limit) { lock (sync) { if (Count("queued") >= limit) throw new AppException("queue_full", "任务队列已满。", 429); WriteJob(job); } }
    public Job? Change(string id, Func<Job, Job?> update)
    {
        lock (sync) { var current = Job(id); if (current is null) return null; var changed = update(current); if (changed is null) return null; changed = changed with { Updated = DateTimeOffset.UtcNow }; WriteJob(changed); return changed; }
    }
    public Job? Claim()
    {
        lock (sync)
        {
            using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT id FROM jobs WHERE state='queued' ORDER BY created,id LIMIT 1";
            return command.ExecuteScalar() is string id ? Change(id, job => job with { State = "running", Stage = "loading", Attempts = job.Attempts + 1 }) : null;
        }
    }
    public void Recover()
    {
        lock (sync) { foreach (var job in Jobs(0, 100, "running")) Change(job.Id, current => current with { State = current.Attempts < 2 ? "queued" : "failed", Stage = "interrupted", Error = "上次执行被进程退出中断；最多自动执行两次。" }); }
    }
}
