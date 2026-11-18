using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Converter.Core;

/// <summary>
/// Checks a converted template against the source it came from.
/// Every check is deterministic. The model never marks its own work:
/// its output only counts once it passes here, and the error messages
/// are written so they can be handed straight back to the model.
/// </summary>
public static class Validator
{
    private static readonly HashSet<string> Types = new() { "string", "number", "currency", "date", "boolean", "list" };
    private static readonly HashSet<string> Ops = new() { "eq", "ne", "gt", "ge", "lt", "le", "in", "isTrue", "isFalse", "exists" };
    private static readonly HashSet<string> Sys = new() { "pageNumber", "pageCount", "today" };
    private static readonly HashSet<string> Resolutions = new() { "conditional", "repeat", "removed", "needs-review" };
    private static readonly Regex PlaceholderSyntax = new(@"<<[^<>]+>>|«[^«»]+»|\{\{[^{}]+\}\}|MERGEFIELD", RegexOptions.Compiled);
    /// <summary>Share of source words that may be absent before the wording check fails.</summary>
    public const double MaxMissingShare = 0.05;

    private static readonly Regex Word = new(@"[A-Za-z0-9]+(?:[.'/][A-Za-z0-9]+)*", RegexOptions.Compiled);

    public static ValidationResult Validate(SourceTemplate src, JsonObject template)
    {
        var result = new ValidationResult();
        var walk = new Walk();

        // ---- 1. structure -------------------------------------------------
        var variables = template["variables"] as JsonArray;
        var blocks = template["blocks"] as JsonArray;
        if (variables is null) Err(result, "structure", "\"variables\" must be an array.");
        if (blocks is null) Err(result, "structure", "\"blocks\" must be an array.");
        if (variables is null || blocks is null) return result;

        var declared = new Dictionary<string, JsonObject>();     // "name" and "list.field" -> declaration
        foreach (var node in variables)
        {
            if (node is not JsonObject v || v["name"] is null) { Err(result, "structure", "Every variable needs a \"name\"."); continue; }
            var name = v["name"]!.ToString();
            var type = v["type"]?.ToString() ?? "";
            if (!Types.Contains(type)) Err(result, "structure", $"Variable \"{name}\" has type \"{type}\". Allowed: {string.Join(", ", Types)}.");
            if (v["sourceNames"] is not JsonArray) Err(result, "structure", $"Variable \"{name}\" needs a \"sourceNames\" array (empty if it does not come from a source field).");
            if (!declared.TryAdd(name, v)) Err(result, "structure", $"Variable \"{name}\" is declared twice.");
            if (type == "list")
                foreach (var f in v["fields"] as JsonArray ?? new JsonArray())
                    if (f is JsonObject fo && fo["name"] is not null) declared[name + "." + fo["name"]] = fo;
        }

        WalkContent(template["header"] as JsonArray, "header", walk, result, declared);
        WalkContent(template["footer"] as JsonArray, "footer", walk, result, declared);
        WalkBlocks(blocks, "blocks", walk, result, declared);

        var rules = template["rules"] as JsonArray ?? new JsonArray();
        foreach (var r in rules.OfType<JsonObject>())
        {
            var resolution = r["resolution"]?.ToString() ?? "";
            if (!Resolutions.Contains(resolution))
                Err(result, "structure", $"Rule \"{r["id"]}\" has resolution \"{resolution}\". Allowed: {string.Join(", ", Resolutions)}.");
        }

        // ---- 2. every source field is bound to a variable that is used ------
        var sourceFields = src.AllInlines()
            .Where(i => i.Kind is InlineKind.MergeField or InlineKind.Placeholder or InlineKind.RepeatStart)
            .Select(i => i.Name!)
            .Concat(src.AllInlines().Where(i => i.Kind == InlineKind.IfField).Select(i => i.If!.Left))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var bySource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // source name -> variable reference
        foreach (var (reference, decl) in declared)
            foreach (var s in decl["sourceNames"] as JsonArray ?? new JsonArray())
                if (s is not null) bySource.TryAdd(s.ToString(), reference);

        var bound = 0;
        foreach (var field in sourceFields)
        {
            if (!bySource.TryGetValue(field, out var reference))
                Err(result, "fields", $"Source field \"{field}\" is not bound. Add it to the sourceNames of the variable that represents it.");
            else if (!walk.UsedVariables.Contains(reference))
                Err(result, "fields", $"Variable \"{reference}\" (source field \"{field}\") is declared but never used in the template.");
            else bound++;
        }
        result.FieldCoverage = sourceFields.Count == 0 ? 1 : (double)bound / sourceFields.Count;

        // ---- 3. logic: IF fields, repeats, instructions ---------------------
        var logicTotal = 0;
        var logicDone = 0;

        foreach (var group in src.AllInlines().Where(i => i.Kind == InlineKind.IfField).GroupBy(i => i.If!.Left, StringComparer.OrdinalIgnoreCase))
        {
            logicTotal += group.Count();
            var reference = bySource.GetValueOrDefault(group.Key);
            var have = reference is null ? 0 : walk.ConditionVariables.Count(v => v == reference);
            logicDone += Math.Min(have, group.Count());
            if (have < group.Count())
                Err(result, "logic", $"The source has {group.Count()} IF field(s) testing \"{group.Key}\" but the template has {have} condition(s) on it. Each IF must become a \"when\" condition.");
        }

        foreach (var repeat in src.AllInlines().Where(i => i.Kind == InlineKind.RepeatStart))
        {
            logicTotal++;
            var reference = bySource.GetValueOrDefault(repeat.Name!);
            if (reference is not null && walk.RepeatLists.Contains(reference)) logicDone++;
            else Err(result, "logic", $"The source repeats a table row over \"{repeat.Name}\". The template needs a table with \"repeat\": {{\"over\": <list variable bound to {repeat.Name}>}}.");
        }

        var instructions = src.AllInlines().Where(i => i.Kind == InlineKind.Instruction).Select(i => i.Text).ToList();
        foreach (var instruction in instructions)
        {
            logicTotal++;
            var rule = rules.OfType<JsonObject>().FirstOrDefault(r => SameText(r["source"]?.ToString(), instruction));
            if (rule is null)
            {
                Err(result, "logic", $"Instruction not accounted for: \"{instruction}\". Add a rule whose \"source\" is this exact text.");
                continue;
            }
            logicDone++;
            if (rule["resolution"]?.ToString() == "needs-review")
            {
                result.NeedsReview++;
                result.Findings.Add(new Finding(Severity.Warning, "review",
                    $"A person needs to decide: \"{instruction}\". {rule["interpretation"]}"));
            }
        }
        result.LogicCoverage = logicTotal == 0 ? 1 : (double)logicDone / logicTotal;

        // ---- 4. nothing meant for the writer is printed ----------------------
        var typedPlaceholders = src.AllInlines().Where(i => i.Kind == InlineKind.Placeholder)
            .Select(i => i.Text).Distinct().ToList();
        foreach (var text in walk.Texts)
        {
            foreach (var raw in typedPlaceholders)
                if (text.Contains(raw, StringComparison.OrdinalIgnoreCase))
                    Err(result, "leftovers", $"The typed placeholder {raw} is still printed as text. Replace it with a {{\"var\": ...}} item.");
            if (PlaceholderSyntax.IsMatch(text))
                Err(result, "leftovers", $"Placeholder syntax left in printed text: \"{Trim(text)}\". Replace it with a {{\"var\": ...}} item.");
            foreach (var instruction in instructions)
                if (Normal(text).Contains(Normal(instruction)) || Normal(text).Contains("[" + Normal(instruction)))
                    Err(result, "leftovers", $"An instruction is being printed as text: \"{Trim(instruction)}\". Instructions must never appear in content.");
        }

        // ---- 5. wording: nothing lost, nothing invented ----------------------
        var sourceWords = Bag(src.AllInlines().Where(i => i.Kind == InlineKind.Text).Select(i => i.Text));
        var allowedExtra = Bag(instructions);                       // an instruction can say "insert: <wording>"
        var outputWords = Bag(walk.Texts);

        var missing = Subtract(sourceWords, outputWords);
        var invented = Subtract(Subtract(outputWords, sourceWords), allowedExtra);
        var sourceCount = sourceWords.Values.Sum();
        var outputCount = outputWords.Values.Sum();
        result.WordRecall = sourceCount == 0 ? 1 : 1 - (double)missing.Values.Sum() / sourceCount;
        result.WordPrecision = outputCount == 0 ? 1 : 1 - (double)invented.Values.Sum() / outputCount;

        // Missing words: an instruction can legitimately remove a few (a paragraph label, a replaced
        // sentence), so up to MaxMissingShare is reported for a person to read and anything more fails.
        if (result.WordRecall < 1 - MaxMissingShare)
            Err(result, "wording", $"{missing.Values.Sum()} of {sourceCount} source words are missing from the template: {Listing(missing)}. Keep the source wording exactly.");
        else if (missing.Count > 0)
            result.Findings.Add(new Finding(Severity.Info, "wording",
                $"Source words not carried into the template: {Listing(missing)}."));

        // Invented words: no tolerance. Every word printed must come from the source text or from an
        // instruction in the source.
        if (invented.Count > 0)
            Err(result, "wording", $"The template contains words that are not in the source: {Listing(invented)}. Do not add or reword text.");

        // ---- 6. reader warnings are carried through ---------------------------
        foreach (var w in src.ReaderWarnings)
        {
            result.NeedsReview++;
            result.Findings.Add(new Finding(Severity.Warning, "reader", w));
        }

        // ---- 7. it has to render ---------------------------------------------
        if (result.Passed)
        {
            try
            {
                foreach (var variant in SampleData.Variants(template))
                {
                    var html = Renderer.RenderHtml(template, variant.Data);
                    if (html.Contains(Renderer.Missing))
                        Err(result, "render", $"Rendering with sample data left a value unresolved in variant \"{variant.Name}\".");
                }
            }
            catch (Exception ex)
            {
                Err(result, "render", "The template failed to render with sample data: " + ex.Message);
            }
        }
        return result;
    }

