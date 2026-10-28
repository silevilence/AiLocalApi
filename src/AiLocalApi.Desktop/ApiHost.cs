using AiLocalApi.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace AiLocalApi.Desktop;

public sealed class ApiHost(ApplicationService service) : IAsyncDisposable
{
    private WebApplication? app;
    private readonly SemaphoreSlim gate = new(1);
    private bool disposed;
    public bool Paused { get => service.Paused; set => service.Paused = value; }
    public bool IsRunning => app is not null;
    public event Action? Changed;
    public string Status { get; private set; } = "正在启动";
    public int Port { get; private set; } = service.Store.Settings.Port;

    public async Task StartAsync()
    {
        var port = int.TryParse(Environment.GetEnvironmentVariable("AILOCAL_PORT"), out var configured) ? configured : Port;
        try { await RestartAsync(port, persist: false); }
        catch (Exception) { Status = $"API 未启动 · 端口 {port}（请在设置中重试）"; Changed?.Invoke(); }
    }

    public async Task RestartAsync(int port, bool persist = true)
    {
        if (port is < 1024 or > 65535) throw new AppException("invalid_port", "端口必须介于 1024 和 65535 之间。");
        await gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var previous = app; var oldPort = Port;
            // Bind the new port first: a failed bind must leave the existing listener available.
            if (previous is not null && port == oldPort) { app = null; await StopAsync(previous); previous = null; }
            WebApplication replacement;
            try { replacement = await BindAsync(port); }
            catch (Exception error)
            {
                if (app is null && port == oldPort)
                {
                    try { app = await BindAsync(oldPort); } catch { Status = $"API 未启动 · 端口 {port}"; }
                }
                Changed?.Invoke();
                throw new AppException("api_bind_failed", $"无法监听端口 {port}，请检查是否被占用。{(app is not null ? $"当前服务仍在端口 {Port} 运行。" : "可更换端口后重试。")} {error.Message}", 409);
            }
            try { if (persist) service.Store.SaveSettings(service.Store.Settings with { Port = port }); }
            catch
            {
                await StopAsync(replacement);
                if (app is null)
                {
                    try { app = await BindAsync(oldPort); }
                    catch { Status = $"API 未启动 · 端口 {oldPort}"; }
                    Changed?.Invoke();
                }
                throw;
            }
            app = replacement; Port = port; Status = $"运行中 · http://localhost:{Port}"; Changed?.Invoke();
            if (previous is not null) await StopAsync(previous);
        }
        finally { gate.Release(); }
    }

    private async Task<WebApplication> BindAsync(int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        var uploads = Path.Combine(service.Paths.Temporary, "uploads"); Directory.CreateDirectory(uploads);
        Environment.SetEnvironmentVariable("ASPNETCORE_TEMP", uploads);
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => { options.ListenLocalhost(port); options.Limits.MaxRequestBodySize = service.Store.Settings.MaxUploadBytes + 1048576; });
        ApiRoutes.Configure(builder, service);
        var candidate = builder.Build(); ApiRoutes.Map(candidate, service);
        try { await candidate.StartAsync(); return candidate; }
        catch { await candidate.DisposeAsync(); service.Paths.Log("api_bind_failed"); throw; }
    }
    private async Task StopAsync(WebApplication host)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await host.StopAsync(timeout.Token); }
        catch (OperationCanceledException) { service.Paths.Log("http_shutdown_timeout"); }
        finally { await host.DisposeAsync(); }
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { disposed = true; if (app is { } host) { app = null; await StopAsync(host); } Status = "API 已停止"; Changed?.Invoke(); }
        finally { gate.Release(); }
    }
}
