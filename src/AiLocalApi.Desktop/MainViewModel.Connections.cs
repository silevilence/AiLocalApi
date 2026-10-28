using System.Collections.ObjectModel;
using AiLocalApi.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalApi.Desktop;

public sealed record KeyRow(ApiKeyInfo Info)
{
    public string Name => Info.Name;
    public string Detail => $"{Info.Prefix} · {(Info.Enabled ? "已启用" : "已停用")} · 创建于 {Info.Created.LocalDateTime:yyyy-MM-dd HH:mm}";
}
public partial class MainViewModel
{
    public ObservableCollection<KeyRow> Keys { get; } = [];
    [ObservableProperty] private KeyRow? selectedKey;
    [ObservableProperty] private string keyName = "";
    [ObservableProperty] private string customKey = "";
    [ObservableProperty] private string createdSecret = "";
    [ObservableProperty] private bool allowAnonymous;
    [ObservableProperty] private string keyMessage = "密钥变更立即生效，已接受的任务不会中断。";
    [ObservableProperty] private string backendTestResult = "测试会临时加载已安装模型，结束后释放资源。";
    [ObservableProperty] private string sourceTestResult = "测试所选源是否返回模型文件；不会下载整个模型。";
    [ObservableProperty] private string restartResult = "仅监听本机。重启 API 保留异步任务；进行中的同步 HTTP 请求可能中断。";
    private bool updatingPolicy;
    [ObservableProperty] private bool policySaving;
    public Task PolicyChange { get; private set; } = Task.CompletedTask;
    public string LiveEndpoint => api.IsRunning ? $"当前地址：http://localhost:{api.Port}" : api.Status;
    public bool HasSecret => CreatedSecret.Length > 0;
    public bool HasKeys => Keys.Count > 0;
    public bool HasSelectedKey => SelectedKey is not null;
    public string ToggleKeyLabel => SelectedKey?.Info.Enabled == true ? "停用密钥" : "启用密钥";
    partial void OnCreatedSecretChanged(string value) => OnPropertyChanged(nameof(HasSecret));
    partial void OnSelectedKeyChanged(KeyRow? value) { OnPropertyChanged(nameof(HasSelectedKey)); OnPropertyChanged(nameof(ToggleKeyLabel)); }
    partial void OnBackendChanged(string value) => BackendTestResult = "尚未测试当前选择。测试会临时加载一个已安装模型。";
    partial void OnDownloadHostChanged(string value) => SourceTestResult = "尚未测试当前选择。测试不会保存设置或下载整个模型。";
    partial void OnAllowAnonymousChanged(bool value)
    {
        if (updatingPolicy) return;
        PolicyChange = ApplyPolicyAsync(value);
    }
    private async Task ApplyPolicyAsync(bool value)
    {
        PolicySaving = true;
        try { await Task.Run(() => service.Store.SetAllowAnonymous(value)); KeyMessage = value ? "已允许不带密钥的本机请求。携带无效密钥的请求仍会拒绝。" : "已要求有效密钥；请为客户端配置一个已启用的密钥。"; OnPropertyChanged(nameof(ApiExample)); }
        catch (Exception error) { KeyMessage = error.Message; updatingPolicy = true; AllowAnonymous = !value; updatingPolicy = false; }
        finally { PolicySaving = false; }
    }
    private void UpdateKeys(ApiKeyInfo[] keys, bool anonymous)
    {
        var rows = keys.Select(key => new KeyRow(key)).ToArray();
        if (!Keys.SequenceEqual(rows)) { var id = SelectedKey?.Info.Id; Keys.Clear(); foreach (var row in rows) Keys.Add(row); SelectedKey = Keys.FirstOrDefault(row => row.Info.Id == id); }
        if (!PolicySaving) { updatingPolicy = true; AllowAnonymous = anonymous; updatingPolicy = false; }
        OnPropertyChanged(nameof(HasKeys));
    }
    [RelayCommand] private async Task CreateKey()
    {
        try
        {
            var result = await Task.Run(() => service.Store.CreateApiKey(KeyName, CustomKey.Length == 0 ? null : CustomKey));
            CreatedSecret = result.Secret; KeyName = ""; CustomKey = "";
            KeyMessage = $"已创建“{result.Info.Name}”。完整密钥仅在此展示，请复制并保存。";
            await RefreshAsync(); SelectedKey = Keys.FirstOrDefault(key => key.Info.Id == result.Info.Id);
        }
        catch (Exception error) { KeyMessage = error.Message; }
    }
    [RelayCommand] private async Task ToggleKey()
    {
        if (SelectedKey is not { } key) return;
        try { await Task.Run(() => service.Store.SetApiKeyEnabled(key.Info.Id, !key.Info.Enabled)); KeyMessage = key.Info.Enabled ? "密钥已停用。" : "密钥已启用。"; await RefreshAsync(); }
        catch (Exception error) { KeyMessage = error.Message; }
    }
    [RelayCommand] private async Task DeleteKey()
    {
        if (SelectedKey is not { } key || Confirm is null || !await Confirm("删除 API 密钥", $"删除“{key.Name}”后，使用此密钥的客户端将无法继续调用。此操作无法撤销。")) return;
        try { await Task.Run(() => service.Store.DeleteApiKey(key.Info.Id)); CreatedSecret = ""; KeyMessage = "密钥已删除。"; await RefreshAsync(); }
        catch (Exception error) { KeyMessage = error.Message; }
    }
    [RelayCommand] private async Task CopySecret()
    {
        if (!HasSecret) return;
        try { await Clipboard!(CreatedSecret); KeyMessage = "密钥已复制，请妥善保存；完成后点击隐藏。"; }
        catch (Exception error) { KeyMessage = error.Message; }
    }
    [RelayCommand] private void HideSecret() => CreatedSecret = "";
    [RelayCommand(IncludeCancelCommand = true)] private async Task TestBackend(CancellationToken cancellationToken)
    {
        var selected = Backend; BackendTestResult = $"正在测试 {selected}，最长 60 秒…";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try { BackendTestResult = await Task.Run(() => ConnectionDiagnostics.TestBackendAsync(service, selected, timeout.Token), timeout.Token); }
        catch (OperationCanceledException) { BackendTestResult = cancellationToken.IsCancellationRequested ? "测试已取消。" : "测试超时，请检查模型或驱动后重试。"; }
        catch (Exception error) { BackendTestResult = $"{selected} 测试失败：{error.Message}"; }
    }
    [RelayCommand(IncludeCancelCommand = true)] private async Task TestSource(CancellationToken cancellationToken)
    {
        var selected = DownloadHost; SourceTestResult = $"正在连接 {selected}，最长 20 秒…";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { SourceTestResult = await ConnectionDiagnostics.TestSourceAsync(selected, timeout.Token); }
        catch (OperationCanceledException) { SourceTestResult = cancellationToken.IsCancellationRequested ? "测试已取消。" : "连接超时，可选择另一下载源后重试。"; }
        catch (Exception error) { SourceTestResult = $"{selected} 测试失败：{error.Message}"; }
    }
    [RelayCommand] private async Task RestartApi()
    {
        RestartResult = "正在应用端口并重启 API…";
        try { await api.RestartAsync((int)Port); RestartResult = $"API 已在端口 {api.Port} 运行，端口设置已保存。"; }
        catch (Exception error) { RestartResult = error.Message; }
        await RefreshAsync(); OnPropertyChanged(nameof(ApiExample)); OnPropertyChanged(nameof(LiveEndpoint));
    }
}
