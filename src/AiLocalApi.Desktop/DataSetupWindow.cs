using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AiLocalApi.Desktop;

internal sealed class DataSetupWindow : Window
{
    private readonly TaskCompletionSource<string?> completion = new();
    public Task<string?> Selection => completion.Task;
    public DataSetupWindow()
    {
        Title = "AiLocalApi · 首次运行"; Width = 600; SizeToContent = SizeToContent.Height; CanResize = false;
        var path = new TextBox { Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiLocalApi", Distribution.Identity) };
        var message = new TextBlock { Text = "选择持久数据目录，保存配置、任务和日志。模型目录可稍后单独选择；确认前不会下载模型。卸载应用会保留这些数据。", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var browse = new Button { Content = "浏览目录" };
        browse.Click += async (_, _) => { var folder = (await StorageProvider.OpenFolderPickerAsync(new() { Title = "选择持久数据目录" })).FirstOrDefault()?.TryGetLocalPath(); if (folder is not null) path.Text = folder; };
        var start = new Button { Content = "确认并启动" };
        start.Click += (_, _) => { try { Distribution.SaveData(path.Text ?? ""); completion.TrySetResult(Distribution.ResolveData()); Close(); } catch (Exception error) { message.Text = error.Message; } };
        Content = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children = { message, path, browse, start } };
        Closed += (_, _) => completion.TrySetResult(null);
    }
}
