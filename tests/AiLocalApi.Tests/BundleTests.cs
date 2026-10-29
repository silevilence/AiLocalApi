using System.Security.Cryptography;
using AiLocalApi.Contracts;
using AiLocalApi.Core;
using Xunit;

namespace AiLocalApi.Tests;

public class BundleTests
{
    [Theory]
    [InlineData("ct2-tiny")]
    [InlineData("tiny")]
    public async Task DiskFullFailsBeforeNetworkAndLeavesModelUnavailable(string id)
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "AiLocalApi-tests", Guid.NewGuid().ToString("N")));
        var store = new StateStore(paths); var manager = new ModelManager(store, _ => 0);
        manager.SetDirectory(Path.Combine(paths.Data, "models"));
        var error = await Assert.ThrowsAsync<AppException>(() => manager.DownloadAsync(id));
        Assert.Equal("disk_full", error.Code);
        Assert.Equal("failed", manager.Progress[id].State);
        Assert.Empty(manager.Installed); Assert.Null(store.Model(id));
    }
    [Fact]
    public async Task BundleVerificationRejectsMissingAndSameSizeCorruptFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), "AiLocalApi-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        byte[] content = [1, 2, 3];
        var file = new ModelFile("model.bin", content.Length, Convert.ToHexStringLower(SHA256.HashData(content)));
        var model = new CatalogModel("ct2-test", 3, file.Sha256, true, Engine: InferenceEngines.CTranslate2, Files: [file]);
        await Assert.ThrowsAsync<AppException>(() => ModelManager.VerifyAsync(path, model, default));
        await File.WriteAllBytesAsync(Path.Combine(path, file.Name), content);
        await ModelManager.VerifyAsync(path, model, default);
        await File.WriteAllBytesAsync(Path.Combine(path, file.Name), [3, 2, 1]);
        await Assert.ThrowsAsync<AppException>(() => ModelManager.VerifyAsync(path, model, default));
    }

    [Fact]
    public void PartialBundleIsNotInstalledAndExtraFilesPreventDeletion()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "AiLocalApi-tests", Guid.NewGuid().ToString("N")));
        var store = new StateStore(paths); var manager = new ModelManager(store);
        var model = ModelManager.Catalog.Single(m => m.Id == "ct2-tiny");
        var directory = Path.Combine(paths.Data, model.Id); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "user-notes.txt"), "keep");
        store.SaveModel(new(model.Id, directory, model.Sha256, model.Size, true, model.Quantization, model.Source, model.Engine));
        Assert.Empty(manager.Installed);
        Assert.Equal("model_extra_files", Assert.Throws<AppException>(() => manager.Delete(model.Id)).Code);
        Assert.True(File.Exists(Path.Combine(directory, "user-notes.txt")));
        Assert.NotNull(store.Model(model.Id));
    }

    [Fact]
    public void CTranslateSettingsRejectVulkanAndCpuHalfPrecision()
    {
        var store = new StateStore(new(Path.Combine(Path.GetTempPath(), "AiLocalApi-tests", Guid.NewGuid().ToString("N"))));
        Assert.Equal("cpu", store.Settings.CTranslateBackend);
        Assert.Throws<AppException>(() => store.SaveSettings(store.Settings with { CTranslateBackend = "vulkan" }));
        Assert.Throws<AppException>(() => store.SaveSettings(store.Settings with { CTranslateComputeType = "float16" }));
        store.SaveSettings(store.Settings with { CTranslateComputeType = "int8" });
        Assert.Equal("int8", store.Settings.CTranslateComputeType);
    }
}
