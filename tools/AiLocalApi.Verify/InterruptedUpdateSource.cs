using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AiLocalApi.Verify;

// Real TCP truncation, distinct from an unreachable source or a complete but corrupt package.
internal sealed class InterruptedUpdateSource : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task server;
    private int interrupted;
    public int InterruptedDownloads => Volatile.Read(ref interrupted);
    public string Url { get; }

    public InterruptedUpdateSource(string feed)
    {
        var manifest = File.ReadAllText(Path.Combine(feed, "releases.win.json")).Replace("0.1.1", "0.1.2", StringComparison.Ordinal);
        var package = Directory.GetFiles(feed, "*-full.nupkg").Single();
        listener.Start(); Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
        server = ServeAsync(manifest, package);
    }

    private async Task ServeAsync(string manifest, string package)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                var request = await reader.ReadLineAsync(stop.Token) ?? "";
                while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 }) { }
                var isPackage = request.Contains(".nupkg", StringComparison.OrdinalIgnoreCase);
                var body = isPackage ? new byte[4096] : Encoding.UTF8.GetBytes(manifest);
                if (isPackage) { using var file = File.OpenRead(package); await file.ReadExactlyAsync(body, stop.Token); }
                var length = isPackage ? new FileInfo(package).Length : body.Length;
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n"), stop.Token);
                await stream.WriteAsync(body, stop.Token); await stream.FlushAsync(stop.Token);
                if (isPackage)
                {
                    await Task.Delay(100, stop.Token);
                    Interlocked.Increment(ref interrupted);
                    client.Client.LingerState = new LingerOption(true, 0);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        stop.Cancel(); listener.Stop(); server.GetAwaiter().GetResult(); stop.Dispose();
    }
}
