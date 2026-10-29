namespace AiLocalApi.Contracts;

public static class InferenceEngines
{
    public const string WhisperCpp = "whisper.cpp";
    public const string CTranslate2 = "ctranslate2";

    public static bool SupportsBackend(string engine, string backend) => engine switch
    {
        WhisperCpp => backend is "cpu" or "auto" or "vulkan" or "cuda",
        CTranslate2 => backend is "cpu" or "auto" or "cuda",
        _ => false
    };

    public static bool SupportsComputeType(string engine, string backend, string computeType) => engine switch
    {
        WhisperCpp => computeType == "default",
        CTranslate2 => computeType is "default" or "float32" or "int8" or "int8_float32" ||
            backend == "cuda" && computeType is "float16" or "int8_float16",
        _ => false
    };
}
