using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
namespace AiLocalApi.Desktop;
public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = new Window { Title = "AiLocalApi", Width = 960, Height = 680, Content = new TextBlock { Text = "尚未配置模型。请先选择模型目录。", Margin = new Thickness(32) } };
        base.OnFrameworkInitializationCompleted();
    }
}
