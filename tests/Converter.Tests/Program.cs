using System.Text.Json.Nodes;
using Converter.Core;

// Dependency-free test runner: `dotnet run --project tests/Converter.Tests`. Exit code 1 if anything fails.
// The engine has no third-party packages, and the tests keep to the same rule.

var root = AppContext.BaseDirectory;
while (!Directory.Exists(Path.Combine(root, "samples", "legacy"))) root = Path.GetDirectoryName(root) ?? throw new Exception("samples/legacy not found");
byte[] Sample(string name) => File.ReadAllBytes(Path.Combine(root, "samples", "legacy", name));
SourceTemplate Read(string name) => DocxReader.Read(name, Sample(name));

var failed = 0;
var passed = 0;
async Task Test(string name, Func<Task> body)
{
    try { await body(); passed++; Console.WriteLine("  ok    " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("  FAIL  " + name + "\n        " + ex.Message); }
}
void Check(bool condition, string what) { if (!condition) throw new Exception(what); }

Console.WriteLine("Reader");
await Test("reads real merge fields with their format switches", () =>
{
    var src = Read("01-policy-renewal-notice.docx");
    var fields = src.AllInlines().Where(i => i.Kind == InlineKind.MergeField).ToList();
    Check(fields.Any(f => f.Name == "First_Name"), "First_Name not found");
    Check(fields.First(f => f.Name == "Renewal_Premium").Format == "$#,##0.00", "numeric format switch lost");
    Check(fields.First(f => f.Name == "Letter_Date").Format == "d MMMM yyyy", "date format switch lost");
    return Task.CompletedTask;
});
await Test("reads IF fields, including a merge field nested in a branch", () =>
{
    var ifs = Read("01-policy-renewal-notice.docx").AllInlines().Where(i => i.Kind == InlineKind.IfField).Select(i => i.If!).ToList();
    Check(ifs.Count == 2, $"expected 2 IF fields, got {ifs.Count}");
    var qld = ifs.First(i => i.Left == "State");
    Check(qld.Op == "=" && qld.Right == "QLD", "IF State = QLD not parsed");
    Check(qld.WhenTrue.Any(i => i.Kind == InlineKind.MergeField && i.Name == "Levy_Amount"), "nested merge field lost");
    Check(ifs.First(i => i.Left == "Auto_Renew").WhenFalse.Count > 0, "else branch lost");
    return Task.CompletedTask;
});
await Test("does not read a field's cached result as text", () =>
{
    var text = string.Concat(Read("01-policy-renewal-notice.docx").AllInlines().Where(i => i.Kind == InlineKind.Text).Select(i => i.Text));
    Check(!text.Contains('«'), "cached field result leaked into the text");
    return Task.CompletedTask;
});
await Test("separates typed placeholders from instructions to the writer", () =>
{
    var src = Read("02-overdue-account-reminder.docx");
    Check(src.AllInlines().Count(i => i.Kind == InlineKind.Instruction) == 2, "expected 2 instructions");
    var typed = src.AllInlines().Where(i => i.Kind == InlineKind.Placeholder).ToList();
    Check(typed.Any(p => p.Name == "Customer Name" && p.Convention == "<<>>"), "<<Customer Name>> not found");
    Check(typed.All(p => p.Name != "Include the next paragraph only if the account is more than 60 days overdue"), "instruction read as a placeholder");
    return Task.CompletedTask;
});
await Test("recognises four placeholder conventions", () =>
{
    var split = DocxReader.SplitTypedPlaceholders(new List<Inline>
        { new() { Kind = InlineKind.Text, Text = "a <<One>> b {{two}} c «Three» d [Four Five] e [Delete if not needed]" } });
    var kinds = split.Where(i => i.Kind != InlineKind.Text).Select(i => i.Kind + ":" + i.Name).ToList();
    Check(string.Join("|", kinds) == "Placeholder:One|Placeholder:two|Placeholder:Three|Placeholder:Four Five|Instruction:", string.Join("|", kinds));
    return Task.CompletedTask;
});
await Test("reads repeat markers, page setup and the footer", () =>
{
    var src = Read("03-account-statement.docx");
    Check(src.AllInlines().Any(i => i.Kind == InlineKind.RepeatStart && i.Name == "Transactions"), "TableStart not read");
    Check(src.Page.Orientation == "landscape" && src.Page.Size == "A4", "page setup wrong");
    Check(src.Footer.Count == 1 && src.AllInlines().Any(i => i.Kind == InlineKind.SystemField && i.Name == "PAGE"), "footer or PAGE field missing");
    return Task.CompletedTask;
});
await Test("rejects a file that is not a Word document", async () =>
{
    var result = await new ConversionPipeline().ConvertAsync("notes.docx", "plain text"u8.ToArray());
    Check(result.Status == "Unreadable" && result.Error is not null, "expected Unreadable");
});

Console.WriteLine("Rule-based pass and validator");
await Test("a template with only fields and IF logic converts with no model", async () =>
{
    var r = await new ConversionPipeline().ConvertAsync("01-policy-renewal-notice.docx", Sample("01-policy-renewal-notice.docx"));
    Check(r.Status == "Converted" && r.Fidelity == 100, $"{r.Status} {r.Fidelity}");
    Check(r.Renders.Count == 4 && r.Renders[0].Html != r.Renders[^1].Html, "expected all-true, each-alone and all-false renders that differ");
});
await Test("a repeating row becomes a list variable with fields", async () =>
{
    var r = await new ConversionPipeline().ConvertAsync("03-account-statement.docx", Sample("03-account-statement.docx"));
    var list = r.Template!["variables"]!.AsArray().First(v => v!["name"]!.ToString() == "transactions")!;
    Check(list["type"]!.ToString() == "list" && list["fields"]!.AsArray().Count == 3, "list variable wrong");
    Check(r.Renders[0].Html.Split("<tr>").Length == 5, "expected a header row and three data rows");
});
await Test("instructions are never printed and are sent for review", async () =>
{
    var r = await new ConversionPipeline().ConvertAsync("06-fee-change-notice.docx", Sample("06-fee-change-notice.docx"));
    Check(r.Status == "Needs review" && r.Validation!.NeedsReview == 1, r.Status);
    Check(!r.Renders[0].Html.Contains("Legal"), "instruction text was printed");
});
await Test("validator catches lost wording", () =>
{
    var src = Read("01-policy-renewal-notice.docx");
    var t = new RuleMapper().Map(src);
    var blocks = t["blocks"]!.AsArray();
    blocks.RemoveAt(blocks.Count - 3); blocks.RemoveAt(blocks.Count - 3); blocks.RemoveAt(5);
    var v = Validator.Validate(src, t);
    Check(v.Findings.Any(f => f.Check is "wording" or "fields"), "expected a wording or fields finding");
    Check(v.WordRecall < 1, "recall should drop");
    return Task.CompletedTask;
});
await Test("validator catches an unbound field, invented words and a printed instruction", () =>
{
    var src = Read("02-overdue-account-reminder.docx");
    var t = new RuleMapper().Map(src);
    t["variables"]!.AsArray().First(v => v!["name"]!.ToString() == "dueDate")!["sourceNames"] = new JsonArray();
    var para = t["blocks"]!.AsArray().First(b => b!["type"]!.ToString() == "paragraph")!["content"]!.AsArray();
    para.Add(new JsonObject { ["text"] = " We value your loyalty enormously and appreciate your continued patronage of our wonderful services today." });
    para.Add(new JsonObject { ["text"] = "[Include the next paragraph only if the account is more than 60 days overdue]" });
    var v = Validator.Validate(src, t);
    Check(v.Errors.Any(e => e.Check == "fields"), "unbound field not caught");
    Check(v.Errors.Any(e => e.Check == "leftovers"), "printed instruction not caught");
    Check(v.Errors.Any(e => e.Check == "wording"), "invented words not caught");
    return Task.CompletedTask;
});
await Test("validator catches an undeclared variable and a missing condition", () =>
{
    var src = Read("01-policy-renewal-notice.docx");
    var t = new RuleMapper().Map(src);
    var blocks = t["blocks"]!.AsArray();
    var conditional = blocks.First(b => b!["type"]!.ToString() == "conditional")!;
    var index = blocks.IndexOf(conditional);
    var inner = conditional["blocks"]!.AsArray()[0]!.DeepClone();
    blocks[index] = inner;                                           // condition dropped, text kept
    blocks.Add(new JsonObject { ["type"] = "paragraph", ["style"] = "Normal", ["content"] = new JsonArray(new JsonObject { ["var"] = "ghost" }) });
    var v = Validator.Validate(src, t);
    Check(v.Errors.Any(e => e.Check == "logic"), "missing condition not caught");
    Check(v.Errors.Any(e => e.Message.Contains("ghost")), "undeclared variable not caught");
    return Task.CompletedTask;
});

await Test("the render test reaches a condition nested inside an else branch", () =>
{
    JsonObject Para(string text) => new() { ["type"] = "paragraph", ["style"] = "Normal", ["content"] = new JsonArray(new JsonObject { ["text"] = text }) };
    var template = new JsonObject
    {
        ["variables"] = new JsonArray(
            new JsonObject { ["name"] = "onHardship", ["type"] = "boolean", ["sourceNames"] = new JsonArray() },
            new JsonObject { ["name"] = "daysOverdue", ["type"] = "number", ["sourceNames"] = new JsonArray() }),
        ["blocks"] = new JsonArray(new JsonObject
        {
            ["type"] = "conditional", ["when"] = new JsonObject { ["var"] = "onHardship", ["op"] = "isTrue" },
            ["blocks"] = new JsonArray(Para("ARRANGEMENT")),
            ["else"] = new JsonArray(new JsonObject
            {
                ["type"] = "conditional", ["when"] = new JsonObject { ["var"] = "daysOverdue", ["op"] = "gt", ["value"] = 60 },
                ["blocks"] = new JsonArray(Para("COLLECTIONS"))
            })
        })
    };
    var renders = SampleData.Variants(template).Select(v => Renderer.RenderText(template, v.Data)).ToList();
    Check(renders.Any(r => r.Contains("ARRANGEMENT")), "first branch never rendered");
    Check(renders.Any(r => r.Contains("COLLECTIONS")), "nested else branch never rendered");
    Check(renders.Any(r => r.Trim().Length == 0), "the neither case never rendered");
    return Task.CompletedTask;
});
await Test("a currency variable with no format prints to two decimals", () =>
{
    var template = new JsonObject
    {
        ["variables"] = new JsonArray(new JsonObject { ["name"] = "amount", ["type"] = "currency", ["sourceNames"] = new JsonArray() }),
        ["blocks"] = new JsonArray(new JsonObject { ["type"] = "paragraph", ["style"] = "Normal",
            ["content"] = new JsonArray(new JsonObject { ["text"] = "$" }, new JsonObject { ["var"] = "amount" }) })
    };
    var text = Renderer.RenderText(template, new JsonObject { ["amount"] = 1250.5 });
    Check(text == "$1,250.50", text);
    Check(template["blocks"]![0]!["content"]![1]!["format"] is null, "rendering must not change the stored template");
    return Task.CompletedTask;
});

Console.WriteLine("Model step and repair loop");
await Test("a failed check is sent back to the model and the fixed answer is accepted", async () =>
{
    var fake = new ScriptedMapper(
        (src, draft) => { var t = (JsonObject)draft.DeepClone(); t["blocks"]!.AsArray().RemoveAt(2); return Resolve(t); },   // loses a paragraph
        (src, draft) => Resolve((JsonObject)draft.DeepClone()));
    var r = await new ConversionPipeline(fake).ConvertAsync("06-fee-change-notice.docx", Sample("06-fee-change-notice.docx"));
    Check(fake.Calls == 2, $"expected 2 model calls, got {fake.Calls}");
    Check(fake.ErrorsSeen.Count == 2 && fake.ErrorsSeen[1].Count > 0, "the validator's errors were not passed back");
    Check(r.Status == "Converted" && r.Mapper == "fake", $"{r.Status} via {r.Mapper}");
    Check(r.Attempts.Count == 3, "attempt log should show the rule pass and two model attempts");
});
await Test("if the model never passes, the rule-based result stands", async () =>
{
    var fake = new ScriptedMapper((src, draft) => new JsonObject { ["blocks"] = "nonsense" });
    var r = await new ConversionPipeline(fake, maxAttempts: 2).ConvertAsync("05-welcome-letter.docx", Sample("05-welcome-letter.docx"));
    Check(fake.Calls == 2 && r.Mapper == "rules" && r.Status == "Needs review", $"{fake.Calls} {r.Mapper} {r.Status}");
});
await Test("if the model call fails, the rule-based result stands", async () =>
{
    var fake = new ScriptedMapper((src, draft) => throw new InvalidOperationException("Model call failed (529)"));
    var r = await new ConversionPipeline(fake, maxAttempts: 1).ConvertAsync("01-policy-renewal-notice.docx", Sample("01-policy-renewal-notice.docx"));
    Check(r.Status == "Converted" && r.Mapper == "rules", $"{r.Status} {r.Mapper}");
});
await Test("JSON is pulled out of a reply wrapped in prose or code fences", () =>
{
    var o = LlmMapper.ExtractJson("Here you go:\n```json\n{\"blocks\":[]}\n```\nDone.");
    Check(o["blocks"] is JsonArray, "not parsed");
    return Task.CompletedTask;
});
await Test("a batch runs every file and respects the concurrency cap", async () =>
{
    var files = Directory.GetFiles(Path.Combine(root, "samples", "legacy"), "*.docx").Select(f => (Path.GetFileName(f), File.ReadAllBytes(f)));
    var results = await new ConversionPipeline().ConvertBatchAsync(files, concurrency: 2);
    Check(results.Count == 6 && results.All(r => r.Status is "Converted" or "Needs review"), "batch did not complete cleanly");
});

Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

// Marks every needs-review rule as resolved, standing in for the model's interpretation.
static JsonObject Resolve(JsonObject template)
{
    foreach (var rule in template["rules"]!.AsArray()) { rule!["resolution"] = "removed"; rule["confidence"] = 0.9; }
    return template;
}

sealed class ScriptedMapper(params Func<SourceTemplate, JsonObject, JsonObject>[] answers) : ITemplateMapper
{
    public string Name => "fake";
    public int Calls { get; private set; }
    public List<IReadOnlyList<string>> ErrorsSeen { get; } = new();

    public Task<MapResult> MapAsync(SourceTemplate source, JsonObject draft, JsonObject? previous, IReadOnlyList<string> errors, CancellationToken ct)
    {
        ErrorsSeen.Add(errors.ToList());
        var answer = answers[Math.Min(Calls, answers.Length - 1)];
        Calls++;
        return Task.FromResult(new MapResult(answer(source, draft), 100, 200));
    }
}
