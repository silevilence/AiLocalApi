using System.Globalization;
using System.Text;
using AiLocalApi.Contracts;
using AiLocalApi.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
namespace AiLocalApi.Desktop;
internal static class ApiRoutes
{
    public static void Configure(WebApplicationBuilder builder, ApplicationService service) => builder.Services.Configure<FormOptions>(options => { options.MultipartBodyLengthLimit = service.Store.Settings.MaxUploadBytes; options.MemoryBufferThreshold = 65536; options.ValueLengthLimit = 8192; });
    public static void Map(WebApplication app, ApplicationService service)
    {
        app.Use(async (context, next) =>
        {
            try
            {
                if (context.Request.Host.Host is not ("localhost" or "127.0.0.1" or "[::1]") || context.Request.Headers.ContainsKey("Origin")) throw new AppException("origin_denied", "Only local non-browser clients are allowed.", 403);
                if (context.Request.Path != "/health" && !service.Store.Authorize(context.Request.Headers.Authorization.ToString())) throw new AppException("unauthorized", "Missing, disabled or invalid Bearer API key.", 401);
                await next(context);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception exception)
            {
                var error = exception as AppException;
                context.Response.StatusCode = error?.Status ?? (exception is BadHttpRequestException bad ? bad.StatusCode : exception is InvalidDataException ? 400 : 500);
                await context.Response.WriteAsJsonAsync(new { error = new { message = error?.Message ?? (context.Response.StatusCode < 500 ? "Invalid request body." : "Internal error; see local diagnostic logs."), type = context.Response.StatusCode < 500 ? "invalid_request_error" : "server_error", param = (string?)null, code = error?.Code ?? "request_failed" } });
                if (error is null) service.Paths.Log("http_request_failed");
            }
        });
        app.MapGet("/health", () => Results.Ok(new { status = "ok", accepting = !service.Paused, modelState = service.ModelState, backend = service.Worker.Backend, version = Distribution.Version, mode = Distribution.Mode, updating = service.Draining }));
        app.MapGet("/v1/models", () => Results.Ok(new { @object = "list", data = ModelList(service) }));
        app.MapGet("/api/jobs", (int? page, int? size) => Results.Ok(service.Store.Jobs(page ?? 0, size ?? 50).Select(PublicJob)));
        app.MapGet("/api/jobs/{id}", (string id) => Results.Ok(PublicJob(service.Get(id))));
        app.MapPost("/api/jobs/{id}/cancel", (string id) => Results.Ok(PublicJob(service.Cancel(id))));
        app.MapPost("/api/jobs/{id}/retry", (string id) => Results.Ok(PublicJob(service.Retry(id))));
        app.MapGet("/api/jobs/{id}/result", (string id, string? format) => { var job = service.Get(id); return Render(job, format ?? "json", job.Options.Words, true); });
        app.MapPost("/api/jobs", async (HttpContext context) =>
        {
            var input = await ParseAsync(context, false);
            await using var stream = input.File.OpenReadStream();
            var job = await service.SubmitAsync(stream, input.File.FileName, input.Model, input.Options, context.RequestAborted);
            return Results.Accepted($"/api/jobs/{job.Id}", PublicJob(job));
        });
        app.MapPost("/v1/audio/transcriptions", (Delegate)((HttpContext context) => CompatibleAsync(context, service, false)));
        app.MapPost("/v1/audio/translations", (Delegate)((HttpContext context) => CompatibleAsync(context, service, true)));
    }
    private static object[] ModelList(ApplicationService service)
    {
        var models = service.Models.Installed.Select(model => (object)new { id = model.Id, @object = "model", created = 0, owned_by = "local", engine = model.Engine, multilingual = model.Multilingual, supports_translation = model.SupportsTranslation, quantization = model.Quantization }).ToList();
        if (service.Store.Settings.DefaultModel is { } id && service.Models.Installed.Any(m => m.Id == id)) models.Add(new { id = "whisper-1", @object = "model", created = 0, owned_by = "local", alias_of = id });
        return models.ToArray();
    }
    private static object PublicJob(Job job) => new { id = job.Id, model = job.Model, engine = job.Engine, state = job.State, stage = job.Stage, created = job.Created, updated = job.Updated, attempts = job.Attempts, error = job.Error, result_url = job.State == "succeeded" ? $"/api/jobs/{job.Id}/result" : null };
    private sealed record Input(IFormFile File, string Model, TranscriptionOptions Options, string Format, bool Segments);
    private static async Task<Input> ParseAsync(HttpContext context, bool translate)
    {
        if (!context.Request.HasFormContentType) throw new AppException("multipart_required", "multipart/form-data is required.", 415);
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var allowed = new HashSet<string>(["model", "language", "prompt", "temperature", "response_format", "timestamp_granularities[]"]);
        if (form.Keys.Any(key => !allowed.Contains(key))) throw new AppException("unsupported_parameter", "Unsupported request parameter.");
        if (form.Files.Count != 1 || form.Files[0].Name != "file") throw new AppException("file_required", "Exactly one file field is required.");
        if (form.Keys.Any(key => key != "timestamp_granularities[]" && form[key].Count != 1)) throw new AppException("duplicate_parameter", "Duplicate request parameter.");
        var model = form["model"].ToString(); if (string.IsNullOrWhiteSpace(model)) throw new AppException("model_required", "model is required.");
        var language = form["language"].ToString();
        if (translate && language.Length > 0) throw new AppException("unsupported_parameter", "translations does not accept language; output is English.");
        var format = form["response_format"].FirstOrDefault() ?? "json";
        if (!TranscriptFormats.Supported.Contains(format)) throw new AppException("unsupported_format", "Unsupported response_format.");
        var granularity = form["timestamp_granularities[]"].ToArray();
        if (granularity.Any(item => item is not ("segment" or "word")) || granularity.Length > 0 && (format != "verbose_json" || translate)) throw new AppException("invalid_timestamps", "Timestamps require verbose_json on transcriptions.");
        var temperature = 0f;
        if (form.ContainsKey("temperature") && !float.TryParse(form["temperature"], NumberStyles.Float, CultureInfo.InvariantCulture, out temperature)) throw new AppException("invalid_temperature", "temperature must be numeric.");
        return new(form.Files[0], model, new(language.Length == 0 ? null : language, form["prompt"].FirstOrDefault(), temperature, translate, granularity.Contains("word")), format, granularity.Length == 0 || granularity.Contains("segment"));
    }
    private static async Task<IResult> CompatibleAsync(HttpContext context, ApplicationService service, bool translate)
    {
        var input = await ParseAsync(context, translate);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); timeout.CancelAfter(TimeSpan.FromMinutes(30));
        await using var stream = input.File.OpenReadStream();
        var job = await service.SubmitAsync(stream, input.File.FileName, input.Model, input.Options, timeout.Token);
        try { return Render(await service.WaitAsync(job.Id, timeout.Token), input.Format, input.Options.Words, input.Segments); }
        catch (OperationCanceledException) { service.Cancel(job.Id); if (context.RequestAborted.IsCancellationRequested) throw; throw new AppException("timeout", "Request timed out; local job cancelled.", 504); }
    }
    private static IResult Render(Job job, string format, bool words, bool segments)
    {
        if (job.State == "expired") throw new AppException("result_expired", "Result has expired.", 410);
        if (job.State != "succeeded" || job.Result is null) throw new AppException("result_unavailable", job.Error ?? $"Job is {job.State}.", job.State is "queued" or "running" ? 409 : 422);
        var result = TranscriptFormats.Format(job.Result, format, words, segments); return Results.Text(result.Content, result.ContentType, Encoding.UTF8);
    }
}
