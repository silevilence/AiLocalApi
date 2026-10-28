using System.Text.Json;

namespace AiLocalApi.Contracts;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Decode<T>(string value) => JsonSerializer.Deserialize<T>(value, Json) ?? throw new InvalidDataException("Empty message");
}

public sealed record TranscriptionOptions(string? Language = null, string? Prompt = null, float Temperature = 0, bool Translate = false, bool Words = false);
public sealed record Word(string Text, double Start, double End);
public sealed record Segment(string Text, double Start, double End);
public sealed record Transcript(string Text, Segment[] Segments, Word[] Words, double Duration, string? Language);
public sealed record WorkerRequest(string Id, string Command, string? ModelPath = null, string? AudioPath = null, TranscriptionOptions? Options = null, string Backend = "cpu", int MaxDurationSeconds = 7200);
public sealed record WorkerReply(string Id, string Kind, string? Stage = null, string? Error = null, Transcript? Result = null, string? Backend = null, int LoadCount = 0, string? BackendDetails = null);
