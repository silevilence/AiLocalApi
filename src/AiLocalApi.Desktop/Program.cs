using Avalonia;
namespace AiLocalApi.Desktop;
internal static class Program
{
    internal static SingleInstance? Instance { get; private set; }
    [STAThread] public static void Main(string[] args)
    {
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(version => { Distribution.ResolveData(); LoginStartup.Set(false); }).Run();
        using var instance = new SingleInstance();
        if (!instance.IsPrimary) { instance.ActivateAsync(args.Contains("--exit") ? (byte)2 : args.Contains("--update") ? (byte)3 : (byte)1).GetAwaiter().GetResult(); return; }
        if (args.Contains("--exit") || args.Contains("--update")) return;
        Instance = instance;
        // This mostly static management window does not need a persistent GPU renderer.
        // Inference GPU selection remains independent in the Worker.
        AppBuilder.Configure<App>().UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software], CompositionMode = [Win32CompositionMode.RedirectionSurface] })
            .LogToTrace().StartWithClassicDesktopLifetime(args);
    }
}
