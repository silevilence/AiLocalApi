using System.Diagnostics;
using AiLocalApi.Contracts;
using AiLocalApi.Core;
using AiLocalApi.Verify;

var reportIndex = Array.IndexOf(args, "--report");
if (reportIndex >= 0)
{
    var report = Path.GetFullPath(args[reportIndex + 1]); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
    var output = new StreamWriter(report) { AutoFlush = true }; Console.SetOut(output); Console.SetError(output);
    AppDomain.CurrentDomain.UnhandledException += (_, e) => { Console.Error.WriteLine(e.ExceptionObject); output.Flush(); };
}
if (args.Length >= 4 && args[0] == "--http") { await HttpRegression.RunAsync(args[1], args[2], args[3], args.Contains("--lock")); return 0; }
if (args.Length >= 1 && args[0] == "--sandbox-drive")
{
    if (Environment.UserName != "WDAGUtilityAccount") throw new Exception("Drive fixture is restricted to Windows Sandbox.");
    Directory.CreateDirectory("C:\\TestDrive"); var map = new ProcessStartInfo("subst.exe") { UseShellExecute = false, CreateNoWindow = true }; map.ArgumentList.Add("T:"); map.ArgumentList.Add("C:\\TestDrive"); using var process = Process.Start(map)!; await process.WaitForExitAsync(); if (process.ExitCode != 0) throw new Exception("Failed to map test drive"); Console.WriteLine("Mapped test drive T:"); return 0;
}
if (args.Length == 3 && args[0] == "--fixtures") { AudioFixtures.Generate(args[1], args[2]); return 0; }
if (args.Length == 2 && args[0] == "--seed") { Regression.Seed(args[1]); return 0; }
if (args.Length < 3) { Console.Error.WriteLine("Usage: Verify <data-dir> <model-dir> <audio-file> [cpu|vulkan|cuda] [--download] [--suite]"); return 2; }
var paths = new AppPaths(Path.GetFullPath(args[0]));
await using var service = new ApplicationService(paths);
service.Models.SetDirectory(args.Contains("--copy-model") ? Path.Combine(paths.Data, "models") : Path.GetFullPath(args[1]));
if (Environment.GetEnvironmentVariable("AILOCAL_WORKER") is null) Environment.SetEnvironmentVariable("AILOCAL_WORKER", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../app/worker/AiLocalApi.Worker.exe")));
service.Store.SaveSettings(service.Store.Settings with { Backend = args.ElementAtOrDefault(3) is "cpu" or "vulkan" or "cuda" or "auto" ? args[3] : "cpu", IdleSeconds = 2 });
var clock = Stopwatch.StartNew();
if (args.Contains("--download"))
{
    var downloading = service.Models.DownloadAsync("tiny");
    while (!downloading.IsCompleted) { Console.WriteLine(Wire.Encode(service.Models.Progress.Values)); await Task.Delay(1000); }
    await downloading;
}
else await service.Models.ImportAsync(Path.Combine(args[1], "ggml-tiny.bin"));
Console.WriteLine($"Model verified in {clock.Elapsed.TotalSeconds:F1}s");
var job = await service.SubmitFileAsync(Path.GetFullPath(args[2]), "tiny", new(Words: true));
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
var result = await service.WaitAsync(job.Id, timeout.Token);
if (result.State != "succeeded" || string.IsNullOrWhiteSpace(result.Result?.Text)) throw new Exception(Wire.Encode(result));
Console.WriteLine(Wire.Encode(new { result.State, result.Result, service.ModelState, backend = service.Worker.Backend, service.Worker.BackendDetails, service.LoadCount, elapsed = clock.Elapsed.TotalSeconds, worker = service.Worker.ProcessId, workerMemory = service.Worker.ProcessId is { } pid ? Process.GetProcessById(pid).WorkingSet64 : 0 }));
var firstCount = service.LoadCount;
var second = await service.SubmitFileAsync(Path.GetFullPath(args[2]), "tiny", new());
if ((await service.WaitAsync(second.Id, timeout.Token)).State != "succeeded" || service.LoadCount != firstCount) throw new Exception("Model reuse failed");
for (var i = 0; i < 100 && (service.Worker.ProcessId is not null || service.LoadedModel is not null); i++) await Task.Delay(100);
if (service.Worker.ProcessId is not null || service.LoadedModel is not null) throw new Exception($"Idle unload failed: state={service.ModelState}, process={service.Worker.ProcessId}, ttl={service.Store.Settings.IdleSeconds}, error={service.LastError}");
Console.WriteLine("PASS: inference, model reuse, idle unload and Worker release");
if (args.Contains("--suite")) await Regression.RunAsync(service, Path.GetFullPath(args[2]));
if (args.Contains("--models")) await Regression.ModelsAsync(service, Path.GetFullPath(args[2]));
return 0;
