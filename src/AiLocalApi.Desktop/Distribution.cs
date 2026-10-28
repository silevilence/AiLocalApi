using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Velopack.Locators;
using AiLocalApi.Core;

namespace AiLocalApi.Desktop;

public static class Distribution
{
    public static bool Packaged
    {
        get { try { return VelopackLocator.Current.CurrentlyInstalledVersion is not null; } catch (InvalidOperationException) { return false; } }
    }
    public static bool Portable => Packaged && VelopackLocator.Current!.IsPortable;
    public static string Root => Packaged ? VelopackLocator.Current!.RootAppDir! : AppContext.BaseDirectory;
    public static string Mode => Packaged ? Portable ? "绿色版" : "安装版" : "开发运行";
    public static string Version => typeof(Distribution).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "development";
    public static string Identity => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(Root).ToUpperInvariant())))[..16];
    private static string ProfilePath => Path.Combine(Root, "instance.json");
    public sealed record Profile(string Id, string DataDirectory);
    public static Profile? ProfileValue { get; private set; }
    public static string StartupId => ProfileValue?.Id ?? Identity;
    public static string StartupExecutable => Packaged ? Path.Combine(Root, "AiLocalApi.exe") : Environment.ProcessPath!;
    public static string? ResolveData()
    {
        if (Environment.GetEnvironmentVariable("AILOCAL_DATA") is { Length: > 0 } explicitPath) return Path.GetFullPath(explicitPath);
        if (!Packaged) return AppPaths.Default.Data;
        if (File.Exists(ProfilePath)) ProfileValue = JsonSerializer.Deserialize<Profile>(File.ReadAllText(ProfilePath)) ?? throw new IOException("发行配置损坏。");
        if (ProfileValue is null && Portable) SaveData(Path.Combine(Root, "Data"));
        return ProfileValue is null ? null : Path.GetFullPath(ProfileValue.DataDirectory, Root);
    }
    public static void SaveData(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (!Portable && (full.Equals(Root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("安装版数据目录必须位于程序安装目录之外，卸载后才能保留。");
        Directory.CreateDirectory(full);
        using (new FileStream(Path.Combine(full, $".write-{Guid.NewGuid():N}"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        ProfileValue = new(ProfileValue?.Id ?? Guid.NewGuid().ToString("N"), Portable ? Path.GetRelativePath(Root, full) : full);
        File.WriteAllText(ProfilePath, JsonSerializer.Serialize(ProfileValue));
    }
    public static void PrepareData(AppPaths paths)
    {
        paths.Initialize();
        if (!Packaged) return;
        var marker = Path.Combine(paths.Data, "location.json");
        var previous = File.Exists(marker) ? JsonSerializer.Deserialize<string>(File.ReadAllText(marker)) : null;
        var store = new StateStore(paths);
        if (Portable && previous is not null && !previous.Equals(paths.Data, StringComparison.OrdinalIgnoreCase)) store.RelocatePaths(previous, paths.Data);
        if (Portable && store.Settings.ModelDirectory is null) store.SaveSettings(store.Settings with { ModelDirectory = Path.Combine(paths.Data, "models") });
        File.WriteAllText(marker, JsonSerializer.Serialize(paths.Data));
        LoginStartup.RepairIfEnabled();
    }
}
