using AiLocalApi.Core;
using Xunit;

namespace AiLocalApi.Tests;

public class ReleaseTests
{
    [Fact]
    public void RelocationPreservesExternalModelsAndCompletedResults()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiLocalApi-tests", Guid.NewGuid().ToString("N"));
        var previous = Path.Combine(root, "原目录"); var current = Path.Combine(root, "移动后 data");
        var store = new StateStore(new(current));
        store.SaveSettings(new(ModelDirectory: Path.Combine(previous, "models"), Port: 18349));
        store.SaveModel(new("tiny", Path.Combine(previous, "models", "tiny.bin"), "hash", 1, true, "F16", "test"));
        var outside = Path.Combine(root, "原目录-other", "base.bin");
        store.SaveModel(new("base", outside, "hash", 1, true, "F16", "test"));
        var now = DateTimeOffset.UtcNow;
        store.AddJob(new("one", "tiny", Path.Combine(previous, "jobs", "one.wav"), new(), "succeeded", "completed", now, now), 10);
        store.RelocatePaths(previous, current);
        Assert.Equal(Path.Combine(current, "models"), store.Settings.ModelDirectory);
        Assert.Equal(18349, store.Settings.Port);
        Assert.Equal(Path.Combine(current, "models", "tiny.bin"), store.Model("tiny")!.Path);
        Assert.Equal(outside, store.Model("base")!.Path);
        Assert.Equal("succeeded", store.Job("one")!.State);
        Assert.Equal(Path.Combine(current, "jobs", "one.wav"), store.Job("one")!.Input);
        Assert.Single(Directory.GetFiles(current, "*.move-*.bak"));
    }
    [Fact]
    public async Task UpdateDrainKeepsQueuedJobsUnclaimedAndCannotBeUnpausedByUi()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "AiLocalApi-tests", Guid.NewGuid().ToString("N")));
        await using var service = new ApplicationService(paths);
        await service.DrainForUpdateAsync();
        var now = DateTimeOffset.UtcNow;
        service.Store.AddJob(new("queued", "tiny", "input.wav", new(), "queued", "queued", now, now), 10);
        service.Paused = false;
        await Task.Delay(1200);
        Assert.True(service.Paused);
        Assert.Equal("queued", service.Get("queued").State);
        Assert.Equal(0, service.Get("queued").Attempts);
        Assert.Null(service.Worker.ProcessId);
        service.Cancel("queued"); service.ResumeAfterUpdateFailure();
        Assert.False(service.Paused);
    }
}
