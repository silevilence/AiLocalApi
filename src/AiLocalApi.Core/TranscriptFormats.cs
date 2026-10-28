using System.Globalization;
using System.Text;
using AiLocalApi.Contracts;
namespace AiLocalApi.Core;
public static class TranscriptFormats
{
    public static readonly string[] Supported = ["json", "text", "srt", "vtt", "verbose_json"];
    public static (string Content, string ContentType) Format(Transcript result, string format, bool words = false, bool segments = true) => format switch
    {
        "text" => (result.Text, "text/plain; charset=utf-8"),
        "json" => (Wire.Encode(new { text = result.Text }), "application/json; charset=utf-8"),
        "verbose_json" => (Verbose(result, words, segments), "application/json; charset=utf-8"),
        "srt" => (Subtitles(result, false), "application/x-subrip; charset=utf-8"),
        "vtt" => (Subtitles(result, true), "text/vtt; charset=utf-8"),
        _ => throw new AppException("unsupported_format", "response_format 必须是 json/text/srt/vtt/verbose_json。")
    };
    private static string Verbose(Transcript result, bool words, bool segments)
    {
        var response = new Dictionary<string, object?> { ["text"] = result.Text, ["language"] = result.Language, ["duration"] = result.Duration };
        if (segments) response["segments"] = result.Segments.Select((segment, id) => new { id, start = segment.Start, end = segment.End, text = segment.Text });
        if (words) response["words"] = result.Words.Select(word => new { word = word.Text, start = word.Start, end = word.End });
        return Wire.Encode(response);
    }
    private static string Subtitles(Transcript transcript, bool vtt)
    {
        var result = new StringBuilder(vtt ? "WEBVTT\n\n" : "");
        for (var i = 0; i < transcript.Segments.Length; i++)
        {
            var segment = transcript.Segments[i];
            if (!vtt) result.Append(i + 1).Append('\n');
            result.Append(Time(segment.Start, vtt)).Append(" --> ").Append(Time(segment.End, vtt)).Append('\n').Append(segment.Text.Trim()).Append("\n\n");
        }
        return result.ToString();
    }
    private static string Time(double seconds, bool vtt)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, Math.Round(seconds * 1000)));
        return string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}{(vtt ? '.' : ',')}{time.Milliseconds:000}");
    }
}
