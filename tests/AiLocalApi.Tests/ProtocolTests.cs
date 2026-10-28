using AiLocalApi.Contracts;
using Xunit;
namespace AiLocalApi.Tests;
public class ProtocolTests
{
    [Fact] public async Task WorkerStartsAndAnswersOverUserPipe()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, $"../../../../../src/AiLocalApi.Worker/bin/{configuration}/net10.0-windows/AiLocalApi.Worker.exe"));
        Environment.SetEnvironmentVariable("AILOCAL_WORKER", path);
        await using var worker = new AiLocalApi.Core.WorkerClient();
        var reply = await worker.SendAsync(new("smoke", "ping"));
        Assert.Equal("done", reply.Kind);
        Assert.Equal("smoke", reply.Id);
        // A final reply promises readiness even while the worker's Task unwinds.
        for (var i = 0; i < 500; i++)
        {
            var id = $"consecutive-{i}";
            var next = await worker.SendAsync(new(id, "ping"));
            Assert.Equal("done", next.Kind); Assert.Equal(id, next.Id);
        }
    }
    [Fact] public void RequestPreservesUnicodeAndCorrelation()
    {
        var request = new WorkerRequest("job-123", "transcribe", "D:\\模型\\tiny.bin", Options: new("zh", "专有名词"));
        Assert.Equal(request, Wire.Decode<WorkerRequest>(Wire.Encode(request)));
    }
}
