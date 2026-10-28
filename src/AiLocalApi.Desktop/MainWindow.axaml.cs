using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
namespace AiLocalApi.Desktop;
public partial class MainWindow : Window
{
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    public MainWindow() { InitializeComponent(); }
    public MainWindow(MainViewModel model) : this()
    {
        DataContext = model;
        model.PickDirectory = async () => (await StorageProvider.OpenFolderPickerAsync(new() { Title = "选择模型下载目录", AllowMultiple = false })).FirstOrDefault()?.TryGetLocalPath();
        model.PickFile = async isModel => (await StorageProvider.OpenFilePickerAsync(new() { Title = isModel ? "导入已支持的 whisper.cpp 模型" : "选择转写音频", AllowMultiple = false, FileTypeFilter = [new(isModel ? "Whisper 模型" : "音频") { Patterns = isModel ? ["*.bin"] : ["*.wav", "*.mp3", "*.m4a", "*.flac", "*.ogg", "*.webm", "*.mp4"] }] })).FirstOrDefault()?.TryGetLocalPath();
        model.PickExport = async format => (await StorageProvider.SaveFilePickerAsync(new() { Title = "导出转写结果", SuggestedFileName = "transcript." + (format is "text" ? "txt" : format is "verbose_json" ? "json" : format), ShowOverwritePrompt = true }))?.TryGetLocalPath();
        model.Clipboard = text => Clipboard!.SetTextAsync(text);
        model.Confirm = async (title, message) =>
        {
            var dialog = new Window { Title = title, Width = 450, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var cancel = new Button { Content = "取消", IsCancel = true };
            var confirm = new Button { Content = "确认删除" };
            cancel.Click += (_, _) => dialog.Close(false); confirm.Click += (_, _) => dialog.Close(true);
            dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 20, Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { cancel, confirm } }
            } };
            dialog.Opened += (_, _) => cancel.Focus();
            return await dialog.ShowDialog<bool>(this);
        };
        refresh.Tick += async (_, _) => await model.RefreshAsync();
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) { if (IsVisible) { refresh.Start(); _ = model.RefreshAsync(); } else refresh.Stop(); } };
        Closed += (_, _) => refresh.Stop();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, async (_, e) => { var paths = e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).Where(path => path is not null).ToArray() ?? []; foreach (var path in paths) await model.AddFileAsync(path!); });
    }
}
