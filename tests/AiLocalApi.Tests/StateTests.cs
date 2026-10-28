using AiLocalApi.Contracts;
using AiLocalApi.Core;
using Microsoft.Data.Sqlite;
using Xunit;
namespace AiLocalApi.Tests;
public class StateTests
{
    [Fact] public void UnavailableLogDoesNotBreakApplicationWork()
    {
        var paths = Paths(); paths.Initialize();
        using var locked = new FileStream(Path.Combine(paths.Logs, $"{DateTime.UtcNow:yyyy-MM-dd}.log"), FileMode.Create, FileAccess.Write, FileShare.None);
        paths.Log("test_diagnostic", "test-job");
        var store = new StateStore(paths); store.SaveSettings(new(Port: 18322));
        Assert.Equal(18322, store.Settings.Port);
    }
    private static AppPaths Paths() => new(Path.Combine(Path.GetTempPath(), "AiLocalApi-tests", Guid.NewGuid().ToString("N"), "中文 path"));
    [Fact] public void SettingsSurviveRestartAndNewerSchemaIsProtected()
    {
        var paths = Paths(); var store = new StateStore(paths); store.SaveSettings(new(Port: 18321));
        Assert.Equal(18321, new StateStore(paths).Settings.Port);
        using (var db = new SqliteConnection($"Data Source={paths.Database}")) { db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version=999"; command.ExecuteNonQuery(); }
        Assert.Equal("database_newer", Assert.Throws<AppException>(() => new StateStore(paths)).Code);
    }
    [Fact] public void ClaimAndRecoveryBoundAttemptsAndPreserveCompletedResult()
    {
        var store = new StateStore(Paths()); var now = DateTimeOffset.UtcNow;
        store.AddJob(new("one", "tiny", "audio", new(), "queued", "queued", now, now), 1);
        Assert.Throws<AppException>(() => store.AddJob(new("two", "tiny", "audio", new(), "queued", "queued", now, now), 1));
        Assert.Equal(1, store.Claim()!.Attempts); Assert.Null(store.Claim()); store.Recover();
        Assert.Equal(2, store.Claim()!.Attempts); store.Recover(); Assert.Equal("failed", store.Job("one")!.State);
    }
    [Fact] public void FailedMigrationKeepsOriginalSchemaAndCreatesBackup()
    {
        var paths = Paths(); paths.Initialize();
        using (var db = new SqliteConnection($"Data Source={paths.Database}")) { db.Open(); using var command = db.CreateCommand(); command.CommandText = "CREATE TABLE jobs(id TEXT PRIMARY KEY); INSERT INTO jobs VALUES('preserved'); PRAGMA user_version=1"; command.ExecuteNonQuery(); }
        Assert.Throws<SqliteException>(() => new StateStore(paths));
        Assert.Single(Directory.GetFiles(paths.Data, "state.db.v1.*.bak"));
        using var check = new SqliteConnection($"Data Source={paths.Database}"); check.Open(); using var query = check.CreateCommand(); query.CommandText = "SELECT id FROM jobs"; Assert.Equal("preserved", query.ExecuteScalar()); query.CommandText = "PRAGMA user_version"; Assert.Equal(1L, query.ExecuteScalar());
    }
}
