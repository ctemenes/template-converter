using System.Text.Json;
using System.Text.Json.Nodes;
using Converter.Core;

// Batch converter.
//   dotnet run --project src/Converter.Cli -- <file-or-folder> [output-folder] [--rules-only] [--concurrency N]
// With ANTHROPIC_API_KEY set, the model step runs. Without it, the rule-based pass runs alone.

var rulesOnly = args.Contains("--rules-only");
var concurrency = 4;
var positional = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--concurrency" && i + 1 < args.Length) concurrency = int.Parse(args[++i]);
    else if (!args[i].StartsWith("--")) positional.Add(args[i]);
}
if (positional.Count == 0)
{
    Console.Error.WriteLine("usage: convert <file-or-folder> [output-folder] [--rules-only] [--concurrency N]");
    return 2;
}

var input = positional[0];
var output = positional.Count > 1 ? positional[1] : "out";
var files = Directory.Exists(input)
    ? Directory.GetFiles(input, "*.docx").OrderBy(f => f, StringComparer.Ordinal).ToList()
    : new List<string> { input };
Directory.CreateDirectory(output);

var pipeline = new ConversionPipeline(rulesOnly ? null : LlmMapper.FromEnvironment());
Console.WriteLine($"Converting {files.Count} template(s). Mode: {pipeline.Mode}. Concurrency: {concurrency}.\n");

var results = await pipeline.ConvertBatchAsync(files.Select(f => (Path.GetFileName(f), File.ReadAllBytes(f))), concurrency);
var pretty = new JsonSerializerOptions { WriteIndented = true };

Console.WriteLine($"{"Template",-36} {"Status",-14} {"Fidelity",8} {"Vars",5} {"Rules",5} {"Tries",5} {"Review",6}  Mapper");
Console.WriteLine(new string('-', 104));
foreach (var r in results)
{
    var stem = Path.GetFileNameWithoutExtension(r.File);
    if (r.Template is not null) File.WriteAllText(Path.Combine(output, stem + ".template.json"), r.Template.ToJsonString(pretty));
    File.WriteAllText(Path.Combine(output, stem + ".report.json"), r.ToJson().ToJsonString(pretty));
    Console.WriteLine($"{Short(r.File, 36),-36} {r.Status,-14} {r.Fidelity,8:0.0} {Count(r, "variables"),5} {Count(r, "rules"),5} {r.Attempts.Count(a => a.Number > 0),5} {r.Validation?.NeedsReview ?? 0,6}  {r.Mapper}");
}

var summary = new JsonObject
{
    ["mode"] = pipeline.Mode,
    ["generatedUtc"] = DateTime.UtcNow.ToString("o"),
    ["total"] = results.Count,
    ["converted"] = results.Count(r => r.Status == "Converted"),
    ["needsReview"] = results.Count(r => r.Status == "Needs review"),
    ["failed"] = results.Count(r => r.Status is "Failed checks" or "Unreadable"),
    ["results"] = new JsonArray(results.Select(r => (JsonNode)r.ToJson()).ToArray())
};
File.WriteAllText(Path.Combine(output, "summary.json"), summary.ToJsonString(pretty));
Console.WriteLine($"\nConverted {summary["converted"]}, needs review {summary["needsReview"]}, failed {summary["failed"]}. Reports written to {Path.GetFullPath(output)}");
return results.Any(r => r.Status is "Failed checks" or "Unreadable") ? 1 : 0;

static string Short(string s, int n) => s.Length <= n ? s : s[..(n - 3)] + "...";
static int Count(ConversionResult r, string key) => (r.Template?[key] as JsonArray)?.Count ?? 0;
