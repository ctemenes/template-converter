using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Converter.Core;

// HTTP host for the conversion engine. Runs anywhere ASP.NET Core runs, including Azure App Service.
//   POST /api/convert                  body = the .docx bytes, header X-File-Name = its name
//   POST /api/convert/sample/{name}    converts one of the bundled sample templates
//   POST /api/batch/samples            starts a batch over every sample, returns a job id
//   GET  /api/jobs/{id}                batch progress and per-template summaries
//   GET  /api/results/{name}           full result for one template (source read, template, checks, renders)

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddSingleton(sp =>
    new ConversionPipeline(LlmMapper.FromEnvironment(sp.GetRequiredService<IHttpClientFactory>().CreateClient())));
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var samplesDir = Path.Combine(AppContext.BaseDirectory, "samples");
var results = new ConcurrentDictionary<string, ConversionResult>(StringComparer.OrdinalIgnoreCase);
var jobs = new ConcurrentDictionary<string, BatchJob>();

string[] Samples() => Directory.Exists(samplesDir)
    ? Directory.GetFiles(samplesDir, "*.docx").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!
    : Array.Empty<string>();

app.MapGet("/api/info", (ConversionPipeline pipeline) => Results.Json(new
{
    mode = pipeline.Mode,
    samples = Samples(),
    converted = results.Values.Select(Summary).ToArray()
}));

app.MapPost("/api/convert/sample/{name}", async (string name, ConversionPipeline pipeline, CancellationToken ct) =>
{
    var path = Path.Combine(samplesDir, Path.GetFileName(name));
    if (!File.Exists(path)) return Results.NotFound(new { error = "No sample with that name." });
    var result = await pipeline.ConvertAsync(Path.GetFileName(path), await File.ReadAllBytesAsync(path, ct), ct);
    results[result.File] = result;
    return Json(result.ToJson());
});

app.MapPost("/api/convert", async (HttpRequest request, ConversionPipeline pipeline, CancellationToken ct) =>
{
    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer, ct);
    if (buffer.Length == 0) return Results.BadRequest(new { error = "Send the .docx file as the request body." });
    if (buffer.Length > 20 * 1024 * 1024) return Results.BadRequest(new { error = "File is larger than 20 MB." });
    var name = Path.GetFileName(Uri.UnescapeDataString(request.Headers["X-File-Name"].FirstOrDefault() ?? "uploaded.docx"));
    var result = await pipeline.ConvertAsync(name, buffer.ToArray(), ct);
    results[result.File] = result;
    return Json(result.ToJson());
});

app.MapPost("/api/batch/samples", (ConversionPipeline pipeline) =>
{
    var names = Samples();
    var job = new BatchJob(Guid.NewGuid().ToString("n")[..12], names.Length);
    jobs[job.Id] = job;
    _ = Task.Run(async () =>
    {
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(names.Select(async name =>
        {
            await gate.WaitAsync();
            try
            {
                var result = await pipeline.ConvertAsync(name, await File.ReadAllBytesAsync(Path.Combine(samplesDir, name)));
                results[result.File] = result;
                job.Done.Add(result.File);
            }
            catch (Exception ex) { job.Errors.Add($"{name}: {ex.Message}"); }
            finally { gate.Release(); }
        }));
        job.FinishedUtc = DateTime.UtcNow;
    });
    return Results.Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id, total = job.Total });
});

app.MapGet("/api/jobs/{id}", (string id) => jobs.TryGetValue(id, out var job)
    ? Results.Json(new
    {
        jobId = job.Id, total = job.Total, done = job.Done.Count + job.Errors.Count, finished = job.FinishedUtc is not null,
        errors = job.Errors.ToArray(),
        results = job.Done.Select(n => results.TryGetValue(n, out var r) ? Summary(r) : null).Where(s => s is not null).ToArray()
    })
    : Results.NotFound());

app.MapGet("/api/results/{name}", (string name) => results.TryGetValue(name, out var result)
    ? Json(result.ToJson())
    : Results.NotFound());

app.Run();

static IResult Json(JsonObject json) => Results.Text(json.ToJsonString(), "application/json");

static object Summary(ConversionResult r) => new
{
    file = r.File, status = r.Status, fidelity = r.Fidelity, mapper = r.Mapper,
    needsReview = r.Validation?.NeedsReview ?? 0,
    attempts = r.Attempts.Count(a => a.Number > 0), millis = r.Millis
};

sealed class BatchJob(string id, int total)
{
    public string Id { get; } = id;
    public int Total { get; } = total;
    public ConcurrentBag<string> Done { get; } = new();
    public ConcurrentBag<string> Errors { get; } = new();
    public DateTime? FinishedUtc { get; set; }
}
