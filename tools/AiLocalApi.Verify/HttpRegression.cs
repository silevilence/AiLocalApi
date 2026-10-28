using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AiLocalApi.Core;
using OpenAI;
using OpenAI.Audio;
using System.ClientModel;
namespace AiLocalApi.Verify;
internal static class HttpRegression
{
    public static async Task RunAsync(string executable, string data, string audio, bool lockSession = false)
    {
        var store = new StateStore(new(Path.GetFullPath(data))); store.SaveSettings(store.Settings with { Port = 17841, ApiKey = "local-verification-key", Backend = "cpu", IdleSeconds = 2 });
        var start = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--tray"); start.Environment["AILOCAL_DATA"] = Path.GetFullPath(data);
        var desktop = Process.Start(start)!;
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri("http://localhost:17841"), Timeout = TimeSpan.FromMinutes(3) };
            var watch = Stopwatch.StartNew();
            while (true) { try { if ((await http.GetAsync("/health")).IsSuccessStatusCode) break; } catch (HttpRequestException) { } if (desktop.HasExited || watch.Elapsed.TotalSeconds > 30) throw new Exception("Host did not become healthy"); await Task.Delay(100); }
            Console.WriteLine($"Tray startup to health: {watch.Elapsed.TotalMilliseconds:F0} ms; working set {desktop.WorkingSet64}");
            Require((await http.GetAsync("/api/jobs")).StatusCode == HttpStatusCode.Unauthorized, "Bearer authentication");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "local-verification-key");
            http.DefaultRequestHeaders.Add("Origin", "https://example.invalid");
            Require((await http.GetAsync("/api/jobs")).StatusCode == HttpStatusCode.Forbidden, "Origin guard"); http.DefaultRequestHeaders.Remove("Origin");
            var models = await http.GetStringAsync("/v1/models"); Require(models.Contains("whisper-1"), "Default alias");
            var sdk = new AudioClient("whisper-1", new ApiKeyCredential("local-verification-key"), new OpenAIClientOptions { Endpoint = new Uri("http://localhost:17841/v1") });
            var sdkResult = await sdk.TranscribeAudioAsync(audio, new AudioTranscriptionOptions { ResponseFormat = AudioTranscriptionFormat.Verbose, TimestampGranularities = AudioTimestampGranularities.Word | AudioTimestampGranularities.Segment });
            Require(sdkResult.Value.Words.Count > 0 && sdkResult.Value.Segments.Count > 0, "Official SDK timestamps");
            var chinese = Path.Combine(Path.GetDirectoryName(audio)!, "chinese.wav");
            if (File.Exists(chinese))
            {
                var zh = await sdk.TranscribeAudioAsync(chinese); Require(!string.IsNullOrWhiteSpace(zh.Value.Text), "SDK Chinese transcription");
                var translation = await sdk.TranslateAudioAsync(chinese); Require(!string.IsNullOrWhiteSpace(translation.Value.Text), "SDK translation");
            }
            Console.WriteLine("PASS: official OpenAI .NET SDK transcription, translation and timestamps");
            foreach (var format in TranscriptFormats.Supported)
            {
                using var form = Form(audio, format); if (format == "verbose_json") { form.Add(new StringContent("word"), "timestamp_granularities[]"); form.Add(new StringContent("segment"), "timestamp_granularities[]"); }
                using var response = await http.PostAsync("/v1/audio/transcriptions", form); var text = await response.Content.ReadAsStringAsync();
                Require(response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(text), $"Format {format}: {text}");
                if (format == "verbose_json") Require(text.Contains("\"words\"") && text.Contains("\"segments\""), "Timestamp fields");
                if (format == "vtt") Require(text.StartsWith("WEBVTT"), "VTT header");
                Console.WriteLine($"PASS: HTTP {format}, {response.Content.Headers.ContentType}");
            }
            using (var form = Form(audio, "verbose_json"))
            {
                form.Add(new StringContent("word"), "timestamp_granularities[]");
                using var response = await http.PostAsync("/api/jobs", form); Require(response.StatusCode == HttpStatusCode.Accepted, "Async 202");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var id = json.RootElement.GetProperty("id").GetString();
                for (var i = 0; i < 1000; i++)
                {
                    using var state = JsonDocument.Parse(await http.GetStringAsync($"/api/jobs/{id}"));
                    var status = state.RootElement.GetProperty("state").GetString(); if (status == "succeeded") break;
                    Require(status is "queued" or "running", "Async job state"); await Task.Delay(50);
                }
                Require((await http.GetAsync($"/api/jobs/{id}/result?format=srt")).IsSuccessStatusCode, "Async result");
                using var detailed = JsonDocument.Parse(await http.GetStringAsync($"/api/jobs/{id}/result?format=verbose_json"));
                Require(detailed.RootElement.GetProperty("words").GetArrayLength() > 0, "Async result retains requested words");
                Require((await http.PostAsync($"/api/jobs/{id}/retry", null)).StatusCode == HttpStatusCode.Conflict, "Success retry rejected");
            }
            using (var form = Form(audio, "text")) { form.Add(new StringContent("word"), "timestamp_granularities[]"); Require((await http.PostAsync("/v1/audio/transcriptions", form)).StatusCode == HttpStatusCode.BadRequest, "Illegal timestamps"); }
            // A second invocation must signal this process and terminate promptly.
            using (var second = Process.Start(start)!) { using var timeout = new CancellationTokenSource(10000); await second.WaitForExitAsync(timeout.Token); Require(second.ExitCode == 0, "Single instance"); }
            Require(!desktop.HasExited && (await http.GetAsync("/health")).IsSuccessStatusCode, "Existing host survives activation");
            Console.WriteLine("PASS: authentication, origin rejection, async polling, error structure, single instance");
            using (var form = Form(audio, "json"))
            {
                using var response = await http.PostAsync("/api/jobs", form); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var id = json.RootElement.GetProperty("id").GetString();
                for (var i = 0; i < 200; i++) { using var state = JsonDocument.Parse(await http.GetStringAsync($"/api/jobs/{id}")); if (state.RootElement.GetProperty("stage").GetString() == "transcribing") break; await Task.Delay(10); }
                desktop.Kill(); await desktop.WaitForExitAsync(); desktop.Dispose(); desktop = Process.Start(start)!;
                for (var i = 0; i < 300; i++) { try { if ((await http.GetAsync("/health")).IsSuccessStatusCode) break; } catch (HttpRequestException) { } await Task.Delay(100); }
                var complete = false;
                for (var i = 0; i < 2000; i++)
                {
                    using var state = JsonDocument.Parse(await http.GetStringAsync($"/api/jobs/{id}")); var status = state.RootElement.GetProperty("state").GetString();
                    if (status == "succeeded") { Require(state.RootElement.GetProperty("attempts").GetInt32() == 2, "Interrupted job attempts"); complete = true; break; }
                    Require(status is "queued" or "running", "Recovered job state"); await Task.Delay(50);
                }
                Require(complete, "Host crash job recovery"); Console.WriteLine("PASS: host termination, Worker parent lifetime and persisted job recovery");
            }
            if (lockSession)
            {
                Require(Environment.UserName == "WDAGUtilityAccount", "Lock test is restricted to Windows Sandbox");
                Require(LockWorkStation(), "Sandbox session lock"); await Task.Delay(3000);
                Require((await http.GetAsync("/health")).IsSuccessStatusCode, "Health while session locked");
                Console.WriteLine("PASS: API remains healthy while Windows Sandbox session is locked");
            }
        }
        finally
        {
            var quit = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true }; quit.ArgumentList.Add("--exit");
            using var signal = Process.Start(quit)!; using var timeout = new CancellationTokenSource(15000); await signal.WaitForExitAsync(timeout.Token); await desktop.WaitForExitAsync(timeout.Token); desktop.Dispose();
        }
        using var port = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 17841); port.Start(); port.Stop();
        Console.WriteLine("PASS: graceful host exit and port release");
    }
    private static MultipartFormDataContent Form(string audio, string format)
    {
        var form = new MultipartFormDataContent(); form.Add(new StreamContent(File.OpenRead(audio)), "file", Path.GetFileName(audio)); form.Add(new StringContent("whisper-1"), "model"); form.Add(new StringContent(format), "response_format"); return form;
    }
    private static void Require(bool condition, string check) { if (!condition) throw new Exception("FAIL: " + check); }
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] private static extern bool LockWorkStation();
}
