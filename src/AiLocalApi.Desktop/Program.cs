using Avalonia;
namespace AiLocalApi.Desktop;
internal static class Program
{
    internal static SingleInstance? Instance { get; private set; }
    [STAThread] public static void Main(string[] args)
    {
        using var instance = new SingleInstance();
        if (!instance.IsPrimary) { instance.ActivateAsync(args.Contains("--exit") ? (byte)2 : (byte)1).GetAwaiter().GetResult(); return; }
        if (args.Contains("--exit")) return;
        Instance = instance;
        // This mostly static management window does not need a persistent GPU renderer.
        // Inference GPU selection remains independent in the Worker.
        AppBuilder.Configure<App>().UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software], CompositionMode = [Win32CompositionMode.RedirectionSurface] })
            .LogToTrace().StartWithClassicDesktopLifetime(args);
    }
}
