using System.Net;
using System.Net.Sockets;
using AiLocalApi.Contracts;
using AiLocalApi.Core;

namespace AiLocalApi.Verify;

internal static class BundleRegression
{
    public static async Task DiskFullAsync(string data, string destination, string source)
    {
        var entry = ModelManager.Catalog.Single(m => m.Id == "ct2-tiny");
        if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destination))!).AvailableFreeSpace >= entry.Files!.Single(f => f.Name == "model.bin").Size)
            throw new Exception("Use a dedicated test volume with less than 75 MB free.");
        await using var service = new ApplicationService(new(Path.GetFullPath(data)));
        service.Models.SetDirectory(destination);
        var partial = Path.Combine(destination, "ct2-tiny.partial"); Directory.CreateDirectory(partial);
        File.Copy(Path.Combine(source, "config.json"), Path.Combine(partial, "config.json"), true);
        try { await service.Models.DownloadAsync(entry.Id); throw new Exception("Disk exhaustion was not rejected"); }
        catch (AppException error) when (error.Code == "disk_full") { }
        if (service.Models.Installed.Length != 0) throw new Exception("Incomplete model was registered");
        Console.WriteLine("PASS: insufficient disk space rejected before weight download; no installed model.");
    }

    public static async Task RunAsync(string data, string source)
    {
        data = Path.GetFullPath(data); source = Path.GetFullPath(source);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
        using var listener = new HttpListener(); var origin = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(origin + "/"); listener.Start();
        var previous = Environment.GetEnvironmentVariable("AILOCAL_MODEL_ORIGIN");
        Environment.SetEnvironmentVariable("AILOCAL_MODEL_ORIGIN", origin);
        var resumed = false; var corruptOnce = true;
        var server = Task.Run(async () =>
        {
            while (!timeout.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync().WaitAsync(timeout.Token); }
                catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or OperationCanceledException) { break; }
                try
                {
                    var name = Path.GetFileName(context.Request.Url!.AbsolutePath);
                    if (!ModelManager.Catalog.Single(m => m.Id == "ct2-tiny").Files!.Any(f => f.Name == name)) { context.Response.StatusCode = 404; continue; }
                    var content = await File.ReadAllBytesAsync(Path.Combine(source, name), timeout.Token);
                    if (name == "tokenizer.json" && corruptOnce) { content[0] ^= 1; corruptOnce = false; }
                    var offset = 0;
                    if (context.Request.Headers["Range"] is { } range)
                    {
                        offset = int.Parse(range[6..].TrimEnd('-')); resumed |= offset > 0;
                        context.Response.StatusCode = 206;
                        context.Response.Headers["Content-Range"] = $"bytes {offset}-{content.Length - 1}/{content.Length}";
                    }
                    context.Response.ContentLength64 = content.Length - offset;
                    for (; offset < content.Length; offset += 65536)
                    {
                        await context.Response.OutputStream.WriteAsync(content.AsMemory(offset, Math.Min(65536, content.Length - offset)), timeout.Token);
                        if (name == "model.bin") await Task.Delay(2, timeout.Token);
                    }
                }
                catch (Exception error) when (error is HttpListenerException or IOException or OperationCanceledException) { }
                finally { context.Response.Close(); }
            }
        });
        try
        {
            var destination = Path.Combine(data, "下载 中文目录");
            await using (var first = new ApplicationService(new(Path.Combine(data, "state"))))
            {
                first.Models.SetDirectory(destination);
                var download = first.Models.DownloadAsync("ct2-tiny");
                if (!ReferenceEquals(download, first.Models.DownloadAsync("ct2-tiny"))) throw new Exception("Concurrent downloads were not merged");
                while (!download.IsCompleted && first.Models.Progress.GetValueOrDefault("ct2-tiny")?.Received < 1048576) await Task.Delay(10, timeout.Token);
                first.Models.CancelDownload("ct2-tiny");
                try { await download; } catch (OperationCanceledException) { }
                if (first.Models.Installed.Length != 0) throw new Exception("Partial model registered");
            }
            await using var second = new ApplicationService(new(Path.Combine(data, "state")));
            try { await second.Models.DownloadAsync("ct2-tiny"); throw new Exception("Corrupt tokenizer accepted"); }
            catch (AppException error) when (error.Code == "model_checksum") { }
            if (second.Models.Installed.Length != 0 || !resumed) throw new Exception("Restart/resume or corruption isolation failed");
            await second.Models.DownloadAsync("ct2-tiny");
            var model = second.Models.Require("ct2-tiny");
            await ModelManager.VerifyAsync(model.Path, ModelManager.Catalog.Single(m => m.Id == model.Id), timeout.Token);
            await using var offline = new ApplicationService(new(Path.Combine(data, "offline")));
            offline.Models.SetDirectory(Path.Combine(data, "离线 导入"));
            await offline.Models.ImportAsync(model.Path, timeout.Token);
            using (offline.Models.Acquire(model.Id))
            {
                try { offline.Models.Delete(model.Id); throw new Exception("Leased model deleted"); }
                catch (AppException error) when (error.Code == "model_busy") { }
            }
            offline.Models.Delete(model.Id);
            if (!Directory.Exists(source) || !Directory.Exists(model.Path)) throw new Exception("Import source was removed");
            second.Models.Delete(model.Id);
            if (second.Models.Installed.Length != 0) throw new Exception("Deleted bundle still installed");
            Console.WriteLine("PASS: cancellation, restart/Range resume, corrupt tokenizer rejection, retry, offline Unicode import, lease protection and safe deletion.");
        }
        finally { Environment.SetEnvironmentVariable("AILOCAL_MODEL_ORIGIN", previous); listener.Stop(); timeout.Cancel(); await server; }
    }
}
