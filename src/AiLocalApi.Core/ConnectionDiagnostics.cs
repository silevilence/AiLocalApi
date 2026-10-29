using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AiLocalApi.Contracts;

namespace AiLocalApi.Core;

public static class ConnectionDiagnostics
{
    public static async Task<string> TestBackendAsync(ApplicationService service, string backend, CancellationToken token, string engine = InferenceEngines.WhisperCpp, string computeType = "default")
    {
        if (!InferenceEngines.SupportsBackend(engine, backend) || !InferenceEngines.SupportsComputeType(engine, backend, computeType)) throw new AppException("invalid_backend", "请选择引擎支持的后端与精度。");
        var model = service.Models.Installed.Where(m => m.Engine == engine).OrderBy(model => model.Size).FirstOrDefault()
            ?? throw new AppException("model_required", "请先在模型页下载或导入一个模型，再测试实际加载能力。");
        using var lease = service.Models.Acquire(model.Id);
        await ModelManager.VerifyAsync(model.Path, ModelManager.Catalog.Single(m => m.Id == model.Id), token);
        await using var worker = new WorkerClient();
        var clock = Stopwatch.StartNew();
        string? fallback = null;
        try { await worker.SendAsync(new(Guid.NewGuid().ToString("N"), "load", ModelPath: model.Path, Backend: backend, Engine: engine, ComputeType: computeType), cancellationToken: token); }
        catch (Exception error) when (backend == "auto" && !token.IsCancellationRequested)
        {
            fallback = error.Message; await worker.DisposeAsync();
            await worker.SendAsync(new(Guid.NewGuid().ToString("N"), "load", ModelPath: model.Path, Backend: "cpu", Engine: engine, ComputeType: computeType), cancellationToken: token);
        }
        return $"可用 · {worker.Engine} · 实际后端 {worker.Backend} · 精度 {worker.ComputeType} · 模型 {model.Id} · {clock.Elapsed.TotalSeconds:F1} 秒\n{worker.BackendDetails}{(fallback is null ? "" : "\n自动回退 CPU：" + fallback)}";
    }

    public static async Task<string> TestSourceAsync(string host, CancellationToken token, HttpClient? client = null)
    {
        if (host is not ("huggingface.co" or "hf-mirror.com")) throw new AppException("invalid_download_host", "请选择支持的模型下载源。");
        using var ownedClient = client is null ? new HttpClient() : null;
        var source = ModelManager.Catalog[0].Source.Replace("https://huggingface.co", "https://" + host);
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.Range = new RangeHeaderValue(0, 3);
        var clock = Stopwatch.StartNew();
        using var response = await (client ?? ownedClient!).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From != 0) throw new IOException("源返回了不正确的文件范围。");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        var header = new byte[4]; await input.ReadExactlyAsync(header, token);
        if (!header.SequenceEqual(new byte[] { 0x6c, 0x6d, 0x67, 0x67 })) throw new IOException("源返回的内容不是预期的模型文件，可能是网络验证页。");
        return $"{host} 可用 · {clock.Elapsed.TotalSeconds:F1} 秒 · 已读取模型文件头。完整下载仍会校验 SHA-256。";
    }
}
