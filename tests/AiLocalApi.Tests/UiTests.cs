using AiLocalApi.Core;
using AiLocalApi.Desktop;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(AiLocalApi.Tests.TestAppBuilder))]

namespace AiLocalApi.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class UiTests
{
    [AvaloniaFact] public async Task ModelFiltersAndPrecisionChoicesExcludeInvalidCombinations()
    {
        await using var service = new ApplicationService(Paths()); await using var host = new ApiHost(service);
        var model = new MainViewModel(service, host); await model.RefreshAsync();
        model.ModelEngine = "ctranslate2";
        for (var i = 0; i < 100 && model.Models.Any(m => !m.Id.StartsWith("ct2-")); i++) { await Task.Delay(10); await model.RefreshAsync(); }
        Assert.Equal(12, model.Models.Count);
        model.ModelQuantization = "Q5_0";
        for (var i = 0; i < 100 && model.HasModels; i++) { await Task.Delay(10); await model.RefreshAsync(); }
        Assert.False(model.HasModels); Assert.False(model.CanDownload);
        model.CTranslateBackend = "cuda"; model.CTranslateComputeType = "float16";
        model.CTranslateBackend = "cpu";
        Assert.Equal("default", model.CTranslateComputeType);
        Assert.DoesNotContain("float16", model.CTranslateComputeTypes);
        Assert.DoesNotContain("vulkan", model.CTranslateBackends);
    }
    private static AppPaths Paths() => new(Path.Combine(Path.GetTempPath(), "AiLocalApi-tests", Guid.NewGuid().ToString("N")));

    [AvaloniaTheory]
    [InlineData(1080, 760)]
    [InlineData(820, 620)]
    public async Task SettingsAndKeyPagesReserveScrollbarSpace(double width, double height)
    {
        await using var service = new ApplicationService(Paths());
        await using var host = new ApiHost(service);
        var model = new MainViewModel(service, host); await model.RefreshAsync();
        var window = new MainWindow(model) { Width = width, Height = height }; window.Show();
        try
        {
            var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
            for (var page = 0; page < 4; page++)
            {
                if (page == 3)
                {
                    model.KeyName = "桌面客户端"; await model.CreateKeyCommand.ExecuteAsync(null); model.HideSecretCommand.Execute(null);
                    model.KeyName = "自动化脚本"; await model.CreateKeyCommand.ExecuteAsync(null);
                }
                tabs.SelectedIndex = page; window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                if (page >= 2)
                {
                    var scroll = window.FindControl<ScrollViewer>(page == 2 ? "SettingsScroll" : "KeysScroll")!;
                    var bar = scroll.GetVisualDescendants().OfType<ScrollBar>().First(item => item.Orientation == Orientation.Vertical && ReferenceEquals(item.TemplatedParent, scroll));
                    Assert.False(scroll.AllowAutoHide);
                    var barLeft = bar.IsVisible ? bar.TranslatePoint(default, window)!.Value.X : scroll.TranslatePoint(new Point(scroll.Bounds.Width, 0), window)!.Value.X;
                    if (scroll.Extent.Height > scroll.Viewport.Height) Assert.True(bar.Bounds.Width > 0);
                    foreach (var input in scroll.GetVisualDescendants().OfType<Control>().Where(item => item is TextBox or ComboBox or NumericUpDown or Button))
                    {
                        if (!input.IsEffectivelyVisible || input.Bounds.Width == 0 || input.GetVisualAncestors().OfType<ScrollBar>().Any()) continue;
                        var right = input.TranslatePoint(new Point(input.Bounds.Width, 0), window)!.Value.X;
                        Assert.True(right <= barLeft - 8, $"{page}: {input.GetType().Name} overlaps scrollbar ({right} > {barLeft}).");
                    }
                    Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1);
                }
                var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ux-results")); Directory.CreateDirectory(directory);
                using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); frame.Save(Path.Combine(directory, $"page-{page}-{width}.png"));
                if (page >= 2)
                {
                    var scroll = window.FindControl<ScrollViewer>(page == 2 ? "SettingsScroll" : "KeysScroll")!;
                    scroll.Offset = new Vector(0, scroll.Extent.Height); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
                    using var bottom = window.CaptureRenderedFrame(); bottom!.Save(Path.Combine(directory, $"page-{page}-{width}-bottom.png"));
                }
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact] public async Task KeyCommandsAndTaskStatesExposeUsableActions()
    {
        await using var service = new ApplicationService(Paths()); await using var host = new ApiHost(service);
        var model = new MainViewModel(service, host); await model.RefreshAsync();
        Assert.True(model.AllowAnonymous); Assert.False(model.HasJobs); Assert.False(model.CanExport); Assert.False(model.CanCancelJob);
        Assert.DoesNotContain("Authorization", model.ApiExample);
        model.KeyName = "UI 测试"; await model.CreateKeyCommand.ExecuteAsync(null);
        Assert.True(model.HasSecret); Assert.Single(model.Keys); Assert.NotNull(model.SelectedKey);
        var secret = model.CreatedSecret; model.HideSecretCommand.Execute(null); Assert.False(model.HasSecret);
        model.AllowAnonymous = false; await model.PolicyChange; Assert.False(service.Store.Authorize("")); Assert.Contains("Authorization", model.ApiExample);
        await model.ToggleKeyCommand.ExecuteAsync(null); Assert.False(service.Store.Authorize("Bearer " + secret));
        await model.ToggleKeyCommand.ExecuteAsync(null); Assert.True(service.Store.Authorize("Bearer " + secret));
        model.Confirm = (_, _) => Task.FromResult(false); await model.DeleteKeyCommand.ExecuteAsync(null); Assert.Single(model.Keys);
        model.Confirm = (_, _) => Task.FromResult(true); await model.DeleteKeyCommand.ExecuteAsync(null); Assert.Empty(model.Keys);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 50; i++) service.Store.AddJob(new($"history-{i}", "tiny", "", new(), "failed", "failed", now, now), 100);
        await model.RefreshAsync(); Assert.True(model.HasJobs); Assert.False(model.CanNext);
        model.SelectedJob = model.Jobs[0]; Assert.True(model.CanRetryJob); Assert.False(model.CanExport);
        service.Store.Change(model.SelectedJob.Id, job => job with { State = "succeeded", Result = new("完成", [], [], 1, "zh") });
        await model.RefreshAsync(); Assert.True(model.CanExport); Assert.False(model.CanRetryJob);
    }
}
