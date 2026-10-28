namespace AiLocalApi.Core;

public sealed record AppPaths(string Data)
{
    public string Database => Path.Combine(Data, "state.db");
    public string Temporary => Path.Combine(Data, "jobs");
    public string Logs => Path.Combine(Data, "logs");
    public static AppPaths Default => new(Path.GetFullPath(Environment.GetEnvironmentVariable("AILOCAL_DATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiLocalApi")));
    public void Initialize()
    {
        foreach (var path in new[] { Data, Temporary, Logs }) Directory.CreateDirectory(path);
    }
    public void Log(string message, string? jobId = null)
    {
        // Callers supply diagnostic codes, never audio, prompts or transcript content.
        lock (this) File.AppendAllText(Path.Combine(Logs, $"{DateTime.UtcNow:yyyy-MM-dd}.log"), $"{DateTimeOffset.UtcNow:O} job={jobId ?? "-"} {message}{Environment.NewLine}");
    }
}

public sealed class AppException(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
