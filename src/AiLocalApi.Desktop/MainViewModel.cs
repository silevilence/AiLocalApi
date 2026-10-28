using System.Collections.ObjectModel;
using AiLocalApi.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalApi.Desktop;
public sealed record ModelRow(string Id, string Summary, string State, bool Installed = false, bool Downloading = false);
public sealed record JobRow(string Id, string Title, string State, string Stage)
{
    public string StateLabel => MainViewModel.Localize(State);
    public string StageLabel => MainViewModel.Localize(Stage);
}
public sealed record LanguageChoice(string Label, string? Code);
public partial class MainViewModel : ObservableObject
{
    private readonly ApplicationService service;
    private readonly ApiHost api;
    private bool refreshing;
    private string? lastSnapshot;
    public ObservableCollection<ModelRow> Models { get; } = [];
    public ObservableCollection<JobRow> Jobs { get; } = [];
    public string[] Backends { get; } = ["cpu", "auto", "vulkan", "cuda"];
    public string[] DownloadHosts { get; } = ["huggingface.co", "hf-mirror.com"];
    [ObservableProperty] private string downloadHost = "huggingface.co";
    public string[] Formats { get; } = ["text", "srt", "vtt", "json", "verbose_json"];
    [ObservableProperty] private string status = "正在启动";
    [ObservableProperty] private string message = "先选择模型目录，再下载或导入模型。";
    [ObservableProperty] private string modelDirectory = "";
    [ObservableProperty] private string backend = "cpu";
    [ObservableProperty] private decimal port = 17840;
    [ObservableProperty] private decimal idleSeconds = 300;
    [ObservableProperty] private decimal retentionDays = 7;
    [ObservableProperty] private bool keepLoaded;
    [ObservableProperty] private bool autoStart;
    [ObservableProperty] private bool paused;
    [ObservableProperty] private ModelRow? selectedModel;
    [ObservableProperty] private JobRow? selectedJob;
    [ObservableProperty] private string resultText = "选择任务查看结果。";
    [ObservableProperty] private string exportFormat = "text";
    [ObservableProperty] private string pageLabel = "第 1 页 · 每页 50 项";
    [ObservableProperty] private string backendDetails = "模型加载后显示运行库诊断。";
    private int page;
    public Func<Task<string?>>? PickDirectory { get; set; }
    public Func<bool, Task<string?>>? PickFile { get; set; }
    public Func<string, Task<string?>>? PickExport { get; set; }
    public Func<string, Task>? Clipboard { get; set; }
    public string ApiExample => $"curl.exe http://localhost:{api.Port}/v1/audio/transcriptions{(AllowAnonymous ? "" : " -H \"Authorization: Bearer YOUR_KEY\"")} -F \"file=@audio.wav\" -F \"model=whisper-1\"";
    public Func<string, string, Task<bool>>? Confirm { get; set; }
    public LanguageChoice[] Languages { get; } = [new("自动检测", null), new("中文", "zh"), new("日本語", "ja"), new("English", "en"), new("한국어", "ko")];
    [ObservableProperty] private LanguageChoice selectedLanguage = new("自动检测", null);
    [ObservableProperty] private bool hasJobs;
    [ObservableProperty] private bool canPrevious;
    [ObservableProperty] private bool canNext;
    public bool CanCancelJob => SelectedJob?.State is "queued" or "running";
    public bool CanRetryJob => SelectedJob?.State is "failed" or "cancelled";
    public bool CanExport => SelectedJob?.State is "succeeded";
    public string PauseLabel => Paused ? "恢复接单" : "暂停接单";
    public bool CanUseModel => SelectedModel?.Installed == true;
    public bool CanDownload => SelectedModel is { } model && !CanUseModel && !model.Downloading && ModelDirectory.Length > 0;
    public bool CanCancelDownload => SelectedModel is { } model && model.Downloading;
    public bool CanDeleteModel => CanUseModel && service.LoadedModel != SelectedModel?.Id;
    [ObservableProperty] private bool canUnload;
    partial void OnPausedChanged(bool value) => OnPropertyChanged(nameof(PauseLabel));
    partial void OnSelectedModelChanged(ModelRow? value) => NotifyActions();
    private void NotifyActions()
    {
        foreach (var name in new[] { nameof(CanCancelJob), nameof(CanRetryJob), nameof(CanExport), nameof(CanUseModel), nameof(CanDownload), nameof(CanCancelDownload), nameof(CanDeleteModel), nameof(CanUnload), nameof(ApiExample), nameof(PauseLabel) }) OnPropertyChanged(name);
    }
    public static string Localize(string value) => value switch
    {
        "queued" => "排队中", "running" => "处理中", "succeeded" => "已完成", "failed" => "失败", "cancelled" => "已取消", "expired" => "已过期",
        "loading" => "加载模型", "decoding" => "解码音频", "transcribing" => "转写中", "completed" => "完成", "interrupted" => "执行中断",
        "unloaded" or "not loaded" => "未加载", "loaded" or "ready" => "已加载", "unloading" => "卸载中", "connecting" => "连接中", "downloading" => "下载中", "verifying" => "校验中", "available" => "可用", _ => value
    };

    public MainViewModel(ApplicationService service, ApiHost api)
    {
        this.service = service; this.api = api;
        var settings = service.Store.Settings;
        DownloadHost = settings.DownloadHost; Message = settings.DefaultModel is null ? "先选择模型目录，再下载或导入模型。" : "选择音频开始转写，或查看已有任务。"; ModelDirectory = settings.ModelDirectory ?? ""; Backend = settings.Backend; Port = settings.Port; IdleSeconds = settings.IdleSeconds; KeepLoaded = settings.KeepLoaded; RetentionDays = settings.RetentionDays; AutoStart = LoginStartup.Enabled; Paused = service.Paused;
    }
    public async Task RefreshAsync()
    {
        if (refreshing) return; refreshing = true;
        try
        {
            Status = $"{api.Status} · {(service.Paused ? "暂停接单" : "接受任务")} · 后端 {Localize(service.Worker.Backend)} · 模型 {Localize(service.ModelState)}";
            BackendDetails = $"{service.Worker.BackendDetails}\n{service.LastError}".Trim(); Paused = service.Paused;
            var snapshot = $"{service.Store.Revision}/{service.LoadedModel}/{service.Worker.ProcessId}/{page}/{string.Join(';', service.Models.Progress.Values)}";
            NotifyActions();
            OnPropertyChanged(nameof(LiveEndpoint));
            if (lastSnapshot == snapshot) return;
            var data = await Task.Run(() =>
            {
                var installed = service.Models.Installed;
                var models = ModelManager.Catalog.Select(model => new ModelRow(model.Id, $"{(model.Multilingual ? "多语言 / 英文翻译" : "仅英语")} · {model.Quantization} · {model.Size / 1048576d:F1} MB", service.Models.Progress.TryGetValue(model.Id, out var progress) && progress.State != "available" ? $"{Localize(progress.State)} · {progress.Received / 1048576d:F1} MB {progress.Error}" : installed.Any(item => item.Id == model.Id) ? $"已安装{(service.Store.Settings.DefaultModel == model.Id ? " · 默认" : "")}{(service.LoadedModel == model.Id ? " · 驻留" : "")}" : "可下载", installed.Any(item => item.Id == model.Id), progress?.State is "connecting" or "downloading" or "verifying")).ToArray();
                return (CanUnload: service.Worker.ProcessId is not null && service.Store.Count("running") == 0, AllowAnonymous: service.Store.AllowAnonymous, Total: service.Store.TotalJobs(), Keys: service.Store.ApiKeys(), Models: models, Jobs: service.Store.Jobs(page).Select(job => new JobRow(job.Id, $"{job.Created.LocalDateTime:MM-dd HH:mm} · {job.Model}", job.State, job.Stage)).ToArray());
            });
            if (!Models.SequenceEqual(data.Models)) { var id = SelectedModel?.Id; Models.Clear(); foreach (var model in data.Models) Models.Add(model); SelectedModel = Models.FirstOrDefault(model => model.Id == id) ?? Models.FirstOrDefault(); }
            if (!Jobs.SequenceEqual(data.Jobs)) { var id = SelectedJob?.Id; Jobs.Clear(); foreach (var job in data.Jobs) Jobs.Add(job); SelectedJob = Jobs.FirstOrDefault(job => job.Id == id); }
            HasJobs = data.Jobs.Length > 0; CanPrevious = page > 0; CanNext = (page + 1) * 50 < data.Total;
            PageLabel = $"第 {page + 1} 页 · 共 {data.Total} 项";
            CanUnload = data.CanUnload;
            UpdateKeys(data.Keys, data.AllowAnonymous);
            lastSnapshot = snapshot;
            NotifyActions();
            Status = $"{api.Status} · {(service.Paused ? "暂停接单" : "接受任务")} · 后端 {Localize(service.Worker.Backend)} · 模型 {Localize(service.ModelState)}";
            BackendDetails = $"{service.Worker.BackendDetails}\n{service.LastError}".Trim();
            Paused = service.Paused;
        }
        catch (Exception error) { Message = error.Message; }
        finally { refreshing = false; }
    }
    private async Task Run(Func<Task> action, string success)
    {
        try { await action(); Message = success; await RefreshAsync(); }
        catch (OperationCanceledException) { Message = "操作已取消，可稍后重试。"; }
        catch (Exception error) { Message = error.Message; }
    }
    [RelayCommand] private async Task ChooseDirectory() { var path = await PickDirectory!(); if (path is not null) await Run(async () => { await Task.Run(() => service.Models.SetDirectory(path)); ModelDirectory = path; }, "目录已更新。已安装模型保留原位置，新下载使用此目录。"); }
    [RelayCommand] private async Task Download() { var id = SelectedModel?.Id; if (id is not null) await Run(() => Task.Run(() => service.Models.DownloadAsync(id)), "模型下载及 SHA-256 校验完成。"); }
    [RelayCommand] private void CancelDownload() { if (SelectedModel is { } model) service.Models.CancelDownload(model.Id); }
    [RelayCommand] private async Task Import() { var file = await PickFile!(true); if (file is not null) await Run(() => Task.Run(() => service.Models.ImportAsync(file)), "模型导入完成。"); }
    [RelayCommand] private async Task SetDefault() { if (SelectedModel is { } model) await Run(() => Task.Run(() => { _ = service.Models.Require(model.Id); service.Store.SaveSettings(service.Store.Settings with { DefaultModel = model.Id }); }), "默认模型已更新，whisper-1 指向此模型。"); }
    [RelayCommand] private async Task DeleteModel()
    {
        if (SelectedModel is { } model && Confirm is not null && await Confirm("删除模型", $"将删除 {model.Id} 对应的本地文件，之后需要重新下载或导入。继续删除？"))
            await Run(() => Task.Run(() => service.Models.Delete(model.Id)), "模型已删除。");
    }
    [RelayCommand] private Task Unload() => Run(() => Task.Run(service.UnloadAsync), "模型已卸载，Worker 已回收。");
    [RelayCommand] private async Task AddAudio() { var path = await PickFile!(false); if (path is not null) await AddFileAsync(path); }
    public Task AddFileAsync(string path) => Run(async () => { await Task.Run(() => service.SubmitFileAsync(path, "whisper-1", new(Language: SelectedLanguage.Code))); page = 0; PageLabel = "第 1 页 · 每页 50 项"; }, "音频已加入任务队列，关闭窗口后仍会继续。");
    [RelayCommand] private Task CancelJob() => SelectedJob is { } job ? Run(() => Task.Run(() => service.Cancel(job.Id)), "已请求取消。") : Task.CompletedTask;
    [RelayCommand] private Task RetryJob() => SelectedJob is { } job ? Run(() => Task.Run(() => service.Retry(job.Id)), "任务已重新排队。") : Task.CompletedTask;
    partial void OnSelectedJobChanged(JobRow? value)
    {
        NotifyActions();
        if (value is null) { ResultText = "选择任务查看结果。"; return; }
        _ = ShowResultAsync(value.Id);
    }
    private async Task ShowResultAsync(string id)
    {
        try
        {
        var job = await Task.Run(() => service.Get(id));
        if (SelectedJob?.Id == id) ResultText = job.Result?.Text ?? job.Error ?? $"{Localize(job.State)} · {Localize(job.Stage)}";
        }
        catch (Exception error) { Message = error.Message; }
    }
    [RelayCommand] private async Task Export()
    {
        if (SelectedJob is not { State: "succeeded" } selected) { Message = "请先选择已完成任务。"; return; }
        var path = await PickExport!(ExportFormat); if (path is null) return;
        await Run(async () => { var job = await Task.Run(() => service.Get(selected.Id)); if (job.Result is null) throw new AppException("result_missing", "此任务尚无结果。"); await File.WriteAllTextAsync(path, TranscriptFormats.Format(job.Result, ExportFormat, job.Options.Words).Content); }, "结果已导出。");
    }
    [RelayCommand] private async Task PreviousPage() { if (page > 0) page--; PageLabel = $"第 {page + 1} 页 · 每页 50 项"; await RefreshAsync(); }
    [RelayCommand] private async Task NextPage() { if (CanNext) page++; PageLabel = $"第 {page + 1} 页 · 每页 50 项"; await RefreshAsync(); }
    [RelayCommand] private async Task SaveSettings() => await Run(() => Task.Run(() =>
    {
        if (Backend != service.Store.Settings.Backend && (service.Store.Count("queued") + service.Store.Count("running") > 0)) throw new AppException("busy", "请等待在途任务结束后切换后端。");
        service.Store.SaveSettings(service.Store.Settings with { DownloadHost = DownloadHost, Port = (int)Port, IdleSeconds = (int)IdleSeconds, KeepLoaded = KeepLoaded, Backend = Backend, RetentionDays = (int)RetentionDays });
        LoginStartup.Set(AutoStart);
    }), "设置已保存。若端口尚未应用，请点击“应用端口并重启 API”；后端在下次加载模型时生效。");
    [RelayCommand] private void TogglePause() { service.Paused = !service.Paused; Paused = service.Paused; }
    [RelayCommand] private Task CopyExample() => Run(() => Clipboard!(ApiExample), "调用示例已复制；请替换音频路径和 API key。");
}
