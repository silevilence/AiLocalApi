using System.Diagnostics;
using AiLocalApi.Contracts;
using AiLocalApi.Core;
using Microsoft.Data.Sqlite;
namespace AiLocalApi.Verify;
internal static class Regression
{
    public static async Task RunAsync(ApplicationService service, string audio)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        service.Store.SaveSettings(service.Store.Settings with { IdleSeconds = 60 });
        foreach (var extension in new[] { "mp3", "m4a", "flac", "ogg", "webm", "mp4" })
        {
            var path = Path.ChangeExtension(audio, extension); if (!File.Exists(path)) throw new Exception("Missing native audio fixture: " + path);
            var formatJob = await service.SubmitFileAsync(path, "tiny", new()); var formatResult = await service.WaitAsync(formatJob.Id, timeout.Token);
            if (formatResult.State != "succeeded" || formatResult.Result is null || Math.Abs(formatResult.Result.Duration - 11) > 0.3) throw new Exception($"Format {extension} failed: {formatResult.Error}");
            Console.WriteLine($"PASS: {extension}, duration={formatResult.Result.Duration:F3}s");
        }
        var longAudio = Path.Combine(Path.GetDirectoryName(audio)!, "long.wav");
        var longJob = await service.SubmitFileAsync(longAudio, "tiny", new(Words: true)); var longResult = await service.WaitAsync(longJob.Id, timeout.Token);
        if (longResult.State != "succeeded" || !longResult.Result!.Words.Any(word => word.Start >= 30) || Math.Abs(longResult.Result.Duration - 44) > 0.01) throw new Exception("Global chunk timestamps failed");
        Console.WriteLine("PASS: 44-second audio uses global segment/word offsets");
        var first = await service.SubmitFileAsync(audio, "tiny", new());
        var queued = await service.SubmitFileAsync(audio, "tiny", new()); service.Cancel(queued.Id);
        if ((await service.WaitAsync(first.Id, timeout.Token)).State != "succeeded" || service.Get(queued.Id).State != "cancelled") throw new Exception("Queue cancellation failed");
        var cancel = await service.SubmitFileAsync(audio, "tiny", new());
        await Until(() => service.Get(cancel.Id).Stage == "transcribing", timeout.Token);
        try { await service.UnloadAsync(); throw new Exception("Busy unload was accepted"); } catch (AppException error) when (error.Code == "model_busy") { }
        try { service.Models.Delete("tiny"); throw new Exception("Busy deletion was accepted"); } catch (AppException error) when (error.Code == "model_busy") { }
        service.Cancel(cancel.Id);
        await Until(() => service.ModelState == "unloaded", timeout.Token);
        if (service.Get(cancel.Id).State != "cancelled") throw new Exception("Running cancellation failed");
        service.Retry(cancel.Id);
        if ((await service.WaitAsync(cancel.Id, timeout.Token)).State != "succeeded") throw new Exception("Retry failed");
        var crash = await service.SubmitFileAsync(audio, "tiny", new());
        await Until(() => service.Get(crash.Id).Stage == "transcribing", timeout.Token);
        Process.GetProcessById(service.Worker.ProcessId!.Value).Kill();
        var recovered = await service.WaitAsync(crash.Id, timeout.Token);
        if (recovered.State != "succeeded" || recovered.Attempts != 2) throw new Exception("Worker crash recovery failed: " + recovered.Error);
        var bad = await service.SubmitAsync(new MemoryStream([1, 2, 3]), "broken.wav", "tiny", new());
        if ((await service.WaitAsync(bad.Id, timeout.Token)).State != "failed") throw new Exception("Corrupt input was accepted");
        var next = await service.SubmitFileAsync(audio, "tiny", new());
        if ((await service.WaitAsync(next.Id, timeout.Token)).State != "succeeded") throw new Exception("Post-failure recovery failed");
        Console.WriteLine("PASS: queued/running cancellation, busy unload/delete, retry, Worker crash recovery, corrupt input isolation");
    }
    private static async Task Until(Func<bool> predicate, CancellationToken token) { while (!predicate()) await Task.Delay(10, token); }
    public static async Task ModelsAsync(ApplicationService service, string audio)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var download = service.Models.DownloadAsync("tiny.en");
        if (!ReferenceEquals(download, service.Models.DownloadAsync("tiny.en"))) throw new Exception("Duplicate downloads not merged");
        await Until(() => download.IsCompleted || service.Models.Progress.TryGetValue("tiny.en", out var status) && status.Received > 65536, timeout.Token);
        service.Models.CancelDownload("tiny.en");
        try { await download; } catch (OperationCanceledException) { }
        if (service.Models.Installed.Any(model => model.Id == "tiny.en")) throw new Exception("Cancelled download appeared installed");
        await service.Models.DownloadAsync("tiny.en");
        await service.Models.ImportAsync(Path.Combine(service.Models.DirectoryPath, "ggml-tiny.en.bin"));
        var corrupt = Path.Combine(service.Paths.Data, "invalid-model.bin"); await File.WriteAllBytesAsync(corrupt, [1, 2, 3]);
        try { await service.Models.ImportAsync(corrupt); throw new Exception("Corrupt model accepted"); } catch (AppException error) when (error.Code == "untrusted_model") { }
        var job = await service.SubmitFileAsync(audio, "tiny.en", new(Language: "en"));
        if ((await service.WaitAsync(job.Id, timeout.Token)).State != "succeeded") throw new Exception("Model switch failed");
        try { await service.SubmitFileAsync(audio, "tiny.en", new(Translate: true)); throw new Exception("English model translation accepted"); } catch (AppException error) when (error.Code == "model_language") { }
        await Task.Delay(200); await service.UnloadAsync(); service.Models.Delete("tiny.en");
        if (service.Models.Installed.Any(model => model.Id == "tiny.en")) throw new Exception("Model removal failed");
        Console.WriteLine("PASS: download cancellation/resume/deduplication, checksum import, model switch, capability validation, safe deletion");
    }
    public static void Seed(string directory)
    {
        var paths = new AppPaths(Path.GetFullPath(directory)); var store = new StateStore(paths);
        using var db = new SqliteConnection($"Data Source={paths.Database}"); db.Open(); using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO jobs(id,state,created,json) VALUES($id,'succeeded',$created,$json)";
        command.Parameters.Add("$id", SqliteType.Text); command.Parameters.Add("$created", SqliteType.Text); command.Parameters.Add("$json", SqliteType.Text);
        for (var i = 0; i < 10000; i++)
        {
            var now = DateTimeOffset.UtcNow.AddSeconds(-i); var id = $"history-{i:00000}";
            command.Parameters["$id"].Value = id; command.Parameters["$created"].Value = now.ToString("O");
            command.Parameters["$json"].Value = Wire.Encode(new Job(id, "tiny", "", new(), "succeeded", "completed", now, now, Result: new("历史任务性能样本", [], [], 1, "zh"))); command.ExecuteNonQuery();
        }
        transaction.Commit(); var watch = Stopwatch.StartNew(); var page = store.Jobs(100); watch.Stop();
        if (page.Length != 50) throw new Exception("Pagination failed");
        Console.WriteLine($"PASS: 10000 history records, page 101 returns 50 in {watch.Elapsed.TotalMilliseconds:F2} ms");
    }
}
