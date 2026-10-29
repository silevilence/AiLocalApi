using System.Diagnostics;
using AiLocalApi.Contracts;
using AiLocalApi.Core;

namespace AiLocalApi.Verify;

internal static class EngineRegression
{
    public static async Task LanguagesAsync(string data, string models, string fixtures, string modelId)
    {
        await using var service = new ApplicationService(new(Path.GetFullPath(data)));
        service.Models.SetDirectory(Path.GetFullPath(models));
        service.Store.SaveSettings(service.Store.Settings with { CTranslateComputeType = "int8", KeepLoaded = true });
        await service.Models.ImportAsync(Path.Combine(models, modelId));
        await MultilingualRegression.RunAsync(service, fixtures, modelId);
        if (!service.Models.Require(modelId).SupportsTranslation)
        {
            try { await service.SubmitFileAsync(Path.Combine(fixtures, "chinese.wav"), modelId, new(Translate: true)); throw new Exception("Unsupported translation was accepted"); }
            catch (AppException error) when (error.Code == "model_translation") { Console.WriteLine("PASS: turbo translation rejected before execution."); }
        }
    }
    public static async Task FailurePathsAsync(string data, string source, string audio, string missingWorker)
    {
        await using var service = new ApplicationService(new(Path.GetFullPath(data)));
        service.Models.SetDirectory(Path.Combine(service.Paths.Data, "模型 中文"));
        service.Store.SaveSettings(service.Store.Settings with { CTranslateComputeType = "int8" });
        await service.Models.ImportAsync(source);
        var config = Path.Combine(service.Models.Require("ct2-tiny").Path, "config.json");
        var original = await File.ReadAllBytesAsync(config);
        var corrupt = original.ToArray(); corrupt[0] ^= 1; await File.WriteAllBytesAsync(config, corrupt);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var job = await service.SubmitFileAsync(audio, "ct2-tiny", new());
        var rejected = await service.WaitAsync(job.Id, timeout.Token);
        if (rejected.State != "failed" || rejected.Error?.Contains("SHA-256") != true) throw new Exception("Corrupt model did not fail verification");
        await File.WriteAllBytesAsync(config, original);
        var validWorker = Environment.GetEnvironmentVariable("AILOCAL_WORKER");
        try
        {
            Environment.SetEnvironmentVariable("AILOCAL_WORKER", Path.GetFullPath(missingWorker));
            job = await service.SubmitFileAsync(audio, "ct2-tiny", new());
            rejected = await service.WaitAsync(job.Id, timeout.Token);
            if (rejected.State != "failed" || rejected.Error?.Contains("ailocal_ct2") != true) throw new Exception("Missing native library was not diagnosed: " + rejected.Error);
        }
        finally { Environment.SetEnvironmentVariable("AILOCAL_WORKER", validWorker); }
        job = await service.SubmitFileAsync(audio, "ct2-tiny", new(Words: true));
        var recovered = await service.WaitAsync(job.Id, timeout.Token);
        if (recovered.State != "succeeded" || recovered.Result!.Words.Length == 0) throw new Exception("Host did not recover: " + recovered.Error);
        Console.WriteLine("PASS: corrupted model and missing native dependency are diagnosed; same host recovers and transcribes from Unicode model path.");
    }
    public static async Task BenchmarkAsync(string data, string models, string audio)
    {
        await using var service = new ApplicationService(new(Path.GetFullPath(data)));
        service.Models.SetDirectory(Path.GetFullPath(models));
        service.Store.SaveSettings(service.Store.Settings with { Backend = "cpu", CTranslateBackend = "cpu", CTranslateComputeType = "int8", KeepLoaded = true });
        foreach (var (modelId, precision) in new[] { ("tiny", "default"), ("ct2-tiny", "int8"), ("ct2-tiny", "float32") })
        {
            service.Store.SaveSettings(service.Store.Settings with { CTranslateComputeType = precision });
            if (!service.Models.Installed.Any(model => model.Id == modelId)) await service.Models.ImportAsync(Path.Combine(models, modelId == "tiny" ? "ggml-tiny.bin" : modelId));
            for (var pass = 0; pass < 2; pass++)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5)); var watch = Stopwatch.StartNew();
                var job = await service.SubmitFileAsync(audio, modelId, new(Language: "en"));
                var result = await service.WaitAsync(job.Id, timeout.Token);
                if (result.State != "succeeded") throw new Exception(result.Error);
                using var worker = Process.GetProcessById(service.Worker.ProcessId!.Value);
                Console.WriteLine(Wire.Encode(new { modelId, pass = pass == 0 ? "cold" : "warm", elapsedSeconds = watch.Elapsed.TotalSeconds, peakWorkerBytes = worker.PeakWorkingSet64, threads = Math.Clamp(Environment.ProcessorCount - 1, 1, 8), decoder = modelId == "tiny" ? "whisper.cpp default greedy" : "CTranslate2 beam=5", service.Worker.ComputeType, result.Result }));
            }
        }
    }
    public static async Task SuiteAsync(string data, string models, string directory)
    {
        await using var service = new ApplicationService(new(Path.GetFullPath(data)));
        service.Models.SetDirectory(Path.GetFullPath(models));
        service.Store.SaveSettings(service.Store.Settings with { DownloadHost = "hf-mirror.com", CTranslateComputeType = "int8", KeepLoaded = true });
        await service.Models.ImportAsync(Path.Combine(models, "ct2-tiny"));
        await service.Models.ImportAsync(Path.Combine(models, "ggml-tiny.bin"));
        service.Store.SaveSettings(service.Store.Settings with { DefaultModel = "ct2-tiny" });
        await MultilingualRegression.RunAsync(service, directory, "ct2-tiny");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        async Task<Job> Run(string model, string audio, TranscriptionOptions options)
        {
            var watch = Stopwatch.StartNew();
            var job = await service.SubmitFileAsync(Path.Combine(directory, audio), model, options);
            var result = await service.WaitAsync(job.Id, timeout.Token);
            if (result.State != "succeeded") throw new Exception(result.Error);
            Console.WriteLine(Wire.Encode(new { model, audio, options, result.Result, elapsedSeconds = watch.Elapsed.TotalSeconds, memory = service.Worker.ProcessId is { } pid ? Process.GetProcessById(pid).WorkingSet64 : 0 }));
            return result;
        }
        var translation = await Run("ct2-tiny", "chinese.wav", new(Translate: true));
        if (!translation.Result!.Text.Any(char.IsAsciiLetter)) throw new Exception("Translation is not English");
        var silence = Path.Combine(service.Paths.Data, "silence.wav");
        using (var wave = new BinaryWriter(File.Create(silence)))
        {
            wave.Write("RIFF"u8); wave.Write(32036); wave.Write("WAVEfmt "u8); wave.Write(16); wave.Write((short)1); wave.Write((short)1); wave.Write(16000); wave.Write(32000); wave.Write((short)2); wave.Write((short)16); wave.Write("data"u8); wave.Write(32000); wave.Write(new byte[32000]);
        }
        var silent = await Run("ct2-tiny", silence, new(Words: true));
        if (silent.Result!.Text.Length != 0 || silent.Result.Words.Length != 0 || silent.Result.Segments.Length != 0) throw new Exception("Silence produced fabricated content");
        var longAudio = await Run("ct2-tiny", "long.wav", new(Language: "en", Words: true));
        MultilingualRegression.ValidateAlignment(longAudio.Result!);
        if (!longAudio.Result!.Words.Any(w => w.Start >= 30)) throw new Exception("Missing global timestamps");
        if (System.Text.RegularExpressions.Regex.Matches(longAudio.Result.Text, "fellow Americans", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count != 4) throw new Exception("A repeated speech was lost or duplicated at the window boundary");
        foreach (var format in TranscriptFormats.Supported) if (string.IsNullOrWhiteSpace(TranscriptFormats.Format(longAudio.Result, format).Content)) throw new Exception("Empty output");
        await Run("tiny", "jfk.wav", new(Language: "en"));
        await Run("whisper-1", "jfk.wav", new(Language: "en", Temperature: 0.2f, Prompt: "American speech"));
        if (service.Worker.Engine != InferenceEngines.CTranslate2) throw new Exception("Alias routed to wrong engine");
        var cancel = await service.SubmitFileAsync(Path.Combine(directory, "long.wav"), "ct2-tiny", new());
        while (service.Get(cancel.Id).Stage != "transcribing") { if (service.Get(cancel.Id).State is "failed" or "succeeded") throw new Exception("Cannot reach cancellation stage"); await Task.Delay(10, timeout.Token); }
        service.Cancel(cancel.Id);
        while (service.Worker.ProcessId is not null) await Task.Delay(50, timeout.Token);
        if (service.Get(cancel.Id).State != "cancelled") throw new Exception("Cancellation changed final state");
        service.Retry(cancel.Id);
        if ((await service.WaitAsync(cancel.Id, timeout.Token)).State != "succeeded") throw new Exception("Retry failed");
        var crash = await service.SubmitFileAsync(Path.Combine(directory, "long.wav"), "ct2-tiny", new());
        while (service.Get(crash.Id).Stage != "transcribing") { if (service.Get(crash.Id).State is "failed" or "succeeded") throw new Exception("Cannot reach crash stage"); await Task.Delay(10, timeout.Token); }
        Process.GetProcessById(service.Worker.ProcessId!.Value).Kill();
        var recovered = await service.WaitAsync(crash.Id, timeout.Token);
        if (recovered.State != "succeeded" || recovered.Attempts != 2) throw new Exception("Native Worker crash recovery failed");
        service.Store.SaveSettings(service.Store.Settings with { CTranslateComputeType = "float32" });
        await Run("ct2-tiny", "jfk.wav", new(Language: "en"));
        if (service.Worker.ComputeType != "float32") throw new Exception("Effective precision was not reported");
        service.Store.SaveSettings(service.Store.Settings with { CTranslateBackend = "auto" });
        await Run("ct2-tiny", "jfk.wav", new(Language: "en"));
        if (service.Worker.Backend == "cpu" && service.LastError?.Contains("回退 CPU") != true) throw new Exception("Fallback was not visible");
        service.Store.SaveSettings(service.Store.Settings with { CTranslateBackend = "cuda" });
        var cuda = await service.SubmitFileAsync(Path.Combine(directory, "jfk.wav"), "ct2-tiny", new());
        var cudaResult = await service.WaitAsync(cuda.Id, timeout.Token);
        Console.WriteLine("CUDA explicit result: " + cudaResult.State + " / " + cudaResult.Error);
        if (cudaResult.State == "succeeded" && service.Worker.Backend != "cuda") throw new Exception("Explicit CUDA silently fell back");
        Console.WriteLine("PASS: multilingual words, translation, long audio, formats, engine switching, alias, cancellation/retry and backend diagnostics.");
    }
    public static async Task RunAsync(string data, string models, string audio, string modelId)
    {
        await using var service = new ApplicationService(new(Path.GetFullPath(data)));
        service.Models.SetDirectory(Path.GetFullPath(models));
        service.Store.SaveSettings(service.Store.Settings with { DownloadHost = "hf-mirror.com", CTranslateBackend = "cpu", CTranslateComputeType = "int8", IdleSeconds = 2, DefaultModel = modelId });
        var entry = ModelManager.Catalog.Single(m => m.Id == modelId);
        var local = Path.Combine(models, entry.IsBundle ? modelId : $"ggml-{modelId}.bin");
        if (Directory.Exists(local) || File.Exists(local)) await service.Models.ImportAsync(local); else await service.Models.DownloadAsync(modelId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var watch = Stopwatch.StartNew();
        var job = await service.SubmitFileAsync(audio, modelId, new(Words: true));
        var result = await service.WaitAsync(job.Id, timeout.Token);
        Console.WriteLine(Wire.Encode(new { result, service.Worker.Engine, service.Worker.Backend, service.Worker.ComputeType, elapsedSeconds = watch.Elapsed.TotalSeconds }));
        if (result.State != "succeeded" || string.IsNullOrWhiteSpace(result.Result?.Text) || result.Result.Words.Length == 0) throw new Exception(result.Error ?? "No transcript or words");
        var count = service.LoadCount;
        var next = await service.SubmitFileAsync(audio, modelId, new(Language: "en", Prompt: "Speech", Temperature: 0));
        if ((await service.WaitAsync(next.Id, timeout.Token)).State != "succeeded" || service.LoadCount != count) throw new Exception("Model reuse failed");
        while (service.Worker.ProcessId is not null) await Task.Delay(100, timeout.Token);
        Console.WriteLine("PASS: CPU transcription, detection, prompt, native alignment, reuse and idle release.");
    }
}
