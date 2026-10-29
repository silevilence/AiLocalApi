using AiLocalApi.Contracts;
using AiLocalApi.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLocalApi.Desktop;

public partial class MainViewModel
{
    public string[] ModelEngines { get; } = ["全部引擎", InferenceEngines.WhisperCpp, InferenceEngines.CTranslate2];
    public string[] ModelTiers { get; } = ["全部档位", "tiny", "base", "small", "medium", "large-v1", "large-v2", "large-v3", "large-v3-turbo"];
    public string[] ModelLanguages { get; } = ["全部语言", "多语言", "仅英语"];
    public string[] ModelQuantizations { get; } = ["全部量化", "F16", "Q5_0", "Q5_1", "Q8_0"];
    [ObservableProperty] private string modelEngine = "全部引擎";
    [ObservableProperty] private string modelTier = "全部档位";
    [ObservableProperty] private string modelLanguage = "全部语言";
    [ObservableProperty] private string modelQuantization = "全部量化";
    [ObservableProperty] private bool hasModels = true;
    public string SelectedModelDetails => SelectedModel is { } selected && ModelManager.Catalog.SingleOrDefault(m => m.Id == selected.Id) is { } model
        ? $"{model.Id} · {model.Engine}\n来源：{model.Source}" : "选择一个模型查看来源及独立 API 模型 ID。";
    partial void OnModelEngineChanged(string value) => _ = RefreshAsync();
    partial void OnModelTierChanged(string value) => _ = RefreshAsync();
    partial void OnModelLanguageChanged(string value) => _ = RefreshAsync();
    partial void OnModelQuantizationChanged(string value) => _ = RefreshAsync();
    private bool MatchesFilter(CatalogModel model) =>
        (ModelEngine == "全部引擎" || model.Engine == ModelEngine) &&
        (ModelTier == "全部档位" || model.Tier.Replace(".en", "") == ModelTier) &&
        (ModelLanguage == "全部语言" || model.Multilingual == (ModelLanguage == "多语言")) &&
        (ModelQuantization == "全部量化" || model.Quantization == ModelQuantization);
    [RelayCommand] private void ResetModelFilters() { ModelEngine = "全部引擎"; ModelTier = "全部档位"; ModelLanguage = "全部语言"; ModelQuantization = "全部量化"; }
    [RelayCommand] private async Task ImportBundle() { var path = await PickDirectory!(); if (path is not null) await Run(() => Task.Run(() => service.Models.ImportAsync(path)), "模型包全部文件校验通过，已导入。"); }
    [RelayCommand] private async Task CopyModelId() { if (SelectedModel is { } model && Clipboard is not null) await Clipboard(model.Id); }

    public string[] CTranslateBackends { get; } = ["cpu", "auto", "cuda"];
    [ObservableProperty] private string cTranslateBackend = "cpu";
    [ObservableProperty] private string cTranslateComputeType = "default";
    [ObservableProperty] private string cTranslateTestResult = "使用 CTranslate2 C++ 接口，CPU 为基线；CUDA 需要单独构建的运行库和 NVIDIA 驱动。";
    public string[] CTranslateComputeTypes => CTranslateBackend == "cuda" ? ["default", "float32", "int8", "int8_float32", "float16", "int8_float16"] : ["default", "float32", "int8", "int8_float32"];
    partial void OnCTranslateBackendChanged(string value)
    {
        OnPropertyChanged(nameof(CTranslateComputeTypes));
        if (!CTranslateComputeTypes.Contains(CTranslateComputeType)) CTranslateComputeType = "default";
        CTranslateTestResult = "尚未测试当前 CTranslate2 后端与精度组合。";
    }
    partial void OnCTranslateComputeTypeChanged(string value) => CTranslateTestResult = "尚未测试当前 CTranslate2 后端与精度组合。";
    [RelayCommand(IncludeCancelCommand = true)] private async Task TestCTranslate(CancellationToken cancellationToken)
    {
        var backend = CTranslateBackend; var precision = CTranslateComputeType;
        CTranslateTestResult = "正在测试 CTranslate2 模型加载，最长 60 秒…";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try { CTranslateTestResult = await Task.Run(() => ConnectionDiagnostics.TestBackendAsync(service, backend, timeout.Token, InferenceEngines.CTranslate2, precision), timeout.Token); }
        catch (OperationCanceledException) { CTranslateTestResult = "测试已取消或超时。"; }
        catch (Exception error) { CTranslateTestResult = "CTranslate2 测试失败：" + error.Message; }
    }
}
