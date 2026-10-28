using AiLocalApi.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AiLocalApi.Desktop;

internal sealed class ApiHost(ApplicationService service) : IAsyncDisposable
{
    private WebApplication? app;
    public bool Paused { get => service.Paused; set => service.Paused = value; }
    public string Status { get; private set; } = "正在启动";
    public int Port { get; private set; } = service.Store.Settings.Port;
    public async Task StartAsync()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("AILOCAL_PORT"), out var port)) Port = port;
        var builder = WebApplication.CreateSlimBuilder();
        var uploads = Path.Combine(service.Paths.Temporary, "uploads"); Directory.CreateDirectory(uploads);
        Environment.SetEnvironmentVariable("ASPNETCORE_TEMP", uploads);
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => { options.ListenLocalhost(Port); options.Limits.MaxRequestBodySize = service.Store.Settings.MaxUploadBytes + 1048576; });
        ApiRoutes.Configure(builder, service);
        app = builder.Build();
        ApiRoutes.Map(app, service);
        try { await app.StartAsync(); Status = $"运行中 · http://localhost:{Port}"; }
        catch (IOException) { Status = $"端口 {Port} 无法监听。关闭占用程序，或设置 AILOCAL_PORT 后重启。"; service.Paths.Log("api_bind_failed"); }
    }
    public async ValueTask DisposeAsync()
    {
        Paused = true;
        if (app is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await app.StopAsync(timeout.Token); }
            catch (OperationCanceledException) { service.Paths.Log("http_shutdown_timeout"); }
            finally { await app.DisposeAsync(); }
        }
    }
}
