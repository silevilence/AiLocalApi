using AiLocalApi.Contracts;
using Microsoft.Data.Sqlite;

namespace AiLocalApi.Core;

public sealed partial class StateStore
{
    // Run before dispatch starts. Only paths inside the previous data tree move.
    public void RelocatePaths(string previous, string current)
    {
        string? Rebase(string? path)
        {
            if (path is null) return null;
            var relative = Path.GetRelativePath(previous, path);
            return relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative) ? path : Path.GetFullPath(relative, current);
        }
        lock (sync)
        {
            using var db = Open();
            using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.Database + $".move-{Guid.NewGuid():N}.bak" }.ToString())) { backup.Open(); db.BackupDatabase(backup); }
            using var transaction = db.BeginTransaction();
            foreach (var table in new[] { "settings", "models", "jobs" })
            {
                var rows = new List<(string Id, string Json)>();
                using (var read = db.CreateCommand())
                {
                    read.Transaction = transaction; read.CommandText = $"SELECT id,json FROM {table}";
                    using var reader = read.ExecuteReader();
                    while (reader.Read()) rows.Add((reader.GetValue(0).ToString()!, reader.GetString(1)));
                }
                foreach (var row in rows)
                {
                    var json = table switch
                    {
                        "settings" => Wire.Encode(Wire.Decode<Settings>(row.Json) with { ModelDirectory = Rebase(Wire.Decode<Settings>(row.Json).ModelDirectory) }),
                        "models" => Wire.Encode(Wire.Decode<ModelInfo>(row.Json) with { Path = Rebase(Wire.Decode<ModelInfo>(row.Json).Path)! }),
                        _ => Wire.Encode(Wire.Decode<Job>(row.Json) with { Input = Rebase(Wire.Decode<Job>(row.Json).Input)! })
                    };
                    using var write = db.CreateCommand(); write.Transaction = transaction; write.CommandText = $"UPDATE {table} SET json=$json WHERE id=$id";
                    write.Parameters.AddWithValue("$json", json); write.Parameters.AddWithValue("$id", row.Id); write.ExecuteNonQuery();
                }
            }
            transaction.Commit(); Interlocked.Increment(ref revision);
        }
    }
}
