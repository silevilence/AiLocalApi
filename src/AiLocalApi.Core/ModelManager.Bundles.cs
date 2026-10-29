using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using AiLocalApi.Contracts;

namespace AiLocalApi.Core;

public sealed partial class ModelManager
{
    private static CatalogModel[] ReadBundleCatalog()
    {
        using var source = typeof(ModelManager).Assembly.GetManifestResourceStream("AiLocalApi.Core.ctranslate2-catalog.json")!;
        var entries = JsonSerializer.Deserialize<CatalogModel[]>(source, Wire.Json)!;
        var allowed = new HashSet<string> { "model.bin", "config.json", "tokenizer.json", "vocabulary.json", "vocabulary.txt", "preprocessor_config.json" };
        foreach (var model in entries)
        {
            if (!model.Id.StartsWith("ct2-", StringComparison.Ordinal) || model.Files is not { Length: > 2 } || model.Files.Any(f => !allowed.Contains(f.Name)) || model.Files.Select(f => f.Name).Distinct().Count() != model.Files.Length || model.Size != model.Files.Sum(f => f.Size))
                throw new InvalidDataException("Invalid bundled CTranslate2 catalog.");
        }
        return entries;
    }
    private static bool IsPresent(ModelInfo model)
    {
        if (model.Engine != InferenceEngines.CTranslate2) return File.Exists(model.Path) && new FileInfo(model.Path).Length == model.Size;
        var entry = Catalog.SingleOrDefault(m => m.Id == model.Id && m.Engine == model.Engine);
        return entry is { IsBundle: true } && entry.Files!.All(f => File.Exists(Path.Combine(model.Path, f.Name)) && new FileInfo(Path.Combine(model.Path, f.Name)).Length == f.Size);
    }
    private static async Task VerifyFileAsync(string path, ModelFile file, CancellationToken token)
    {
        if (!File.Exists(path)) throw new AppException("model_checksum", $"模型缺少文件：{file.Name}", 422);
        await using var input = File.OpenRead(path);
        if (input.Length != file.Size || Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)) != file.Sha256)
            throw new AppException("model_checksum", $"模型文件大小或 SHA-256 校验失败：{file.Name}", 422);
    }
    private async Task DownloadBundleAsync(CatalogModel model, string directory, CancellationToken token)
    {
        var temporary = Path.Combine(directory, model.Id + ".partial"); Directory.CreateDirectory(temporary);
        long complete = 0;
        foreach (var file in model.Files!)
        {
            var target = Path.Combine(temporary, file.Name);
            var existing = File.Exists(target) ? new FileInfo(target).Length : 0;
            if (existing > file.Size) { File.Move(target, Path.Combine(directory, model.Id + "." + file.Name + ".invalid-" + Guid.NewGuid().ToString("N"))); existing = 0; }
            Progress[model.Id] = new(model.Id, complete + existing, model.Size, "connecting");
            if (availableSpace(directory) < file.Size - existing + 16 * 1024 * 1024) throw new AppException("disk_full", "模型目录磁盘空间不足。");
            if (existing < file.Size)
            {
                var origin = Environment.GetEnvironmentVariable("AILOCAL_MODEL_ORIGIN") ?? "https://" + store.Settings.DownloadHost;
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{origin}/{model.Repository}/resolve/{model.Revision}/{file.Name}");
                if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
                using var header = CancellationTokenSource.CreateLinkedTokenSource(token); header.CancelAfter(TimeSpan.FromSeconds(60));
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, header.Token);
                response.EnsureSuccessStatusCode();
                var resume = response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From == existing;
                if (response.StatusCode == HttpStatusCode.PartialContent && !resume) throw new IOException("下载服务器返回了错误的 Range。");
                if (!resume) existing = 0;
                await using var output = new FileStream(target, resume ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                await using var input = await response.Content.ReadAsStreamAsync(token);
                var buffer = new byte[81920];
                while (true)
                {
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(token); stall.CancelAfter(TimeSpan.FromSeconds(30));
                    var count = await input.ReadAsync(buffer, stall.Token); if (count == 0) break;
                    existing += count; if (existing > file.Size) throw new IOException("下载超过可信文件大小。");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    Progress[model.Id] = new(model.Id, complete + existing, model.Size, "downloading");
                }
            }
            Progress[model.Id] = new(model.Id, complete + file.Size, model.Size, "verifying");
            try { await VerifyFileAsync(target, file, token); }
            catch (AppException) { File.Move(target, Path.Combine(directory, model.Id + "." + file.Name + ".invalid-" + Guid.NewGuid().ToString("N"))); throw; }
            complete += file.Size;
        }
        token.ThrowIfCancellationRequested();
        if (Directory.EnumerateDirectories(temporary).Any() || Directory.EnumerateFiles(temporary).Any(path => !model.Files.Any(file => file.Name == Path.GetFileName(path))))
            throw new AppException("model_extra_files", "临时模型目录包含额外文件，未登记为可用。", 409);
        lock (Sync)
        {
            if (leases.ContainsKey(model.Id)) throw new AppException("model_busy", "模型正在使用。", 409);
            var destination = Path.Combine(directory, model.Id);
            if (Directory.Exists(destination)) Directory.Move(destination, destination + ".invalid-" + Guid.NewGuid().ToString("N"));
            Directory.Move(temporary, destination);
            RegisterBundle(model, destination, model.Source);
        }
        Progress[model.Id] = new(model.Id, model.Size, model.Size, "available");
    }
    private void RegisterBundle(CatalogModel model, string path, string source)
    {
        store.SaveModel(new(model.Id, path, model.Sha256, model.Size, model.Multilingual, model.Quantization, source, model.Engine));
        if (store.Settings.DefaultModel is null) store.SaveSettings(store.Settings with { DefaultModel = model.Id });
    }
    private async Task ImportBundleAsync(string path, CancellationToken token)
    {
        var weights = Path.Combine(path, "model.bin");
        if (!File.Exists(weights)) throw new AppException("untrusted_model", "模型目录缺少 model.bin。");
        await using var file = File.OpenRead(weights);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token));
        var model = Catalog.SingleOrDefault(m => m.IsBundle && m.Sha256 == hash) ?? throw new AppException("untrusted_model", "模型包不在可信目录中。");
        await VerifyAsync(path, model, token);
        lock (Sync)
        {
            if (leases.ContainsKey(model.Id) || downloads.TryGetValue(model.Id, out var active) && !active.IsCompleted) throw new AppException("model_busy", "模型正在使用或下载。", 409);
            var destination = Path.Combine(DirectoryPath, model.Id);
            if (!string.Equals(Path.GetFullPath(path), destination, StringComparison.OrdinalIgnoreCase))
            {
                var temporary = destination + ".import-" + Guid.NewGuid().ToString("N"); Directory.CreateDirectory(temporary);
                foreach (var item in model.Files!) File.Copy(Path.Combine(path, item.Name), Path.Combine(temporary, item.Name));
                // Verify the copied snapshot too: the source may have changed during import.
                VerifyAsync(temporary, model, token).GetAwaiter().GetResult();
                if (Directory.Exists(destination)) Directory.Move(destination, destination + ".previous-" + Guid.NewGuid().ToString("N"));
                Directory.Move(temporary, destination);
            }
            RegisterBundle(model, destination, "local import");
        }
    }
    private static void DeleteBundle(ModelInfo model)
    {
        if (!Directory.Exists(model.Path)) return;
        if ((File.GetAttributes(model.Path) & FileAttributes.ReparsePoint) != 0)
            throw new AppException("model_extra_files", "模型目录是链接，未执行删除。", 409);
        var catalog = Catalog.Single(m => m.Id == model.Id && m.IsBundle);
        var expected = catalog.Files!.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Directory.EnumerateDirectories(model.Path).Any() || Directory.EnumerateFiles(model.Path).Any(f => !expected.Contains(Path.GetFileName(f))))
            throw new AppException("model_extra_files", "模型目录包含额外文件，未执行删除。", 409);
        foreach (var file in catalog.Files!) File.Delete(Path.Combine(model.Path, file.Name));
        Directory.Delete(model.Path);
    }
}
