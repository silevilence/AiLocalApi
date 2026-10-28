using AiLocalApi.Contracts;
using AiLocalApi.Core;
using Xunit;
namespace AiLocalApi.Tests;
public class BehaviorTests
{
    [Theory]
    [InlineData(0, 8, 100000, "empty_audio")]
    [InlineData(9, 8, 100000, "upload_too_large")]
    [InlineData(1, 8, 16, "temporary_limit")]
    public async Task RejectedUploadHonorsLimitsWithoutLeavingFiles(int length, long uploadLimit, long temporaryLimit, string code)
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "AiLocalApi-tests", Guid.NewGuid().ToString("N")));
        await using var service = new ApplicationService(paths);
        // Admission only needs an installed file; none of these requests may reach inference.
        var model = Path.Combine(paths.Data, "fixture.bin"); await File.WriteAllBytesAsync(model, [1]);
        service.Store.SaveModel(new("tiny", model, "test-only", 1, true, "F16", "test"));
        service.Store.SaveSettings(service.Store.Settings with { MaxUploadBytes = uploadLimit, MaxTemporaryBytes = temporaryLimit, MaxDurationSeconds = 1 });
        var error = await Assert.ThrowsAsync<AppException>(() => service.SubmitAsync(new MemoryStream(new byte[length]), "audio.wav", "tiny", new()));
        Assert.Equal(code, error.Code); Assert.Empty(service.Store.Jobs()); Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Temporary));
        Assert.Null(service.Worker.ProcessId);
    }
    [Fact] public void SubtitleHoursDoNotWrapAndFormatsEscapeJson()
    {
        var transcript = new Transcript("你好\"test", [new("你好\"test", 3661.234, 3662.567)], [], 3663, "zh");
        Assert.Contains("01:01:01,234 --> 01:01:02,567", TranscriptFormats.Format(transcript, "srt").Content);
        Assert.StartsWith("WEBVTT\n\n01:01:01.234", TranscriptFormats.Format(transcript, "vtt").Content);
        using var json = System.Text.Json.JsonDocument.Parse(TranscriptFormats.Format(transcript, "json").Content);
        Assert.Equal(transcript.Text, json.RootElement.GetProperty("text").GetString());
        Assert.Throws<AppException>(() => TranscriptFormats.Format(transcript, "xml"));
    }
    [Fact] public async Task InvalidInputLeavesNoJobOrUpload()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "AiLocalApi-tests", Guid.NewGuid().ToString("N")));
        await using var service = new ApplicationService(paths);
        await Assert.ThrowsAsync<AppException>(() => service.SubmitAsync(new MemoryStream([1]), "file.wav", "missing", new()));
        await Assert.ThrowsAsync<AppException>(() => service.SubmitAsync(new MemoryStream([1]), "file.exe", "tiny", new()));
        Assert.Empty(service.Store.Jobs()); Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Temporary));
    }
    [Fact] public async Task UnloadIsIdempotentAndNoDefaultModelDownload()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "AiLocalApi-tests", Guid.NewGuid().ToString("N")));
        await using var service = new ApplicationService(paths);
        Assert.Null(service.Store.Settings.ModelDirectory);
        await Assert.ThrowsAsync<AppException>(() => service.Models.DownloadAsync("tiny"));
        await service.Worker.DisposeAsync(); await service.Worker.DisposeAsync();
        Assert.Null(service.Worker.ProcessId);
    }
    [Fact] public void ConcurrentClaimsCannotExecuteOneJobTwice()
    {
        var store = new StateStore(new(Path.Combine(Path.GetTempPath(), "AiLocalApi-tests", Guid.NewGuid().ToString("N"))));
        var now = DateTimeOffset.UtcNow;
        store.AddJob(new("one", "tiny", "input.wav", new(), "queued", "queued", now, now), 100);
        var claims = new System.Collections.Concurrent.ConcurrentBag<Job>();
        Parallel.For(0, 20, _ => { if (store.Claim() is { } job) claims.Add(job); });
        Assert.Single(claims);
        store.Change("one", job => job with { State = "cancelled" });
        Assert.Null(store.Change("one", job => job.State == "running" ? job with { State = "succeeded" } : null));
        Assert.Equal("cancelled", store.Job("one")!.State);
    }
}
