using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AiLocalApi.Contracts;

namespace AiLocalApi.Core;

public sealed record ModelFile(string Name, long Size, string Sha256);
public sealed record CatalogModel(string Id, long Size, string Sha256, bool Multilingual, string Quantization = "F16", string Engine = InferenceEngines.WhisperCpp, string? Repository = null, string? Revision = null, ModelFile[]? Files = null)
{
    public string Source => IsBundle ? $"https://huggingface.co/{Repository}/tree/{Revision}" : $"https://huggingface.co/ggerganov/whisper.cpp/resolve/{WhisperCatalog.Revision}/ggml-{Id}.bin?download=true";
    public bool SupportsTranslation => Multilingual && !Id.Contains("turbo", StringComparison.Ordinal);
    public bool IsBundle => Engine == InferenceEngines.CTranslate2;
    public string Tier => (Id.StartsWith("ct2-", StringComparison.Ordinal) ? Id[4..] : Id).Split("-q")[0];
}
public sealed record DownloadStatus(string Id, long Received, long Total, string State, string? Error = null);

public sealed partial class ModelManager(StateStore store)
{
    private readonly Func<string, long> availableSpace = directory => new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace;
    internal ModelManager(StateStore store, Func<string, long> availableSpace) : this(store) => this.availableSpace = availableSpace;
    public static readonly CatalogModel[] Catalog = [.. WhisperCatalog.Models, .. ReadBundleCatalog()];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(60) };
    internal readonly object Sync = new();
    private readonly Dictionary<string, int> leases = [];
    private readonly ConcurrentDictionary<string, Task> downloads = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> cancellation = new();
    public ConcurrentDictionary<string, DownloadStatus> Progress { get; } = new();
    private bool stopping;
    public ModelInfo[] Installed => store.Models().Where(IsPresent).ToArray();
    public string DirectoryPath => store.Settings.ModelDirectory ?? throw new AppException("model_directory_required", "请先选择模型目录，不会自动向系统盘下载模型。");
    public void SetDirectory(string directory)
    {
        lock (Sync)
        {
            if (downloads.Values.Any(task => !task.IsCompleted) || leases.Count > 0 || store.Count("queued") + store.Count("running") > 0) throw new AppException("busy", "下载或任务进行中，暂时无法更改目录。", 409);
            var full = Path.GetFullPath(directory);
            Directory.CreateDirectory(full);
            var probe = Path.Combine(full, $".write-test-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            store.SaveSettings(store.Settings with { ModelDirectory = full });
        }
    }
    public Task DownloadAsync(string id)
    {
        lock (Sync)
        {
            if (stopping) throw new AppException("stopping", "服务正在退出。", 503);
            if (downloads.TryGetValue(id, out var task) && !task.IsCompleted) return task;
            var model = Catalog.SingleOrDefault(item => item.Id == id) ?? throw new AppException("unknown_model", "模型不在支持的目录中。");
            var directory = DirectoryPath;
            if (Installed.Any(item => item.Id == id)) return Task.CompletedTask;
            var cts = new CancellationTokenSource(); cancellation[id] = cts;
            return downloads[id] = DownloadCoreAsync(model, directory, cts);
        }
    }
    public void CancelDownload(string id) { if (cancellation.TryGetValue(id, out var cts)) { try { cts.Cancel(); } catch (ObjectDisposedException) { } } }
    public async Task StopAsync()
    {
        Task[] active;
        lock (Sync) { stopping = true; foreach (var pair in cancellation) { try { pair.Value.Cancel(); } catch (ObjectDisposedException) { } } active = downloads.Values.ToArray(); }
        try { await Task.WhenAll(active); } catch (Exception) { /* Download status retains the reason; shutdown still proceeds. */ }
    }
    private async Task DownloadCoreAsync(CatalogModel model, string directory, CancellationTokenSource cts)
    {
        var temporary = Path.Combine(directory, model.Id + ".partial");
        try
        {
            if (model.IsBundle) { await DownloadBundleAsync(model, directory, cts.Token); return; }
            var existing = File.Exists(temporary) ? new FileInfo(temporary).Length : 0;
            Progress[model.Id] = new(model.Id, existing, model.Size, "connecting");
            if (existing > model.Size) { File.Delete(temporary); existing = 0; }
            if (availableSpace(directory) < model.Size - existing + 16 * 1024 * 1024) throw new AppException("disk_full", "模型目录磁盘空间不足。");
            if (existing < model.Size)
            {
                // Optional public mirror is for restricted networks; catalog hashes remain authoritative.
                var source = model.Source.Replace("https://huggingface.co", Environment.GetEnvironmentVariable("AILOCAL_MODEL_ORIGIN") ?? "https://" + store.Settings.DownloadHost);
                using var request = new HttpRequestMessage(HttpMethod.Get, source);
                if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
                using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token); headerTimeout.CancelAfter(TimeSpan.FromSeconds(60));
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var resume = response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From == existing;
                if (response.StatusCode == HttpStatusCode.PartialContent && !resume) throw new IOException("下载服务器返回了错误的 Range。");
                if (!resume) existing = 0;
                await using var output = new FileStream(temporary, resume ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 81920, true);
                await using var input = await response.Content.ReadAsStreamAsync(cts.Token);
                var buffer = new byte[81920]; int count;
                while (true)
                {
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(cts.Token); stall.CancelAfter(TimeSpan.FromSeconds(30));
                    count = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false); if (count == 0) break;
                    existing += count;
                    if (existing > model.Size) throw new IOException("下载大小超出可信目录中的值。");
                    await output.WriteAsync(buffer.AsMemory(0, count), cts.Token);
                    Progress[model.Id] = new(model.Id, existing, model.Size, "downloading");
                }
            }
            Progress[model.Id] = new(model.Id, existing, model.Size, "verifying");
            try { await VerifyAsync(temporary, model, cts.Token); }
            catch (AppException) { File.Move(temporary, temporary + ".invalid-" + Guid.NewGuid().ToString("N")); throw; }
            lock (Sync)
            {
                if (leases.ContainsKey(model.Id)) throw new AppException("model_busy", "模型正在使用。", 409);
                var destination = Path.Combine(directory, $"ggml-{model.Id}.bin");
                File.Move(temporary, destination, true);
                store.SaveModel(new(model.Id, destination, model.Sha256, model.Size, model.Multilingual, model.Quantization, model.Source));
                if (store.Settings.DefaultModel is null) store.SaveSettings(store.Settings with { DefaultModel = model.Id });
            }
            Progress[model.Id] = new(model.Id, model.Size, model.Size, "available");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { Progress[model.Id] = new(model.Id, Progress.GetValueOrDefault(model.Id)?.Received ?? 0, model.Size, "cancelled"); throw; }
        catch (OperationCanceledException) { Progress[model.Id] = new(model.Id, Progress.GetValueOrDefault(model.Id)?.Received ?? 0, model.Size, "failed", "网络超时，可重试并继续下载。"); throw new AppException("download_timeout", "网络超时，可重试并继续下载。", 504); }
        catch (Exception error) { Progress[model.Id] = new(model.Id, Progress.GetValueOrDefault(model.Id)?.Received ?? 0, model.Size, "failed", error.Message); throw; }
        finally { cancellation.TryRemove(model.Id, out _); cts.Dispose(); }
    }
    public async Task ImportAsync(string path, CancellationToken token = default)
    {
        if (Directory.Exists(path)) { await ImportBundleAsync(path, token); return; }
        await using var file = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token));
        var model = Catalog.SingleOrDefault(item => item.Sha256 == hash && item.Size == file.Length) ?? throw new AppException("untrusted_model", "文件不匹配支持目录的大小与 SHA-256；请导入模型列表中支持的官方模型或量化版本。");
        lock (Sync)
        {
            if (leases.ContainsKey(model.Id) || downloads.TryGetValue(model.Id, out var task) && !task.IsCompleted) throw new AppException("model_busy", "模型正在使用或下载。", 409);
            var destination = Path.Combine(DirectoryPath, $"ggml-{model.Id}.bin");
            if (!string.Equals(Path.GetFullPath(path), destination, StringComparison.OrdinalIgnoreCase)) File.Copy(path, destination, true);
            store.SaveModel(new(model.Id, destination, hash, model.Size, model.Multilingual, model.Quantization, "local import"));
            if (store.Settings.DefaultModel is null) store.SaveSettings(store.Settings with { DefaultModel = model.Id });
        }
    }
    public static async Task VerifyAsync(string path, CatalogModel model, CancellationToken token)
    {
        if (model.IsBundle)
        {
            foreach (var item in model.Files!) await VerifyFileAsync(Path.Combine(path, item.Name), item, token).ConfigureAwait(false);
            return;
        }
        await using var file = File.OpenRead(path);
        if (file.Length != model.Size || Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token)) != model.Sha256)
            throw new AppException("model_checksum", "模型大小或 SHA-256 校验失败；损坏的下载将被隔离，可重新下载。", 422);
    }
    public ModelInfo Require(string id)
    {
        if (id == "whisper-1") id = store.Settings.DefaultModel ?? throw new AppException("model_not_configured", "尚未设置默认模型。", 503);
        return Installed.SingleOrDefault(model => model.Id == id) ?? throw new AppException("model_unavailable", "模型未安装或文件已损坏。", 404);
    }
    public IDisposable Acquire(string id)
    {
        lock (Sync) { _ = Require(id); leases[id] = leases.GetValueOrDefault(id) + 1; return new Lease(() => { lock (Sync) { if (--leases[id] == 0) leases.Remove(id); } }); }
    }
    public void Delete(string id)
    {
        lock (Sync)
        {
            if (leases.ContainsKey(id) || store.Count("queued") + store.Count("running") > 0 || downloads.TryGetValue(id, out var task) && !task.IsCompleted) throw new AppException("model_busy", "请先结束任务并卸载模型。", 409);
            var model = store.Model(id) ?? throw new AppException("model_not_found", "模型不存在。", 404);
            if (model.Engine == InferenceEngines.CTranslate2) DeleteBundle(model); else File.Delete(model.Path);
            store.RemoveModel(id);
            if (store.Settings.DefaultModel == id) store.SaveSettings(store.Settings with { DefaultModel = null });
        }
    }
    private sealed class Lease(Action release) : IDisposable { private Action? action = release; public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke(); }
}
