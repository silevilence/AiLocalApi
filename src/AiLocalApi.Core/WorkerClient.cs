using System.Diagnostics;
using System.IO.Pipes;
using AiLocalApi.Contracts;

namespace AiLocalApi.Core;

public sealed class WorkerClient : IAsyncDisposable
{
    private Process? process;
    private NamedPipeServerStream? pipe;
    private StreamReader? reader;
    private StreamWriter? writer;
    private readonly SemaphoreSlim gate = new(1);
    public int? ProcessId => process is { HasExited: false } ? process.Id : null;
    public string Backend { get; private set; } = "not loaded";
    public string BackendDetails { get; private set; } = "";
    public string Engine { get; private set; } = InferenceEngines.WhisperCpp;
    public string ComputeType { get; private set; } = "default";
    public async Task<WorkerReply> SendAsync(WorkerRequest request, Action<WorkerReply>? progress = null, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureStartedAsync(cancellationToken);
            await writer!.WriteLineAsync(Wire.Encode(request));
            using var finished = new CancellationTokenSource();
            var currentProcess = process; var currentWriter = writer;
            using var registration = cancellationToken.Register(() => _ = Task.Run(async () =>
            {
                try
                {
                    await currentWriter!.WriteLineAsync(Wire.Encode(new WorkerRequest(request.Id, "cancel")));
                    await Task.Delay(TimeSpan.FromSeconds(3), finished.Token);
                    if (currentProcess is { HasExited: false }) currentProcess.Kill(true);
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException) { }
            }));
            try
            {
            while (true)
            {
                var line = await reader!.ReadLineAsync() ?? throw new IOException("Worker disconnected; retry the job.");
                var reply = Wire.Decode<WorkerReply>(line);
                if (reply.Id != request.Id) throw new IOException("Worker correlation mismatch");
                if (reply.Backend is not null) Backend = reply.Backend;
                if (reply.BackendDetails is not null) BackendDetails = reply.BackendDetails;
                if (reply.Kind == "done") { Engine = reply.Engine; ComputeType = reply.ComputeType ?? "default"; }
                if (reply.Kind == "progress") { progress?.Invoke(reply); continue; }
                cancellationToken.ThrowIfCancellationRequested();
                if (reply.Kind == "error") throw new AppException("worker_error", reply.Error ?? "Worker failed", 422);
                return reply;
            }
            }
            finally { finished.Cancel(); }
        }
        finally { gate.Release(); }
    }
    private async Task EnsureStartedAsync(CancellationToken token)
    {
        if (process is { HasExited: false } && pipe is { IsConnected: true }) return;
        Reset();
        var name = $"AiLocalApi-worker-{Guid.NewGuid():N}";
        pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var adjacentWorker = Path.Combine(AppContext.BaseDirectory, "AiLocalApi.Worker.exe");
        var executable = Environment.GetEnvironmentVariable("AILOCAL_WORKER")
            ?? (File.Exists(adjacentWorker) ? adjacentWorker : Path.Combine(AppContext.BaseDirectory, "worker", "AiLocalApi.Worker.exe"));
        if (!File.Exists(executable)) throw new AppException("worker_missing", $"Worker not found: {executable}. Run scripts/build.ps1.", 503);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(name); start.ArgumentList.Add(Environment.ProcessId.ToString());
        process = Process.Start(start) ?? throw new IOException("Worker could not start");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await pipe.WaitForConnectionAsync(timeout.Token);
        reader = new(pipe, leaveOpen: true); writer = new(pipe, leaveOpen: true) { AutoFlush = true };
    }
    private void Reset()
    {
        if (process is { HasExited: false }) process.Kill(true);
        process?.Dispose(); process = null;
        try { writer?.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
        writer = null; reader?.Dispose(); reader = null; pipe?.Dispose(); pipe = null; Backend = "not loaded";
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (process is { HasExited: false } && writer is not null)
            {
                try
                {
                    await writer.WriteLineAsync(Wire.Encode(new WorkerRequest(Guid.NewGuid().ToString("N"), "exit")));
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
            }
            Reset();
        }
        finally { gate.Release(); }
    }
}
