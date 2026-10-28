using System.Text.Json;
using AiLocalApi.Core;
using Velopack;
using Velopack.Sources;

namespace AiLocalApi.Desktop;

public sealed class AppUpdates(ApplicationService service, Func<Task> exit)
{
    private readonly SemaphoreSlim gate = new(1);
    public bool IsBusy => gate.CurrentCount == 0;
    public string Status { get; private set; } = Distribution.Packaged ? "等待检查更新" : "开发运行 · 更新已禁用";
    public string Notes { get; private set; } = "";
    public string Source { get; private set; } = "";
    public event Action? Changed;
    private void Report(string state)
    {
        Status = state; Changed?.Invoke();
        service.Paths.Log("update: " + state);
    }
    public async Task CheckAndApplyAsync()
    {
        if (!Distribution.Packaged || !await gate.WaitAsync(0)) return;
        try
        {
            using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "release.json")));
            Source = Environment.GetEnvironmentVariable("AILOCAL_UPDATE_SOURCE") ?? config.RootElement.GetProperty("repository").GetString()!;
            var options = new UpdateOptions { ExplicitChannel = "win", AllowVersionDowngrade = false };
            var manager = Source.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
                ? new UpdateManager(new GithubSource(Source, Environment.GetEnvironmentVariable("AILOCAL_GITHUB_TOKEN"), false), options)
                : new UpdateManager(Source, options);
            Report("正在检查更新");
            var update = await manager.CheckForUpdatesAsync();
            if (update is null) { Report("当前已是最新版本"); return; }
            if (update.TargetFullRelease.Version.IsPrerelease) throw new IOException("正式通道拒绝预发布版本。");
            Notes = update.TargetFullRelease.NotesMarkdown ?? "";
            Report($"正在下载 {update.TargetFullRelease.Version}");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            await manager.DownloadUpdatesAsync(update, progress => Report($"下载更新 {progress}%"), timeout.Token);
            Report("更新已校验 · 等待当前转写完成，暂不接受新任务");
            await service.DrainForUpdateAsync();
            Report("正在更新并重启 · API 将短暂不可用");
            // Only start the updater's bounded wait AFTER unbounded inference drain and Worker exit.
            manager.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: true, restartArgs: ["--tray"]);
            await exit();
        }
        catch (Exception error)
        {
            service.ResumeAfterUpdateFailure();
            Report("更新失败，可重试：" + error.Message);
        }
        finally { gate.Release(); Changed?.Invoke(); }
    }
    public async Task AutomaticAsync(CancellationToken token)
    {
        if (!Distribution.Packaged || Environment.GetEnvironmentVariable("AILOCAL_DISABLE_UPDATE_CHECK") == "1") return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            while (!token.IsCancellationRequested) { await CheckAndApplyAsync(); await Task.Delay(TimeSpan.FromHours(6), token); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