    // ---- walking the template ------------------------------------------------

    private sealed class Walk
    {
        public HashSet<string> UsedVariables { get; } = new();
        public List<string> ConditionVariables { get; } = new();
        public HashSet<string> RepeatLists { get; } = new();
        public List<string> Texts { get; } = new();
    }

    private static void WalkBlocks(JsonArray? blocks, string path, Walk walk, ValidationResult result, Dictionary<string, JsonObject> declared)
    {
        if (blocks is null) return;
        for (var i = 0; i < blocks.Count; i++)
        {
            var at = $"{path}[{i}]";
            if (blocks[i] is not JsonObject b) { Err(result, "structure", $"{at} must be an object."); continue; }
            switch (b["type"]?.ToString())
            {
                case "paragraph":
                    if (b["content"] is not JsonArray) Err(result, "structure", $"{at}: a paragraph needs a \"content\" array.");
                    WalkContent(b["content"] as JsonArray, at + ".content", walk, result, declared);
                    break;
                case "conditional":
                    WalkCondition(b["when"], at + ".when", walk, result, declared);
                    if (b["blocks"] is not JsonArray) Err(result, "structure", $"{at}: a conditional needs a \"blocks\" array.");
                    WalkBlocks(b["blocks"] as JsonArray, at + ".blocks", walk, result, declared);
                    WalkBlocks(b["else"] as JsonArray, at + ".else", walk, result, declared);
                    break;
                case "table":
                    foreach (var cell in b["header"] as JsonArray ?? new JsonArray())
                        WalkContent(cell as JsonArray, at + ".header", walk, result, declared);
                    foreach (var row in b["rows"] as JsonArray ?? new JsonArray())
                        foreach (var cell in row as JsonArray ?? new JsonArray())
                            WalkContent(cell as JsonArray, at + ".rows", walk, result, declared);
                    if (b["repeat"] is JsonObject repeat)
                    {
                        var over = repeat["over"]?.ToString() ?? "";
                        if (!declared.TryGetValue(over, out var list) || list["type"]?.ToString() != "list")
                            Err(result, "structure", $"{at}.repeat.over is \"{over}\", which is not a declared list variable.");
                        else { walk.RepeatLists.Add(over); walk.UsedVariables.Add(over); }
                        foreach (var cell in repeat["cells"] as JsonArray ?? new JsonArray())
                            WalkContent(cell as JsonArray, at + ".repeat.cells", walk, result, declared);
                    }
                    break;
                case "pageBreak": break;
                default:
                    Err(result, "structure", $"{at} has type \"{b["type"]}\". Allowed: paragraph, conditional, table, pageBreak.");
                    break;
            }
        }
    }

