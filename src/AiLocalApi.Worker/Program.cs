using System.Diagnostics;
using System.IO.Pipes;
using AiLocalApi.Contracts;

if (args.Length != 2 || !int.TryParse(args[1], out var parentId)) return 2;
using var parent = Process.GetProcessById(parentId);
_ = Task.Run(async () => { await parent.WaitForExitAsync(); Environment.Exit(0); });
using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
await pipe.ConnectAsync(15000);
using var reader = new StreamReader(pipe);
using var writer = new StreamWriter(pipe) { AutoFlush = true };
while (await reader.ReadLineAsync() is { } line)
{
    var request = Wire.Decode<WorkerRequest>(line);
    await writer.WriteLineAsync(Wire.Encode(new WorkerReply(request.Id, request.Command == "ping" ? "done" : "error", Error: request.Command == "ping" ? null : "No model configured")));
    if (request.Command == "exit") break;
}
return 0;
