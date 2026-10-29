using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiLocalApi.Contracts;
using AiLocalApi.Core;

namespace AiLocalApi.Verify;

internal static class MultilingualRegression
{
    public static async Task RunAsync(ApplicationService service, string directory, string modelId = "tiny")
    {
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "multilingual.json")));
        var languages = new HashSet<string>();
        foreach (var sample in manifest.RootElement.EnumerateArray())
        {
            var language = sample.GetProperty("language").GetString()!; languages.Add(language);
            var audio = Path.Combine(directory, sample.GetProperty("file").GetString()!);
            await using (var file = File.OpenRead(audio))
                Require(Convert.ToHexString(await SHA256.HashDataAsync(file)).Equals(sample.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "fixture checksum");
            foreach (var automatic in new[] { false, true })
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var job = await service.SubmitFileAsync(audio, modelId, new(Language: automatic ? null : language, Words: true));
                var completed = await service.WaitAsync(job.Id, timeout.Token);
                Require(completed.State == "succeeded" && completed.Result is not null, $"{language} inference: {completed.Error}");
                var result = completed.Result!;
                Require(result.Duration > 0 && result.Segments.Length > 0 && result.Words.Length > 0, "duration and timestamps");
                var pattern = language switch { "zh" => @"\p{IsCJKUnifiedIdeographs}", "ja" => @"[\p{IsHiragana}\p{IsKatakana}]", "ko" => @"\p{IsHangulSyllables}", _ => "[a-zA-Z]" };
                Require(Regex.IsMatch(result.Text, pattern), $"{language} script: {result.Text}");
                Require(result.Language == language, $"expected {language}, got {result.Language}");
                foreach (var segment in result.Segments) Require(segment.Start >= 0 && segment.End >= segment.Start && segment.End <= result.Duration + .02, "segment bounds");
                foreach (var word in result.Words) Require(word.Start >= 0 && word.End >= word.Start && word.End <= result.Duration + .02 && !word.Text.Contains('\uFFFD'), "Unicode word and bounds");
                ValidateAlignment(result);
                if (modelId.StartsWith("ct2-", StringComparison.Ordinal) && language == "zh")
                    Require(result.Words[0].Start is > 4.5 and < 6, "FLEURS Chinese leading pause must not be attached to first word");
                var reference = sample.GetProperty("reference").GetString()!;
                Console.WriteLine(Wire.Encode(new { modelId, language, automatic, result.Text, reference, result.Duration, segments = result.Segments.Length, words = result.Words.Length, characterErrorRate = CharacterErrorRate(reference, result.Text), elapsedSeconds = watch.Elapsed.TotalSeconds, alignment = result.Words }));
            }
        }
        Require(languages.SetEquals(["zh", "ja", "en", "ko"]), "all four languages are required");
        Console.WriteLine("PASS: Chinese, Japanese, English, Korean; explicit language and automatic detection; Unicode words and timestamp bounds. CER is diagnostic, not an accuracy guarantee.");
    }
    public static void ValidateAlignment(Transcript result)
    {
        Require(result.Words.Zip(result.Words.Skip(1)).All(pair => pair.First.Start <= pair.Second.Start), "word order across windows");
        Require(result.Segments.Zip(result.Segments.Skip(1)).All(pair => pair.First.Start <= pair.Second.Start), "segment order across windows");
        static string Letters(string text) => string.Concat(text.Where(char.IsLetterOrDigit));
        Require(Letters(string.Concat(result.Words.Select(word => word.Text))) == Letters(result.Text), "word text must correspond to transcript");
    }
    private static double CharacterErrorRate(string reference, string actual)
    {
        static char[] Normalize(string text) => text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
        var expected = Normalize(reference); var observed = Normalize(actual); var previous = Enumerable.Range(0, observed.Length + 1).ToArray();
        for (var i = 1; i <= expected.Length; i++)
        {
            var current = new int[observed.Length + 1]; current[0] = i;
            for (var j = 1; j <= observed.Length; j++) current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + (expected[i - 1] == observed[j - 1] ? 0 : 1));
            previous = current;
        }
        return Math.Round((double)previous[^1] / Math.Max(1, expected.Length), 4);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception("Multilingual regression failed: " + message); }
}
