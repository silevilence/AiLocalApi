using System.IO.Pipes;
using System.Security.Principal;

namespace AiLocalApi.Desktop;

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly string pipeName = "AiLocalApi-activate-" + WindowsIdentity.GetCurrent().User!.Value;
    private readonly CancellationTokenSource stop = new();
    public bool IsPrimary { get; }
    public SingleInstance()
    {
        mutex = new Mutex(true, "Local\\" + pipeName, out var created);
        IsPrimary = created;
    }
    public async Task ActivateAsync(byte command = 1)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000);
        await pipe.WriteAsync(new byte[] { command });
    }
    public async Task ListenAsync(Action<byte> activate)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                var buffer = new byte[1];
                if (await pipe.ReadAsync(buffer, stop.Token) == 1) activate(buffer[0]);
            }
        }
        catch (OperationCanceledException) { }
    }
    public void Dispose() { stop.Cancel(); if (IsPrimary) mutex.ReleaseMutex(); mutex.Dispose(); stop.Dispose(); }
}
