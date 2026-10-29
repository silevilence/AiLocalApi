using AiLocalApi.Contracts;
using AiLocalApi.Core;
using Xunit;

namespace AiLocalApi.Tests;

public class ModelCatalogTests
{
    [Fact]
    public void CatalogHasPinnedDistinctArtifactsAndCorrectCapabilities()
    {
        Assert.Equal(33, ModelManager.Catalog.Count(m => !m.IsBundle));
        Assert.Equal(ModelManager.Catalog.Length, ModelManager.Catalog.Select(m => m.Id).Distinct().Count());
        foreach (var model in ModelManager.Catalog)
        {
            Assert.Matches("^[a-f0-9]{64}$", model.Sha256);
            Assert.True(model.Size > 1_000_000);
            Assert.DoesNotContain("/main/", model.Source);
            Assert.Equal(!model.Id.Contains(".en"), model.Multilingual);
            Assert.Equal(model.Multilingual && !model.Id.Contains("turbo"), model.SupportsTranslation);
            if (model.IsBundle)
            {
                Assert.Contains(model.Files!, f => f.Name == "model.bin");
                Assert.Contains(model.Files!, f => f.Name == "tokenizer.json");
                Assert.Contains(model.Files!, f => f.Name == "config.json");
                Assert.Contains(model.Files!, f => f.Name is "vocabulary.txt" or "vocabulary.json");
                Assert.Equal(model.Sha256, model.Files!.Single(f => f.Name == "model.bin").Sha256);
                Assert.Matches("^[a-f0-9]{40}$", model.Revision!);
                Assert.All(model.Files!, file => { Assert.True(file.Size > 0); Assert.Matches("^[a-f0-9]{64}$", file.Sha256); });
                Assert.Equal(model.Size, model.Files!.Sum(f => f.Size));
            }
        }
        Assert.Contains(ModelManager.Catalog, m => m.Id == "large-v3-turbo-q5_0");
        Assert.Contains(ModelManager.Catalog, m => m.Id == "medium.en-q8_0");
    }

    [Theory]
    [InlineData("large-v3-turbo", "model_translation", null, true)]
    [InlineData("large-v3-turbo-q5_0", "model_translation", null, true)]
    [InlineData("medium.en", "model_language", null, true)]
    [InlineData("tiny", "model_language", "yue", false)]
    public async Task UnsupportedRequestIsRejectedBeforeUpload(string id, string code, string? language, bool translate)
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "AiLocalApi-tests", Guid.NewGuid().ToString("N")));
        await using var service = new ApplicationService(paths);
        var catalog = ModelManager.Catalog.Single(m => m.Id == id);
        var file = Path.Combine(paths.Data, "fixture.bin"); await File.WriteAllBytesAsync(file, [1]);
        service.Store.SaveModel(new(id, file, "test-only", 1, catalog.Multilingual, catalog.Quantization, "test"));
        var error = await Assert.ThrowsAsync<AppException>(() => service.SubmitAsync(new MemoryStream([1]), "audio.wav", id, new(Language: language, Translate: translate)));
        Assert.Equal(code, error.Code);
        Assert.Empty(service.Store.Jobs()); Assert.Null(service.Worker.ProcessId);
    }
}
