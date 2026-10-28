using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using AiLocalApi.Core;
using Microsoft.Win32;

namespace AiLocalApi.Verify;

internal static class ReleaseRegression
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS: " + message); }
    // root, data, models, audio, version, mode, optional nextVersion and update source
    public static async Task RunAsync(string[] args)
    {
        if (Environment.UserName != "WDAGUtilityAccount") throw new Exception("Release acceptance requires Windows Sandbox.");
        var root = Path.GetFullPath(args[1]); var data = Path.GetFullPath(args[2]);
        var executable = Path.Combine(root, "current", "AiLocalApi.Desktop.exe");
        var store = new StateStore(new(data)); var models = new ModelManager(store);
        if (!args.Contains("--existing")) { models.SetDirectory(Path.Combine(data, "models")); await models.ImportAsync(Path.Combine(args[3], "ggml-tiny.bin")); }
        store.SaveSettings(store.Settings with { Port = 17842, DefaultModel = "tiny", IdleSeconds = 2, Backend = "cpu" });
        var profile = Path.Combine(root, "instance.json");
        var firstRun = !File.Exists(profile);
        if (firstRun) await File.WriteAllTextAsync(profile, JsonSerializer.Serialize(new { Id = Guid.NewGuid().ToString("N"), DataDirectory = args[6] == "portable" ? "Data" : data }));
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(profile));
        var startup = "AiLocalApi-" + config.RootElement.GetProperty("Id").GetString();
        using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (firstRun) Check(run.GetValue(startup) is null, "startup is opt-in");
        var launch = new ProcessStartInfo(executable, "--tray") { UseShellExecute = false, CreateNoWindow = true };
        launch.Environment.Remove("AILOCAL_DATA"); launch.Environment["AILOCAL_DISABLE_UPDATE_CHECK"] = "1";
        using var interrupted = args.Contains("--interrupt-feed") ? new InterruptedUpdateSource(args[8]) : null;
        if (args.Length > 8 && !args[8].StartsWith("--")) launch.Environment["AILOCAL_UPDATE_SOURCE"] = interrupted?.Url ?? args[8];
        using var process = Process.Start(launch)!;
        using var http = new HttpClient { BaseAddress = new("http://localhost:17842"), Timeout = TimeSpan.FromSeconds(20) };
        var mode = args[6] == "portable" ? "绿色版" : "安装版";
        try
        {
            Check((await Version(http, args[5])).GetProperty("mode").GetString() == mode, "mode=" + mode);
            Check(File.Exists(Path.Combine(root, "AiLocalApi.exe")), "stable launch entry");
            if (args.Contains("--existing"))
            {
                Check(store.Model("tiny")!.Path == Path.Combine(data, "models", "ggml-tiny.bin"), "existing model relocated without reimport");
                Check((string?)run.GetValue(startup) == $"\"{Path.Combine(root, "AiLocalApi.exe")}\" --tray", "moved startup entry repaired");
                Check(store.Jobs().All(job => job.Input.StartsWith(data, StringComparison.OrdinalIgnoreCase)), "historical input paths relocated");
            }
            var first = await Submit(http, args[4]); await Job(http, first);
            Check(!string.IsNullOrWhiteSpace(await http.GetStringAsync($"/api/jobs/{first}/result?format=text")), "real transcription before update");
            using (var duplicate = Process.Start(launch)!) { await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Check(duplicate.ExitCode == 0, "single instance activation"); }
            if (args.Length > 7 && args[7] == "failure")
            {
                var before = DateTime.UtcNow;
                using (var trigger = Process.Start(new ProcessStartInfo(executable, "--update") { UseShellExecute = false, CreateNoWindow = true })!) await trigger.WaitForExitAsync();
                var failed = false;
                for (var i = 0; i < 240; i++)
                {
                    failed = Directory.GetFiles(Path.Combine(data, "logs"), "*.log").Any(file => File.ReadLines(file).Any(line => line.Contains("更新失败") && DateTimeOffset.TryParse(line.Split(' ')[0], out var timestamp) && timestamp.UtcDateTime >= before));
                    if (failed) break; await Task.Delay(500);
                }
                Check(failed, "update failure is diagnosed");
                if (interrupted is not null) Check(interrupted.InterruptedDownloads > 0, "connection interrupted during package transfer");
                Check((await Version(http, args[5])).GetProperty("accepting").GetBoolean(), "failed update preserves version and accepting API");
                Check(store.Job(first)!.Result is not null && File.Exists(store.Model("tiny")!.Path), "failure preserves data and model");
                await Job(http, await Submit(http, args[4]));
            }
            else if (args.Length > 7 && !args[7].StartsWith("--"))
            {
                run.SetValue(startup, "\"T:\\stale\\AiLocalApi.exe\" --tray");
                string? active = null, queued = null;
                if (!args.Contains("--idle-update"))
                {
                    var longAudio = Path.Combine(data, "update-long.wav"); LongAudio(args[4], longAudio);
                    active = await Submit(http, longAudio); queued = await Submit(http, args[4]);
                    for (var i = 0; i < 100 && store.Job(active)?.State != "running"; i++) await Task.Delay(100);
                    Check(store.Job(active)?.State == "running", "transcription running when update requested");
                }
                else Check(store.Jobs().All(job => job.State is not ("queued" or "running")), "idle when update requested");
                using (var trigger = Process.Start(new ProcessStartInfo(executable, "--update") { UseShellExecute = false, CreateNoWindow = true })!) await trigger.WaitForExitAsync();
                Check((await Version(http, args[7])).GetProperty("mode").GetString() == mode, "mode preserved after update");
                if (active is not null && queued is not null)
                {
                    await Job(http, active); await Job(http, queued);
                    Check(store.Job(active)!.Attempts == 1, "active job completes without restart");
                }
                Check(store.Job(first)!.Result is not null, "previous result preserved");
                Check(store.Settings.Port == 17842 && File.Exists(store.Model("tiny")!.Path), "settings and model preserved");
                Check((string?)run.GetValue(startup) == $"\"{Path.Combine(root, "AiLocalApi.exe")}\" --tray", "startup repaired to stable path");
                await Job(http, await Submit(http, args[4]));
            }
            Console.WriteLine($"RESULT root={root}; data={data}; startup={startup}");
        }
        finally
        {
            using var quit = Process.Start(new ProcessStartInfo(executable, "--exit") { UseShellExecute = false, CreateNoWindow = true });
            if (quit is not null) await quit.WaitForExitAsync();
            var stopped = false;
            for (var i = 0; i < 100; i++) { try { await http.GetAsync("/health"); await Task.Delay(100); } catch (HttpRequestException) { stopped = true; break; } }
            Check(stopped, "graceful exit releases API port");
        }
    }
    private static async Task<JsonElement> Version(HttpClient http, string version)
    {
        for (var i = 0; i < 1800; i++)
        {
            try { var state = await http.GetFromJsonAsync<JsonElement>("/health"); if (state.GetProperty("version").GetString() == version) { Console.WriteLine("PASS: API version=" + version); return state; } }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException) { }
            await Task.Delay(500);
        }
        throw new Exception("API did not reach version " + version);
    }
    private static async Task<string> Submit(HttpClient http, string audio)
    {
        using var form = new MultipartFormDataContent(); form.Add(new StringContent("whisper-1"), "model"); form.Add(new StreamContent(File.OpenRead(audio)), "file", Path.GetFileName(audio));
        using var response = await http.PostAsync("/api/jobs", form); response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }
    private static async Task Job(HttpClient http, string id)
    {
        for (var i = 0; i < 1800; i++)
        {
            var job = await http.GetFromJsonAsync<JsonElement>($"/api/jobs/{id}"); var state = job.GetProperty("state").GetString();
            if (state == "succeeded") { Console.WriteLine("PASS: persisted job " + id); return; }
            if (state is not ("queued" or "running")) throw new Exception(job.ToString());
            await Task.Delay(500);
        }
        throw new Exception("Job timed out: " + id);
    }
    private static void LongAudio(string input, string output)
    {
        using var reader = new BinaryReader(File.OpenRead(input)); reader.BaseStream.Position = 12; byte[]? pcm = null;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length) { var id = new string(reader.ReadChars(4)); var size = reader.ReadInt32(); if (id == "data") { pcm = reader.ReadBytes(size); break; } reader.BaseStream.Position += size + (size & 1); }
        if (pcm is null) throw new Exception("No PCM data");
        using var writer = new BinaryWriter(File.Create(output)); const int repeats = 60; var length = pcm.Length * repeats;
        writer.Write("RIFF"u8); writer.Write(36 + length); writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(length);
        for (var i = 0; i < repeats; i++) writer.Write(pcm);
    }
}
