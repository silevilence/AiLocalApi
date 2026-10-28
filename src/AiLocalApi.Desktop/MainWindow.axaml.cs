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
        refresh.Tick += async (_, _) => await model.RefreshAsync();
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) { if (IsVisible) { refresh.Start(); _ = model.RefreshAsync(); } else refresh.Stop(); } };
        Closed += (_, _) => refresh.Stop();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, async (_, e) => { var paths = e.DataTransfer.TryGetFiles()?.Select(file => file.TryGetLocalPath()).Where(path => path is not null).ToArray() ?? []; foreach (var path in paths) await model.AddFileAsync(path!); });
    }
}
