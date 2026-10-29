using AiLocalApi.Contracts;
using AiLocalApi.Core;
using Xunit;

namespace AiLocalApi.Tests;

public class EngineContractTests
{
    [Fact]
    public async Task AliasBindsModelAndEngineBeforeDefaultChanges()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "AiLocalApi-tests", Guid.NewGuid().ToString("N")));
        await using var service = new ApplicationService(paths);
        var file = Path.Combine(paths.Data, "fixture.bin"); await File.WriteAllBytesAsync(file, [1]);
        foreach (var id in new[] { "tiny", "base" }) service.Store.SaveModel(new(id, file, "test", 1, true, "F16", "test"));
        service.Store.SaveSettings(service.Store.Settings with { DefaultModel = "tiny" });
        var job = await service.SubmitAsync(new MemoryStream([1]), "audio.wav", "whisper-1", new());
        service.Store.SaveSettings(service.Store.Settings with { DefaultModel = "base" });
        var persisted = service.Get(job.Id);
        Assert.Equal("tiny", persisted.Model); Assert.Equal(InferenceEngines.WhisperCpp, persisted.Engine);
    }
    [Theory]
    [InlineData("tiny", true, "yue", false)]
    [InlineData("ct2-large-v2", true, "yue", false)]
    [InlineData("ct2-large-v3", true, "yue", true)]
    [InlineData("large-v3-turbo-q5_0", true, "yue", true)]
    [InlineData("tiny.en", false, "zh", false)]
    [InlineData("ct2-tiny", true, "zh", true)]
    public void LanguageCapabilitiesRespectModelVocabulary(string id, bool multilingual, string language, bool supported)
        => Assert.Equal(supported, new ModelInfo(id, "", "", 0, multilingual, "F16", "").SupportsLanguage(language));

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
