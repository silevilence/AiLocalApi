using System.Diagnostics;
using System.IO.Pipes;
using AiLocalApi.Contracts;
using AiLocalApi.Worker;

if (args.Length != 2 || !int.TryParse(args[1], out var parentId)) return 2;
using var parent = Process.GetProcessById(parentId);
_ = Task.Run(async () => { await parent.WaitForExitAsync(); Environment.Exit(0); });
using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
await pipe.ConnectAsync(15000);
using var reader = new StreamReader(pipe);
using var writer = new StreamWriter(pipe) { AutoFlush = true };
using var engine = new InferenceEngine();
using var writeLock = new SemaphoreSlim(1);
async Task Reply(WorkerReply value) { await writeLock.WaitAsync(); try { await writer.WriteLineAsync(Wire.Encode(value)); } finally { writeLock.Release(); } }
Task running = Task.CompletedTask;
int busy = 0;
CancellationTokenSource? active = null;
string? activeId = null;
while (await reader.ReadLineAsync() is { } line)
{
    var request = Wire.Decode<WorkerRequest>(line);
    if (request.Command == "cancel") { if (activeId == request.Id) active?.Cancel(); continue; }
    if (request.Command == "exit") { active?.Cancel(); await running; engine.Unload(); await Reply(new(request.Id, "done")); break; }
    if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) { await Reply(new(request.Id, "error", Error: "Worker busy")); continue; }
    active?.Dispose(); active = new(); activeId = request.Id;
    var token = active.Token;
    running = Task.Run(async () =>
    {
    WorkerReply final;
    try
    {
        Task Progress(string stage) => Reply(new WorkerReply(request.Id, "progress", stage, Backend: engine.Backend, LoadCount: engine.LoadCount));
        Transcript? result = null;
        switch (request.Command)
        {
            case "ping": break;
            case "load": engine.Load(request.ModelPath!, request.Backend, request.Engine, request.ComputeType); break;
            case "transcribe":
                await Progress("loading"); engine.Load(request.ModelPath!, request.Backend, request.Engine, request.ComputeType);
                result = await engine.TranscribeAsync(request.AudioPath!, request.Options ?? new(), request.MaxDurationSeconds, Progress, token); break;
            case "unload": case "exit": engine.Unload(); break;
            default: throw new InvalidOperationException("Unknown Worker command.");
        }
        final = new WorkerReply(request.Id, "done", Result: result, Backend: engine.Backend, LoadCount: engine.LoadCount, BackendDetails: engine.BackendDetails, Engine: engine.Engine, ComputeType: engine.ComputeType);
    }
    catch (Exception error)
    {
        final = new WorkerReply(request.Id, "error", Error: error.Message, Backend: engine.Backend, LoadCount: engine.LoadCount);
    }
    // Completion is a protocol boundary: the host may send its next request as
    // soon as it reads the final reply, before this Task has transitioned to completed.
    Interlocked.Exchange(ref busy, 0);
    await Reply(final);
    });
}
active?.Cancel(); await running; active?.Dispose();
return 0;