    private static void WalkContent(JsonArray? content, string path, Walk walk, ValidationResult result, Dictionary<string, JsonObject> declared)
    {
        if (content is null) return;
        for (var i = 0; i < content.Count; i++)
        {
            var at = $"{path}[{i}]";
            if (content[i] is not JsonObject c) { Err(result, "structure", $"{at} must be an object."); continue; }
            if (c["text"] is not null) walk.Texts.Add(c["text"]!.ToString());
            else if (c["var"] is not null)
            {
                var name = c["var"]!.ToString();
                if (!declared.ContainsKey(name)) Err(result, "structure", $"{at} uses variable \"{name}\", which is not declared.");
                else walk.UsedVariables.Add(name);
            }
            else if (c["sys"] is not null)
            {
                if (!Sys.Contains(c["sys"]!.ToString())) Err(result, "structure", $"{at} has sys \"{c["sys"]}\". Allowed: {string.Join(", ", Sys)}.");
            }
            else if (c["when"] is not null)
            {
                WalkCondition(c["when"], at + ".when", walk, result, declared);
                if (c["content"] is not JsonArray) Err(result, "structure", $"{at}: an inline \"when\" needs a \"content\" array.");
                WalkContent(c["content"] as JsonArray, at + ".content", walk, result, declared);
                WalkContent(c["else"] as JsonArray, at + ".else", walk, result, declared);
            }
            else Err(result, "structure", $"{at} must have one of: text, var, sys, when.");
        }
    }

