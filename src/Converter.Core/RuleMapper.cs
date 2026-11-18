using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Converter.Core;

public static class Names
{
    /// <summary>"First_Name", "Account No", "claim_number" -> firstName, accountNo, claimNumber.</summary>
    public static string Normalise(string raw)
    {
        var spaced = Regex.Replace(raw, "([a-z0-9])([A-Z])", "$1 $2");
        var parts = Regex.Split(spaced, "[^A-Za-z0-9]+").Where(p => p.Length > 0).ToList();
        if (parts.Count == 0) return "value";
        var sb = new StringBuilder(parts[0].ToLowerInvariant());
        foreach (var p in parts.Skip(1))
            sb.Append(char.ToUpperInvariant(p[0])).Append(p[1..].ToLowerInvariant());
        var name = sb.ToString();
        return char.IsDigit(name[0]) ? "v" + name : name;
    }

    public static string InferType(string normalised)
    {
        var n = normalised.ToLowerInvariant();
        if (Has(n, "date", "expiry", "dob", "period")) return "date";
        if (Has(n, "amount", "premium", "balance", "fee", "excess", "levy", "payment", "price", "total", "settlement", "instalment"))
            return "currency";
        if (n.EndsWith("days") || n.EndsWith("day") || n.EndsWith("count") || Has(n, "qty", "quantity")) return "number";
        return "string";
        static bool Has(string s, params string[] keys) => keys.Any(s.Contains);
    }
}

/// <summary>
/// Deterministic first pass. It converts everything that is unambiguous in the
/// source (real fields, IF fields, repeat markers, typed placeholders) and
/// leaves every plain-English instruction as a rule marked "needs-review".
/// The pipeline still works with this mapper alone, so it also serves as the
/// offline mode and as the fallback if the model is unavailable.
/// </summary>
public sealed class RuleMapper
{
    private readonly Dictionary<string, JsonObject> _variables = new();
    private readonly JsonArray _rules = new();
    private string? _repeatScope;     // list variable name while inside a repeating row

