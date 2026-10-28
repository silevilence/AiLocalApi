using System.Security.Cryptography;
using System.Text;
using AiLocalApi.Contracts;
using Microsoft.Data.Sqlite;

namespace AiLocalApi.Core;

public sealed record ApiKeyInfo(string Id, string Name, string Prefix, bool Enabled, DateTimeOffset Created);
public sealed record CreatedApiKey(ApiKeyInfo Info, string Secret);

public sealed partial class StateStore
{
    private sealed record StoredKey(ApiKeyInfo Info, string Hash);
    private static string HashKey(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    private static StoredKey MakeKey(string name, string secret) => new(new(Guid.NewGuid().ToString("N"), name,
        secret.Length >= 16 ? secret[..8] + "…" : "旧密钥", true, DateTimeOffset.UtcNow), HashKey(secret));

    private static void MigrateKeys(SqliteConnection db, SqliteTransaction transaction)
    {
        Execute(db, "CREATE TABLE api_keys(id TEXT PRIMARY KEY,json TEXT NOT NULL); CREATE TABLE auth_policy(id INTEGER PRIMARY KEY CHECK(id=1),allow_anonymous INTEGER NOT NULL);", transaction);
        using var read = db.CreateCommand(); read.Transaction = transaction; read.CommandText = "SELECT json FROM settings WHERE id=1";
        var settings = read.ExecuteScalar() is string json ? Wire.Decode<Settings>(json) : new();
        var hasKey = !string.IsNullOrEmpty(settings.ApiKey);
        Execute(db, $"INSERT INTO auth_policy VALUES(1,{(hasKey ? 0 : 1)})", transaction);
        if (!hasKey) return;
        var key = MakeKey("原有 API 密钥", settings.ApiKey!);
        using var write = db.CreateCommand(); write.Transaction = transaction;
        write.CommandText = "INSERT INTO api_keys VALUES($id,$key); UPDATE settings SET json=$settings WHERE id=1";
        write.Parameters.AddWithValue("$id", key.Info.Id); write.Parameters.AddWithValue("$key", Wire.Encode(key));
        write.Parameters.AddWithValue("$settings", Wire.Encode(settings with { ApiKey = null })); write.ExecuteNonQuery();
    }

    public bool AllowAnonymous
    {
        get { lock (sync) { using var db = Open(); return Convert.ToInt32(Scalar(db, "SELECT allow_anonymous FROM auth_policy WHERE id=1")) == 1; } }
    }
    public void SetAllowAnonymous(bool allow)
    {
        lock (sync) { using var db = Open(); Execute(db, $"UPDATE auth_policy SET allow_anonymous={(allow ? 1 : 0)} WHERE id=1"); Interlocked.Increment(ref revision); }
    }
    private StoredKey[] ReadKeys()
    {
        using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT json FROM api_keys ORDER BY rowid DESC";
        using var reader = command.ExecuteReader(); var keys = new List<StoredKey>();
        while (reader.Read()) keys.Add(Wire.Decode<StoredKey>(reader.GetString(0))); return keys.ToArray();
    }
    public ApiKeyInfo[] ApiKeys() { lock (sync) return ReadKeys().Select(key => key.Info).ToArray(); }
    public CreatedApiKey CreateApiKey(string name, string? secret = null)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 80) throw new AppException("key_name", "请填写 1–80 字的密钥名称。");
        secret ??= "ala_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        if (secret.Length is < 16 or > 256 || secret.Any(c => c <= 32 || c > 126)) throw new AppException("key_value", "自定义密钥需为 16–256 个无空白 ASCII 字符。");
        lock (sync)
        {
            var key = MakeKey(name, secret);
            if (ReadKeys().Any(existing => existing.Hash == key.Hash)) throw new AppException("duplicate_key", "此密钥已存在。");
            Write("api_keys", key.Info.Id, key); return new(key.Info, secret);
        }
    }
    public void SetApiKeyEnabled(string id, bool enabled)
    {
        lock (sync)
        {
            var key = Read<StoredKey>("api_keys", id) ?? throw new AppException("key_missing", "密钥已不存在。");
            Write("api_keys", id, key with { Info = key.Info with { Enabled = enabled } });
        }
    }
    public void DeleteApiKey(string id)
    {
        lock (sync)
        {
            using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "DELETE FROM api_keys WHERE id=$id";
            command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery(); Interlocked.Increment(ref revision);
        }
    }
    public bool Authorize(string authorization)
    {
        lock (sync)
        {
            if (authorization.Length == 0) return AllowAnonymous;
            if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || authorization.Length > 263) return false;
            var hash = Convert.FromHexString(HashKey(authorization[7..]));
            return ReadKeys().Any(key => key.Info.Enabled && CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(key.Hash)));
        }
    }
}
