using System.Threading.Channels;
using AiLocalApi.Contracts;

namespace AiLocalApi.Core;

public sealed class ApplicationService : IAsyncDisposable
{
    public AppPaths Paths { get; }
    public StateStore Store { get; }
    public ModelManager Models { get; }
    public WorkerClient Worker { get; } = new();
    public bool Paused { get; set; }
    public string ModelState { get; private set; } = "unloaded";
    public string? LoadedModel { get; private set; }
    public int LoadCount { get; private set; }
    public string? LastError { get; private set; }
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim execution = new(1);
    private readonly SemaphoreSlim ingestion = new(1);
    private CancellationTokenSource? activeCancellation;
    private string? activeJob;
    private IDisposable? modelLease;
    private string? backend;
    private string effectiveBackend = "cpu";
    private DateTimeOffset lastUse = DateTimeOffset.UtcNow;
    private DateTimeOffset nextCleanup = DateTimeOffset.MinValue;
    private readonly Task dispatcher;
    public ApplicationService(AppPaths paths)
    {
        Paths = paths; Store = new(paths); Models = new(Store); Store.Recover();
        foreach (var directory in Directory.EnumerateDirectories(Paths.Temporary))
        {
            var id = Path.GetFileName(directory);
            if (Guid.TryParseExact(id, "N", out _) && Store.Job(id) is null) Directory.Delete(directory, true);
        }
        dispatcher = Task.Run(DispatchAsync);
    }
    public async Task<Job> SubmitAsync(Stream source, string name, string modelId, TranscriptionOptions options, CancellationToken token = default)
    {
        if (Paused) throw new AppException("paused", "服务已暂停接单。", 503);
        if (!float.IsFinite(options.Temperature) || options.Temperature is < 0 or > 1) throw new AppException("invalid_temperature", "temperature 必须介于 0 和 1。");
        if (options.Prompt?.Length > 4096) throw new AppException("prompt_too_long", "prompt 最长 4096 字符。");
        if (options.Language is { } language && !Languages.Contains(language)) throw new AppException("invalid_language", "language 必须是支持的 ISO 语言代码。");
        if (!SupportedExtensions.Contains(Path.GetExtension(name).ToLowerInvariant())) throw new AppException("unsupported_audio", "支持 WAV、MP3、M4A、FLAC、OGG、WebM 和 MP4。", 415);
        await ingestion.WaitAsync(token);
        var id = Guid.NewGuid().ToString("N"); var directory = Path.Combine(Paths.Temporary, id); var accepted = false;
        try
        {
            ModelInfo model;
            IDisposable reservation;
            lock (Models.Sync) { model = Models.Require(modelId); reservation = Models.Acquire(model.Id); }
            using (reservation)
            {
                if (!model.Multilingual && (options.Translate || options.Language is not (null or "en"))) throw new AppException("model_language", "英语专用模型不支持该语言或翻译。");
                var settings = Store.Settings;
                if (Store.Count("queued") >= settings.MaxQueued) throw new AppException("queue_full", "任务队列已满。", 429);
                var existing = Directory.EnumerateFiles(Paths.Temporary, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
                Directory.CreateDirectory(directory);
                var input = Path.Combine(directory, "input" + Path.GetExtension(name).ToLowerInvariant());
                await using (var file = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920]; long length = 0; int read;
                    while ((read = await source.ReadAsync(buffer, token)) > 0)
                    {
                        length += read;
                        if (length > settings.MaxUploadBytes) throw new AppException("upload_too_large", "音频超出上传大小限制。", 413);
                        // Reserve disk for one decoded mono float stream, bounded by maximum duration.
                        if (existing + length + settings.MaxDurationSeconds * 64000L > settings.MaxTemporaryBytes) throw new AppException("temporary_limit", "任务临时空间达到限制。", 507);
                        await file.WriteAsync(buffer.AsMemory(0, read), token);
                    }
                    if (length == 0) throw new AppException("empty_audio", "音频为空。");
                }
                token.ThrowIfCancellationRequested();
                var now = DateTimeOffset.UtcNow; var job = new Job(id, model.Id, input, options, "queued", "queued", now, now);
                Store.AddJob(job, settings.MaxQueued); accepted = true; wake.Writer.TryWrite(true); return job;
            }
        }
        finally { if (!accepted && Directory.Exists(directory)) Directory.Delete(directory, true); ingestion.Release(); }
    }
    public async Task<Job> SubmitFileAsync(string path, string model, TranscriptionOptions options, CancellationToken token = default)
    { await using var input = File.OpenRead(path); return await SubmitAsync(input, Path.GetFileName(path), model, options, token); }
    public Job Get(string id) => Store.Job(id) ?? throw new AppException("job_not_found", "任务不存在。", 404);
    public Job Cancel(string id)
    {
        var job = Get(id);
        if (job.State is not ("queued" or "running")) return job;
        job = Store.Change(id, current => current.State is "queued" or "running" ? current with { State = "cancelled", Stage = "cancelled" } : null) ?? Get(id);
        lock (Models.Sync) { if (activeJob == id) activeCancellation?.Cancel(); }
        return job;
    }
    public Job Retry(string id)
    {
        lock (Models.Sync)
        {
            if (Paused) throw new AppException("paused", "服务已暂停接单。", 503);
            var job = Get(id); _ = Models.Require(job.Model);
            if (job.State is not ("failed" or "cancelled")) throw new AppException("retry_conflict", "只能重试失败或取消的任务。", 409);
            if (activeJob == id) throw new AppException("busy", "正在回收已取消任务，请稍后重试。", 409);
            if (!File.Exists(job.Input)) throw new AppException("input_expired", "输入已过保留期，请重新上传。", 410);
            if (Store.Count("queued") >= Store.Settings.MaxQueued) throw new AppException("queue_full", "任务队列已满。", 429);
            var updated = Store.Change(id, current => current with { State = "queued", Stage = "queued", Attempts = 0, Error = null, Result = null })!;
            wake.Writer.TryWrite(true); return updated;
        }
    }
    public async Task<Job> WaitAsync(string id, CancellationToken token)
    {
        while (true) { token.ThrowIfCancellationRequested(); var job = Get(id); if (job.State is not ("queued" or "running")) return job; await Task.Delay(150, token); }
    }
    private async Task DispatchAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await execution.WaitAsync(stop.Token);
                try
                {
                    var job = Store.Claim();
                    if (job is not null) await ExecuteAsync(job);
                    else if (LoadedModel is not null && !Store.Settings.KeepLoaded && DateTimeOffset.UtcNow - lastUse >= TimeSpan.FromSeconds(Store.Settings.IdleSeconds)) await UnloadCoreAsync();
                }
                finally { execution.Release(); }
                if (Store.Count("queued") > 0) continue;
                if (DateTimeOffset.UtcNow >= nextCleanup) { Cleanup(); nextCleanup = DateTimeOffset.UtcNow.AddMinutes(1); }
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); tick.CancelAfter(1000);
                try { await wake.Reader.ReadAsync(tick.Token); } catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception error) { LastError = error.Message; Paths.Log("dispatcher_error"); await Task.Delay(500); }
        }
    }
    private async Task ExecuteAsync(Job job)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(Store.Settings.MaxExecutionSeconds));
        lock (Models.Sync) { activeJob = job.Id; activeCancellation = cts; if (Get(job.Id).State == "cancelled") cts.Cancel(); }
        try
        {
            cts.Token.ThrowIfCancellationRequested();
            var settings = Store.Settings;
            if (LoadedModel != job.Model || backend != settings.Backend || Worker.ProcessId is null)
            {
                await UnloadCoreAsync();
                ModelState = "loading"; modelLease = Models.Acquire(job.Model);
                Store.Change(job.Id, current => current.State == "running" ? current with { Stage = "loading" } : null);
                var model = Models.Require(job.Model);
                await ModelManager.VerifyAsync(model.Path, ModelManager.Catalog.Single(item => item.Id == model.Id), cts.Token);
                effectiveBackend = settings.Backend;
                WorkerReply loaded;
                try { loaded = await Worker.SendAsync(new(job.Id, "load", model.Path, Backend: effectiveBackend), cancellationToken: cts.Token); }
                catch (Exception error) when (settings.Backend == "auto" && !cts.IsCancellationRequested && error is AppException or IOException)
                {
                    await Worker.DisposeAsync(); effectiveBackend = "cpu";
                    LastError = "自动 GPU 初始化失败，已回退 CPU：" + error.Message;
                    loaded = await Worker.SendAsync(new(job.Id, "load", model.Path, Backend: effectiveBackend), cancellationToken: cts.Token);
                }
                LoadedModel = job.Model; backend = settings.Backend; ModelState = "loaded"; LoadCount += loaded.LoadCount;
                if (settings.Backend == "auto" && loaded.Backend == "cpu" && effectiveBackend == "auto") LastError = "自动模式未找到可用 GPU 运行库，当前使用 CPU。";
            }
            var result = await Worker.SendAsync(new(job.Id, "transcribe", Models.Require(job.Model).Path, job.Input, job.Options, effectiveBackend, settings.MaxDurationSeconds),
                progress => Store.Change(job.Id, current => current.State == "running" ? current with { Stage = progress.Stage ?? "running" } : null), cts.Token);
            Store.Change(job.Id, current => current.State == "running" ? current with { State = "succeeded", Stage = "completed", Result = result.Result, Error = null } : null);
            Paths.Log("job_succeeded", job.Id);
        }
        catch (Exception error)
        {
            Store.Change(job.Id, current => current.State != "running" ? null : current with { State = stop.IsCancellationRequested || error is IOException && current.Attempts < 2 && !cts.IsCancellationRequested ? "queued" : "failed", Stage = "interrupted", Error = cts.IsCancellationRequested && !stop.IsCancellationRequested ? "执行时间超过配置上限。" : error.Message });
            LastError = error.Message; Paths.Log(cts.IsCancellationRequested ? "job_cancelled" : "job_failed", job.Id);
            await UnloadCoreAsync(); ModelState = cts.IsCancellationRequested ? "unloaded" : "failed";
        }
        finally { lastUse = DateTimeOffset.UtcNow; lock (Models.Sync) { activeJob = null; activeCancellation = null; } }
    }
    public async Task UnloadAsync()
    {
        if (!await execution.WaitAsync(0)) throw new AppException("model_busy", "任务正在执行，结束后才能卸载模型。", 409);
        try { await UnloadCoreAsync(); } finally { execution.Release(); }
    }
    private async Task UnloadCoreAsync()
    {
        ModelState = "unloading";
        // Recycling the idle Worker also releases driver allocations and resets native backend selection.
        await Worker.DisposeAsync(); modelLease?.Dispose(); modelLease = null; LoadedModel = null; backend = null; ModelState = "unloaded";
    }
    private void Cleanup()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Store.Settings.RetentionDays);
        foreach (var job in Store.ExpiredBefore(cutoff))
        {
            lock (Models.Sync)
            {
                if (activeJob == job.Id || Store.Job(job.Id)?.State != job.State) continue;
                var directory = Path.GetFullPath(Path.Combine(Paths.Temporary, job.Id));
                if (!Guid.TryParseExact(job.Id, "N", out _) || !directory.StartsWith(Path.GetFullPath(Paths.Temporary) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
                Store.Change(job.Id, current => current with { State = "expired", Stage = "expired", Result = null });
            }
        }
        foreach (var log in Directory.EnumerateFiles(Paths.Logs, "*.log")) if (File.GetLastWriteTimeUtc(log) < cutoff.UtcDateTime) File.Delete(log);
    }
    public async ValueTask DisposeAsync()
    {
        Paused = true; stop.Cancel(); await Models.StopAsync(); await dispatcher; await UnloadAsync(); stop.Dispose();
    }
    public static readonly HashSet<string> SupportedExtensions = [".wav", ".mp3", ".m4a", ".flac", ".ogg", ".webm", ".mp4"];
    private static readonly HashSet<string> Languages = new("en zh de es ru ko fr ja pt tr pl ca nl ar sv it id hi fi vi he uk el ms cs ro da hu ta no th ur hr bg lt la mi ml cy sk te fa lv bn sr az sl kn et mk br eu is hy ne mn bs kk sq sw gl mr pa si km sn yo so af oc ka be tg sd gu am yi lo uz fo ht ps tk nn mt sa lb my bo tl mg as tt haw ln ha ba jw su yue".Split(' '));
}
