using AiLocalApi.Contracts;
using AiLocalApi.Core;
using Xunit;

namespace AiLocalApi.Tests;

public class EngineContractTests
{
    [Fact]
    public void OldPersistedRecordsAndRequestsDefaultToWhisperCpp()
    {
        var model = Wire.Decode<ModelInfo>("""{"id":"tiny","path":"model.bin","sha256":"abc","size":1,"multilingual":true,"quantization":"F16","source":"import"}""");
        Assert.Equal(InferenceEngines.WhisperCpp, model.Engine);
        Assert.Equal(InferenceEngines.WhisperCpp, Wire.Decode<WorkerRequest>("""{"id":"test","command":"load"}""").Engine);
        var job = Wire.Decode<Job>("""{"id":"old","model":"tiny","input":"a.wav","options":{},"state":"queued","stage":"queued","created":"2026-01-01T00:00:00Z","updated":"2026-01-01T00:00:00Z"}""");
        Assert.Equal(InferenceEngines.WhisperCpp, job.Engine);
    }

    [Theory]
    [InlineData("whisper.cpp", "vulkan", "default", true)]
    [InlineData("ctranslate2", "vulkan", "default", false)]
    [InlineData("ctranslate2", "cpu", "int8", true)]
    [InlineData("ctranslate2", "cpu", "float16", false)]
    [InlineData("ctranslate2", "cuda", "float16", true)]
    [InlineData("missing", "cpu", "default", false)]
    public void DeviceAndPrecisionCapabilitiesAreEngineSpecific(string engine, string backend, string precision, bool supported)
        => Assert.Equal(supported, InferenceEngines.SupportsBackend(engine, backend) && InferenceEngines.SupportsComputeType(engine, backend, precision));
}
