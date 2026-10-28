using AiLocalApi.Contracts;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace AiLocalApi.Worker;

internal sealed class InferenceEngine : IDisposable
{
    private WhisperFactory? factory;
    private string? loadedPath;
    private string? requestedBackend;
    private readonly List<string> diagnostics = [];
    private readonly IDisposable logger;
    public InferenceEngine() => logger = LogProvider.AddLogger((_, line) => { if (line is not null && (line.Contains("ggml_vulkan:") || line.Contains("ggml_cuda:") || line.Contains("using device"))) lock (diagnostics) { if (diagnostics.Count < 32) diagnostics.Add(line.Trim()); } });
    public string BackendDetails { get { lock (diagnostics) return string.Join("\n", diagnostics); } }
    public int LoadCount { get; private set; }
    public string Backend => RuntimeOptions.LoadedLibrary?.ToString().ToLowerInvariant() ?? "not loaded";
    public void Load(string path, string backend)
    {
        if (factory is not null && path == loadedPath && backend == requestedBackend) return;
        if (requestedBackend is not null && requestedBackend != backend) throw new InvalidOperationException("Backend changed; restart Worker first.");
        Unload(); requestedBackend = backend;
        RuntimeOptions.RuntimeLibraryOrder = backend switch
        {
            "cpu" => [RuntimeLibrary.Cpu], "vulkan" => [RuntimeLibrary.Vulkan],
            "cuda" => [RuntimeLibrary.Cuda], "auto" => [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu],
            _ => throw new ArgumentException("Unsupported backend")
        };
        try
        {
            factory = WhisperFactory.FromPath(path, new WhisperFactoryOptions { UseGpu = backend != "cpu" });
            _ = factory.CreateBuilder(); // Validate that native context creation actually succeeded.
            if (RuntimeOptions.LoadedLibrary is RuntimeLibrary.Vulkan or RuntimeLibrary.Cuda)
            {
                var devices = NativeDevices.Gpus();
                if (devices.Length == 0) throw new InvalidOperationException("未检测到可用 GPU；不会将 CPU 计算显示为 GPU 推理。");
                lock (diagnostics) diagnostics.Add("GPU devices: " + string.Join(", ", devices));
            }
        }
        catch (Exception error) { throw new InvalidOperationException($"无法初始化 {backend} 后端。CPU 需要 MSVC CRT/OpenMP；Vulkan 需要显卡驱动；CUDA 需要匹配的 NVIDIA 驱动及启用 CUDA 的构建。{error.Message}", error); }
        loadedPath = path; LoadCount++;
    }
    public async Task<Transcript> TranscribeAsync(string audio, TranscriptionOptions options, int maxSeconds, Func<string, Task> progress, CancellationToken token)
    {
        if (factory is null) throw new InvalidOperationException("Model is not loaded.");
        var alignment = options.Words ? new WordAlignment(loadedPath!) : null;
        var pcm = audio + ".pcm";
        try
        {
            await progress("decoding");
            var duration = AudioDecoder.Decode(audio, pcm, maxSeconds, token);
            await progress("transcribing");
            var builder = factory.CreateBuilder().WithTemperature(options.Temperature).WithThreads(Math.Max(1, Math.Min(8, Environment.ProcessorCount - 1)));
            if (string.IsNullOrWhiteSpace(options.Language)) builder.WithLanguageDetection(); else builder.WithLanguage(options.Language);
            if (!string.IsNullOrWhiteSpace(options.Prompt)) builder.WithPrompt(options.Prompt);
            if (options.Translate) builder.WithTranslate();
            if (options.Words) builder.WithTokenTimestamps();
            using var processor = builder.Build();
            var segments = new List<Segment>(); var words = new List<Word>(); string? language = options.Language;
            await using var file = File.OpenRead(pcm);
            var bytes = new byte[16000 * 30 * sizeof(float)];
            long totalSamples = 0;
            while (true)
            {
                var read = 0;
                while (read < bytes.Length) { var count = await file.ReadAsync(bytes.AsMemory(read)); if (count == 0) break; read += count; }
                if (read == 0) break;
                var samples = new float[read / sizeof(float)]; Buffer.BlockCopy(bytes, 0, samples, 0, read);
                var offset = totalSamples / 16000d;
                await foreach (var segment in processor.ProcessAsync(samples, token))
                {
                    language = segment.Language;
                    segments.Add(new(segment.Text, offset + segment.Start.TotalSeconds, Math.Min(duration, offset + segment.End.TotalSeconds)));
                    if (alignment is not null) words.AddRange(alignment.Align(segment, offset, duration));
                }
                totalSamples += samples.Length;
            }
            return new(string.Concat(segments.Select(segment => segment.Text)).Trim(), segments.ToArray(), words.ToArray(), duration, language);
        }
        finally { if (File.Exists(pcm)) File.Delete(pcm); }
    }
    public void Unload() { factory?.Dispose(); factory = null; loadedPath = null; }
    public void Dispose() { Unload(); logger.Dispose(); }
}
