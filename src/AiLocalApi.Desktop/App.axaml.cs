using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AiLocalApi.Core;
namespace AiLocalApi.Desktop;
public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? lifetime;
    private Window? window;
    private ApiHost? api;
    private ApplicationService? service;
    private TrayIcon? tray;
    private bool exiting;
    private AppUpdates? updates;
    private readonly CancellationTokenSource updateStop = new();
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        _ = InitializeDesktopAsync();
        base.OnFrameworkInitializationCompleted();
    }
    private async Task InitializeDesktopAsync()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            lifetime = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = Program.Instance!.ListenAsync(command => Dispatcher.UIThread.Post(async () =>
            {
                if (command == 2) await ExitAsync();
                else if (command == 3) { if (updates is not null) await updates.CheckAndApplyAsync(); }
                else if (service is null) { desktop.MainWindow?.Show(); desktop.MainWindow?.Activate(); }
                else ShowWindow();
            }));
            try
            {
                var data = Distribution.ResolveData();
                if (data is null)
                {
                    var setup = new DataSetupWindow(); desktop.MainWindow = setup; setup.Show(); data = await setup.Selection;
                    if (data is null) { desktop.Shutdown(); return; }
                }
                var paths = new AppPaths(data); Distribution.PrepareData(paths); service = new(paths); api = new(service);
            }
            catch (Exception error)
            {
                desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
                desktop.MainWindow = new Window { Title = "AiLocalApi · 启动失败", Width = 640, Height = 240, Content = new TextBlock { Text = $"无法打开数据目录或数据库。请检查 AILOCAL_DATA 路径与权限，使用兼容的应用版本。\n\n{error.Message}", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(24) } };
                desktop.MainWindow.Show(); return;
            }
            var menu = new NativeMenu();
            var state = new NativeMenuItem("正在启动") { IsEnabled = false };
            api.Changed += () => Dispatcher.UIThread.Post(() => state.Header = api.Status);
            menu.Add(state);
            updates = new AppUpdates(service!, ExitAsync);
            updates.Changed += () => Dispatcher.UIThread.Post(() => state.Header = updates.Status);
            var update = new NativeMenuItem("检查并安全更新") { IsEnabled = Distribution.Packaged };
            update.Click += async (_, _) => await updates.CheckAndApplyAsync(); menu.Add(update);
            var open = new NativeMenuItem("打开管理窗口"); open.Click += (_, _) => ShowWindow(); menu.Add(open);
            var startup = new NativeMenuItem("登录时启动") { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = LoginStartup.Enabled };
            startup.Click += (_, _) => { try { LoginStartup.Set(startup.IsChecked); } catch (Exception error) { state.Header = error.Message; } }; menu.Add(startup);
            var pause = new NativeMenuItem("暂停接单") { ToggleType = NativeMenuItemToggleType.CheckBox };
            pause.Click += (_, _) => api.Paused = pause.IsChecked; menu.Add(pause);
            menu.Add(new NativeMenuItemSeparator());
            var quit = new NativeMenuItem("退出"); quit.Click += async (_, _) => await ExitAsync(); menu.Add(quit);
            tray = new TrayIcon { ToolTipText = "AiLocalApi · 本地语音转写", Menu = menu, Icon = new WindowIcon(Path.Combine(AppContext.BaseDirectory, "app.ico")), IsVisible = true };
            tray.Clicked += (_, _) => ShowWindow();
            TrayIcon.SetIcons(this, new TrayIcons { tray });
            desktop.ShutdownRequested += async (_, e) => { if (!exiting) { e.Cancel = true; await ExitAsync(); } };
            _ = StartAsync(state, desktop.Args?.Contains("--tray") == true);
            _ = updates.AutomaticAsync(updateStop.Token);
        }
    }
    private async Task StartAsync(NativeMenuItem state, bool trayOnly)
    {
        try { await api!.StartAsync(); state.Header = api.Status; }
        catch (Exception error) { state.Header = "启动失败：" + error.Message; }
        if (!trayOnly) ShowWindow();
    }
    private void ShowWindow()
    {
        if (window is null)
        {
            window = new MainWindow(new MainViewModel(service!, api!) { Updates = updates });
            window.Closing += (_, e) => { if (!exiting) { e.Cancel = true; window.Hide(); } };
            lifetime!.MainWindow = window;
        }
        window.Show(); window.WindowState = WindowState.Normal; window.Activate();
    }
    private async Task ExitAsync()
    {
        if (exiting) return;
        exiting = true;
        updateStop.Cancel();
        try
        {
            if (service is not null) service.Paused = true;
            try { if (api is not null) await api.DisposeAsync(); }
            finally { if (service is not null) await service.DisposeAsync(); }
        }
        finally { tray?.Dispose(); lifetime!.Shutdown(); }
    }
}