    public JsonObject Map(SourceTemplate src)
    {
        var header = ContentOfBlocks(src.Header);
        var footer = ContentOfBlocks(src.Footer);
        var blocks = new JsonArray();
        foreach (var b in src.Body)
            foreach (var mapped in MapBlock(b)) blocks.Add(mapped);

        return new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["name"] = Path.GetFileNameWithoutExtension(src.FileName),
            ["source"] = new JsonObject { ["file"] = src.FileName, ["format"] = src.Format },
            ["page"] = new JsonObject
            {
                ["size"] = src.Page.Size, ["orientation"] = src.Page.Orientation,
                ["marginsMm"] = new JsonObject
                {
                    ["top"] = src.Page.MarginTopMm, ["right"] = src.Page.MarginRightMm,
                    ["bottom"] = src.Page.MarginBottomMm, ["left"] = src.Page.MarginLeftMm
                }
            },
            ["variables"] = new JsonArray(_variables.Values.Select(v => (JsonNode)v).ToArray()),
            ["header"] = header,
            ["footer"] = footer,
            ["blocks"] = blocks,
            ["rules"] = _rules
        };
    }

    private JsonArray ContentOfBlocks(List<Block> blocks)
    {
        var content = new JsonArray();
        foreach (var p in blocks.OfType<ParagraphBlock>())
            foreach (var item in Content(p.Inlines)) content.Add(item!.DeepClone());
        return content;
    }

    private IEnumerable<JsonObject> MapBlock(Block block)
    {
        if (block is TableBlock table) { yield return MapTable(table); yield break; }
        var p = (ParagraphBlock)block;
        if (p.PageBreakBefore) yield return new JsonObject { ["type"] = "pageBreak" };

        // An instruction that stands alone on its line is a note to the writer: record it, print nothing.
        var meaningful = p.Inlines.Where(i => !(i.Kind == InlineKind.Text && string.IsNullOrWhiteSpace(i.Text))).ToList();
        if (meaningful.Count > 0 && meaningful.All(i => i.Kind == InlineKind.Instruction))
        {
            foreach (var i in meaningful) AddInstructionRule(i.Text);
            yield break;
        }

        // A paragraph that is nothing but an IF field becomes a block-level conditional.
        if (meaningful.Count == 1 && meaningful[0].Kind == InlineKind.IfField)
        {
            var spec = meaningful[0].If!;
            var conditional = new JsonObject
            {
                ["type"] = "conditional",
                ["when"] = Condition(spec),
                ["blocks"] = new JsonArray(Para(p, spec.WhenTrue))
            };
            if (spec.WhenFalse.Any(i => !string.IsNullOrWhiteSpace(i.Text) || i.Kind != InlineKind.Text))
                conditional["else"] = new JsonArray(Para(p, spec.WhenFalse));
            yield return conditional;
            yield break;
        }

        yield return Para(p, p.Inlines);
    }

    private JsonObject Para(ParagraphBlock p, List<Inline> inlines)
    {
        var o = new JsonObject { ["type"] = "paragraph", ["style"] = p.Style };
        if (p.ListItem) o["list"] = true;
        o["content"] = Content(inlines);
        return o;
    }

    private JsonObject MapTable(TableBlock table)
    {
        var o = new JsonObject { ["type"] = "table" };
        var staticRows = new JsonArray();
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            var start = row.SelectMany(c => c).SelectMany(p => p.Inlines).FirstOrDefault(i => i.Kind == InlineKind.RepeatStart);
            if (start is not null)
            {
                var list = DeclareList(start.Name!);
                _repeatScope = list;
                o["repeat"] = new JsonObject { ["over"] = list, ["cells"] = Cells(row) };
                _repeatScope = null;
            }
            else if (r == 0 && table.Rows.Count > 1) o["header"] = Cells(row);
            else staticRows.Add(Cells(row));
        }
        if (staticRows.Count > 0) o["rows"] = staticRows;
        return o;
    }

    private JsonArray Cells(List<List<ParagraphBlock>> row)
    {
        var cells = new JsonArray();
        foreach (var cell in row)
        {
            var content = new JsonArray();
            foreach (var p in cell) foreach (var item in Content(p.Inlines)) content.Add(item!.DeepClone());
            cells.Add(content);
        }
        return cells;
    }

    private JsonArray Content(List<Inline> inlines)
    {
        var content = new JsonArray();
        foreach (var i in inlines)
        {
            switch (i.Kind)
            {
                case InlineKind.Text:
                    if (i.Text.Length == 0) break;
                    var t = new JsonObject { ["text"] = i.Text };
                    if (i.Bold) t["bold"] = true;
                    if (i.Italic) t["italic"] = true;
                    content.Add(t);
                    break;
                case InlineKind.MergeField:
                case InlineKind.Placeholder:
                    var v = new JsonObject { ["var"] = Declare(i.Name!, i.Format) };
                    if (i.Format is not null) v["format"] = i.Format;
                    content.Add(v);
                    break;
                case InlineKind.SystemField:
                    content.Add(new JsonObject
                    {
                        ["sys"] = i.Name switch { "PAGE" => "pageNumber", "NUMPAGES" or "SECTIONPAGES" => "pageCount", _ => "today" }
                    });
                    break;
                case InlineKind.IfField:
                    var when = new JsonObject { ["when"] = Condition(i.If!), ["content"] = Content(i.If!.WhenTrue) };
                    if (i.If.WhenFalse.Count > 0) when["else"] = Content(i.If.WhenFalse);
                    content.Add(when);
                    break;
                case InlineKind.Instruction:
                    AddInstructionRule(i.Text);
                    break;
                // RepeatStart / RepeatEnd are structure, not content.
            }
        }
        return content;
    }

    private void AddInstructionRule(string text) => _rules.Add(new JsonObject
    {
        ["id"] = "R" + (_rules.Count + 1),
        ["source"] = text,
        ["interpretation"] = "Instruction written for the person filling in the letter. Not interpreted by the rule-based pass.",
        ["resolution"] = "needs-review",
        ["confidence"] = 0
    });

    private JsonObject Condition(IfSpec spec)
    {
        var name = Declare(spec.Left, null);
        var op = spec.Op switch { "=" => "eq", "<>" => "ne", ">" => "gt", ">=" => "ge", "<" => "lt", "<=" => "le", _ => "eq" };
        var o = new JsonObject { ["var"] = name, ["op"] = op };
        if (double.TryParse(spec.Right, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            o["value"] = number;
            var v = _variables.TryGetValue(name, out var decl) ? decl : null;
            if (v is not null && (string?)v["type"] == "string") v["type"] = "number";
        }
        else o["value"] = spec.Right;
        return o;
    }

    private string DeclareList(string sourceName)
    {
        var name = Names.Normalise(sourceName);
        if (!_variables.ContainsKey(name))
            _variables[name] = new JsonObject
            {
                ["name"] = name, ["type"] = "list",
                ["sourceNames"] = new JsonArray(sourceName), ["fields"] = new JsonArray()
            };
        return name;
    }

    /// <summary>Declares a variable (or a list field when inside a repeating row) and returns the reference to use.</summary>
    private string Declare(string sourceName, string? format)
    {
        var name = Names.Normalise(sourceName);
        if (_repeatScope is not null)
        {
            var fields = (JsonArray)_variables[_repeatScope]["fields"]!;
            if (!fields.Any(f => (string?)f!["name"] == name))
                fields.Add(NewVariable(name, sourceName, format));
            return _repeatScope + "." + name;
        }
        if (_variables.TryGetValue(name, out var existing))
        {
            var sources = (JsonArray)existing["sourceNames"]!;
            if (!sources.Any(s => (string?)s == sourceName)) sources.Add(sourceName);
            if (format is not null && existing["format"] is null) existing["format"] = format;
        }
        else _variables[name] = NewVariable(name, sourceName, format);
        return name;
    }

    private static JsonObject NewVariable(string name, string sourceName, string? format)
    {
        var type = Names.InferType(name);
        if (format is not null && format.Contains('#') && type == "string") type = "number";
        if (format is not null && (format.Contains('y') || format.Contains('M')) && !format.Contains('#')) type = "date";
        var v = new JsonObject { ["name"] = name, ["type"] = type, ["sourceNames"] = new JsonArray(sourceName) };
        if (format is not null) v["format"] = format;
        return v;
    }
}
