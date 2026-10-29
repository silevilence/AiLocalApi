using AiLocalApi.Contracts;

namespace AiLocalApi.Worker;

internal sealed class CTranslateInferenceEngine : IDisposable
{
    private CTranslateNative? native;
    private WhisperTokenizer? tokenizer;
    private WhisperFeatures? features;
    private (string Path, string Backend, string Precision)? loaded;
    public string Backend { get; private set; } = "not loaded";
    public string ComputeType => native?.ComputeType ?? "not loaded";
    public string BackendDetails => $"CTranslate2 4.6.0 / {Backend} / {ComputeType}";
    public int LoadCount { get; private set; }
    public void Load(string path, string backend, string precision)
    {
        if (native is not null && loaded == (path, backend, precision)) return;
        Unload();
        // Auto fallback is handled in the host and is therefore visible to the user.
        var selected = backend == "auto" ? "cuda" : backend;
        try
        {
            native = new(path, selected, precision);
            tokenizer = new(Path.Combine(path, "tokenizer.json"));
            features = new(native.MelCount);
            Backend = selected; loaded = (path, backend, precision); LoadCount++;
        }
        catch { Unload(); throw; }
    }
    public async Task<Transcript> TranscribeAsync(string audio, TranscriptionOptions options, int maxSeconds, Func<string, Task> progress, CancellationToken token)
    {
        if (native is null || tokenizer is null || features is null) throw new InvalidOperationException("Model not loaded.");
        var pcm = audio + ".pcm";
        try
        {
            await progress("decoding");
            var duration = AudioDecoder.Decode(audio, pcm, maxSeconds, token);
            await progress("transcribing");
            await using var input = File.OpenRead(pcm);
            var segments = new List<Segment>(); var words = new List<Word>();
            string? language = options.Language;
            var promptText = string.IsNullOrWhiteSpace(options.Prompt) ? [] : tokenizer.Encode(" " + options.Prompt.Trim()).TakeLast(180).ToArray();
            var bytes = new byte[480000 * sizeof(float)];
            long position = 0; var total = input.Length / sizeof(float);
            while (position < total)
            {
                token.ThrowIfCancellationRequested();
                input.Position = position * sizeof(float);
                var count = (int)Math.Min(bytes.Length, input.Length - input.Position);
                await input.ReadExactlyAsync(bytes.AsMemory(0, count), token);
                var samples = new float[count / sizeof(float)]; Buffer.BlockCopy(bytes, 0, samples, 0, count);
                // Digital silence needs no model output and must not manufacture a transcript.
                if (samples.All(value => Math.Abs(value) < 1e-7)) { position += samples.Length; continue; }
                native.Encode(features.Extract(samples, token)); token.ThrowIfCancellationRequested();
                language ??= native.DetectLanguage();
                var start = new List<int> { tokenizer.Id("<|startoftranscript|>") };
                if (native.Multilingual) { start.Add(tokenizer.Id($"<|{language}|>")); start.Add(tokenizer.Id(options.Translate ? "<|translate|>" : "<|transcribe|>")); }
                var prompt = new List<int>();
                if (promptText.Length > 0) { prompt.Add(tokenizer.Id("<|startofprev|>")); prompt.AddRange(promptText); }
                prompt.AddRange(start);
                var generated = native.Generate(prompt.ToArray(), options.Temperature); token.ThrowIfCancellationRequested();
                if (generated.NoSpeech > 0.6f && generated.Score < -1f) { position += samples.Length; continue; }
                var generatedTokens = generated.Tokens;
                var singleTimestampEnding = generatedTokens.Length >= 2 && generatedTokens[^2] < tokenizer.TimestampBegin && generatedTokens[^1] >= tokenizer.TimestampBegin;
                var lastSplit = 0;
                for (var i = 1; i < generatedTokens.Length; i++)
                    if (generatedTokens[i - 1] >= tokenizer.TimestampBegin && generatedTokens[i] >= tokenizer.TimestampBegin) lastSplit = i;
                var partialWindow = lastSplit > 0 && !singleTimestampEnding;
                // Consecutive timestamps delimit completed segments. Replay the unfinished tail
                // from its timestamp instead of dropping it at a fixed 30-second boundary.
                var completedTokens = partialWindow ? generatedTokens[..lastSplit] : generatedTokens;
                var windowDuration = samples.Length / 16000d; var offset = position / 16000d;
                var firstSegment = segments.Count;
                var buffer = new List<int>(); var textTokens = new List<int>();
                double segmentStart = 0, lastTimestamp = 0;
                foreach (var id in completedTokens)
                {
                    if (id >= tokenizer.TimestampBegin)
                    {
                        var time = Math.Clamp((id - tokenizer.TimestampBegin) * 0.02, 0, windowDuration);
                        if (buffer.Count > 0)
                        {
                            var text = tokenizer.Decode(buffer);
                            if (!string.IsNullOrWhiteSpace(text)) { segments.Add(new(text, offset + segmentStart, offset + Math.Max(segmentStart, time))); textTokens.AddRange(buffer); }
                            buffer.Clear();
                        }
                        segmentStart = Math.Max(segmentStart, time); lastTimestamp = segmentStart;
                    }
                    else if (id < tokenizer.End) buffer.Add(id);
                }
                // A truncated decoder window is retried from the last completed timestamp.
                var truncated = generated.Tokens.Length >= 448 - prompt.Count;
                if (buffer.Count > 0 && !truncated && !partialWindow)
                {
                    var text = tokenizer.Decode(buffer);
                    if (!string.IsNullOrWhiteSpace(text)) { segments.Add(new(text, offset + segmentStart, offset + windowDuration)); textTokens.AddRange(buffer); }
                }
                if (options.Words && textTokens.Count > 0)
                {
                    var alignment = native.Align(start.ToArray(), textTokens.ToArray(), Math.Clamp((int)Math.Ceiling(samples.Length / 160d), 1, 3000));
                    token.ThrowIfCancellationRequested();
                    var spans = new List<(int Start, int End, long TimeStart, long TimeEnd)>(); var byteOffset = 0;
                    for (var i = 0; i < textTokens.Count; i++)
                    {
                        var begin = alignment.FirstOrDefault(pair => pair.Text == i, (Text: -1, Time: -1));
                        var end = alignment.FirstOrDefault(pair => pair.Text == i + 1, (Text: -1, Time: -1));
                        if (begin.Text < 0 || end.Text < 0) throw new InvalidDataException("Incomplete native word alignment.");
                        var length = tokenizer.Bytes(textTokens[i]).Length;
                        spans.Add((byteOffset, byteOffset + length, begin.Time * 2L, end.Time * 2L)); byteOffset += length;
                    }
                    var aligned = WordAlignment.AlignBytes(tokenizer.Decode(textTokens), spans, offset, duration, language);
                    // DTW starts at frame zero, which can attach a leading pause to the
                    // first word. Retain its native end and bound only this outlier start
                    // using the other aligned word durations; this is not a VAD estimate.
                    var lengths = aligned.Select(word => word.End - word.Start).Where(length => length > 0).Order().ToArray();
                    if (lengths.Length > 1)
                    {
                        var median = Math.Min(0.7, (lengths[(lengths.Length - 1) / 2] + lengths[lengths.Length / 2]) / 2);
                        if (aligned[0].End - aligned[0].Start > Math.Max(1, median * 4))
                        {
                            aligned[0] = aligned[0] with { Start = Math.Round(aligned[0].End - median * 2, 2) };
                            if (firstSegment < segments.Count && aligned[0].Start < segments[firstSegment].End)
                                segments[firstSegment] = segments[firstSegment] with { Start = Math.Max(segments[firstSegment].Start, aligned[0].Start) };
                        }
                    }
                    words.AddRange(aligned);
                }
                if ((truncated || partialWindow) && lastTimestamp <= 0) throw new InvalidDataException("Whisper did not advance past a completed timestamp.");
                position += truncated || partialWindow ? Math.Clamp((long)(lastTimestamp * 16000), 1, samples.Length) : samples.Length;
            }
            return new(string.Concat(segments.Select(s => s.Text)).Trim(), segments.ToArray(), words.ToArray(), duration, language ?? options.Language);
        }
        finally { if (File.Exists(pcm)) File.Delete(pcm); }
    }
    public void Unload() { native?.Dispose(); native = null; tokenizer = null; features = null; loaded = null; Backend = "not loaded"; }
    public void Dispose() => Unload();
}
