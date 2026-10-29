using System.Diagnostics;
using AiLocalApi.Contracts;
using AiLocalApi.Core;

namespace AiLocalApi.Verify;

internal static class CatalogRegression
{
    public static async Task RunAsync(string data, string models, string audio, bool bundles = false)
    {
        await using var service = new ApplicationService(new(Path.GetFullPath(data)));
        service.Models.SetDirectory(Path.GetFullPath(models));
        service.Store.SaveSettings(service.Store.Settings with { DownloadHost = "hf-mirror.com", Backend = "cpu", CTranslateComputeType = "int8", KeepLoaded = true });
        var errors = new List<string>();
        var modelsToCheck = ModelManager.Catalog.Where(m => m.IsBundle == bundles).OrderBy(m => m.Size).ToArray();
        using var slots = new SemaphoreSlim(2);
        var downloads = modelsToCheck.ToDictionary(m => m.Id, Download);
        async Task Download(CatalogModel model)
        {
            if (File.Exists(Path.Combine(data, model.Id + ".verified.json"))) return;
            var local = Path.Combine(models, model.IsBundle ? model.Id : $"ggml-{model.Id}.bin");
            if (File.Exists(local) || Directory.Exists(local)) return;
            await slots.WaitAsync();
            try
            {
                for (var attempt = 0; ; attempt++)
                {
                    try { await service.Models.DownloadAsync(model.Id); return; }
                    catch (Exception error) when (attempt < 2 && error is HttpRequestException or IOException or AppException) { Console.WriteLine($"Retry {model.Id}: {error.Message}"); }
                }
            }
            finally { slots.Release(); }
        }
        foreach (var model in modelsToCheck)
        {
            var checkpoint = Path.Combine(data, model.Id + ".verified.json");
            if (File.Exists(checkpoint)) { Console.WriteLine("PREVIOUS PASS " + model.Id); continue; }
            try
            {
            var watch = Stopwatch.StartNew();
            var local = Path.Combine(models, model.IsBundle ? model.Id : $"ggml-{model.Id}.bin");
            Console.WriteLine($"START {model.Id} bytes={model.Size}");
            await downloads[model.Id];
            await service.Models.ImportAsync(local);
            var job = await service.SubmitFileAsync(audio, model.Id, new(Language: "en", Words: true));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            var result = await service.WaitAsync(job.Id, timeout.Token);
            if (result.State != "succeeded" || string.IsNullOrWhiteSpace(result.Result?.Text) || result.Result.Words.Length == 0) throw new Exception(Wire.Encode(result));
            var evidence = Wire.Encode(new { model.Id, model.Sha256, model.Size, elapsedSeconds = watch.Elapsed.TotalSeconds, result.Result, backend = service.Worker.Backend });
            Console.WriteLine(evidence); await File.WriteAllTextAsync(checkpoint, evidence);
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                try { await service.UnloadAsync(); break; }
                catch (AppException error) when (error.Code == "model_busy") { await Task.Delay(50, timeout.Token); }
            }
            }
            catch (Exception error) { errors.Add(model.Id); Console.WriteLine($"FAIL {model.Id}: {error}"); }
        }
        if (errors.Count > 0) throw new Exception("Catalog verification failed: " + string.Join(", ", errors));
        Console.WriteLine("PASS: all catalog artifacts verified, loaded and transcribed with word timestamps.");
    }
}