    private static void WalkCondition(JsonNode? node, string at, Walk walk, ValidationResult result, Dictionary<string, JsonObject> declared)
    {
        if (node is not JsonObject c) { Err(result, "structure", $"{at} must be a condition object."); return; }
        if (c["all"] is JsonArray all) { foreach (var x in all) WalkCondition(x, at + ".all", walk, result, declared); return; }
        if (c["any"] is JsonArray any) { foreach (var x in any) WalkCondition(x, at + ".any", walk, result, declared); return; }
        if (c["not"] is not null) { WalkCondition(c["not"], at + ".not", walk, result, declared); return; }
        var name = c["var"]?.ToString() ?? "";
        var op = c["op"]?.ToString() ?? "";
        if (!declared.ContainsKey(name)) Err(result, "structure", $"{at} tests variable \"{name}\", which is not declared.");
        else { walk.UsedVariables.Add(name); walk.ConditionVariables.Add(name); }
        if (!Ops.Contains(op)) Err(result, "structure", $"{at} has op \"{op}\". Allowed: {string.Join(", ", Ops)}.");
        else if (op is not ("isTrue" or "isFalse" or "exists") && c["value"] is null)
            Err(result, "structure", $"{at}: op \"{op}\" needs a \"value\".");
    }

    // ---- helpers ---------------------------------------------------------------

    private static void Err(ValidationResult r, string check, string message) => r.Findings.Add(new Finding(Severity.Error, check, message));
    private static string Normal(string? s) => Regex.Replace((s ?? "").ToLowerInvariant(), @"\s+", " ").Trim();
    private static bool SameText(string? a, string b)
    {
        var x = Normal(a).Trim('[', ']', ' ');
        var y = Normal(b).Trim('[', ']', ' ');
        if (x.Length == 0 || y.Length == 0) return false;
        if (x == y) return true;
        // Allow a rule to quote most of an instruction, but not a fragment of it.
        var (shorter, longer) = x.Length <= y.Length ? (x, y) : (y, x);
        return longer.Contains(shorter) && shorter.Length >= longer.Length * 0.6;
    }
    private static string Trim(string s) => s.Length <= 80 ? s : s[..77] + "...";

    private static Dictionary<string, int> Bag(IEnumerable<string> texts)
    {
        var bag = new Dictionary<string, int>();
        foreach (var t in texts)
            foreach (Match m in Word.Matches(t.ToLowerInvariant()))
                bag[m.Value] = bag.GetValueOrDefault(m.Value) + 1;
        return bag;
    }

    private static Dictionary<string, int> Subtract(Dictionary<string, int> a, Dictionary<string, int> b)
    {
        var diff = new Dictionary<string, int>();
        foreach (var (word, count) in a)
        {
            var left = count - b.GetValueOrDefault(word);
            if (left > 0) diff[word] = left;
        }
        return diff;
    }

    private static string Listing(Dictionary<string, int> bag) =>
        string.Join(", ", bag.Take(25).Select(kv => kv.Value > 1 ? $"{kv.Key} x{kv.Value}" : kv.Key)) + (bag.Count > 25 ? ", ..." : "");
}
