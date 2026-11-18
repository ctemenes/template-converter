using System.Text.Json.Nodes;

namespace Converter.Core;

// ---------------------------------------------------------------------------
// Intermediate representation of a legacy template.
// The reader turns a source file into this. Nothing in here is guessed:
// it only records what is physically in the file.
// ---------------------------------------------------------------------------

public enum InlineKind
{
    Text,          // literal words
    MergeField,    // a real Word MERGEFIELD
    SystemField,   // PAGE, NUMPAGES, DATE and similar
    IfField,       // a real Word IF field
    RepeatStart,   // MERGEFIELD TableStart:Name
    RepeatEnd,     // MERGEFIELD TableEnd:Name
    Placeholder,   // hand-typed, e.g. <<Name>>, {{name}}, [Name]
    Instruction    // hand-typed note to the letter writer, e.g. [Delete if ...]
}

public sealed class Inline
{
    public InlineKind Kind { get; init; }
    public string Text { get; init; } = "";       // literal text, or the raw source for anything else
    public string? Name { get; init; }            // field or placeholder name as written in the source
    public string? Convention { get; init; }      // MERGEFIELD, <<>>, «», {{}}, []
    public string? Format { get; init; }          // Word format switch, e.g. d MMMM yyyy
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public IfSpec? If { get; init; }
}

public sealed class IfSpec
{
    public string Left { get; init; } = "";
    public string Op { get; init; } = "=";
    public string Right { get; init; } = "";
    public List<Inline> WhenTrue { get; init; } = new();
    public List<Inline> WhenFalse { get; init; } = new();
}

public abstract class Block { }

public sealed class ParagraphBlock : Block
{
    public string Style { get; init; } = "Normal";
    public bool ListItem { get; init; }
    public bool PageBreakBefore { get; init; }
    public List<Inline> Inlines { get; init; } = new();
}

public sealed class TableBlock : Block
{
    // rows -> cells -> paragraphs
    public List<List<List<ParagraphBlock>>> Rows { get; init; } = new();
}

public sealed class PageSetup
{
    public string Size { get; init; } = "A4";
    public string Orientation { get; init; } = "portrait";
    public double MarginTopMm { get; init; }
    public double MarginRightMm { get; init; }
    public double MarginBottomMm { get; init; }
    public double MarginLeftMm { get; init; }
}

public sealed class StyleInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? BasedOn { get; init; }
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public double? SizePt { get; init; }
}

public sealed class SourceTemplate
{
    public string FileName { get; init; } = "";
    public string Format { get; init; } = "docx";
    public PageSetup Page { get; init; } = new();
    public List<StyleInfo> Styles { get; init; } = new();
    public List<Block> Header { get; init; } = new();
    public List<Block> Body { get; init; } = new();
    public List<Block> Footer { get; init; } = new();
    public List<string> ReaderWarnings { get; init; } = new();

    public IEnumerable<ParagraphBlock> AllParagraphs()
    {
        foreach (var b in Header.Concat(Body).Concat(Footer))
        {
            if (b is ParagraphBlock p) yield return p;
            else if (b is TableBlock t)
                foreach (var row in t.Rows) foreach (var cell in row) foreach (var cp in cell) yield return cp;
        }
    }

    /// <summary>Every inline in the file, including the ones inside IF branches.</summary>
    public IEnumerable<Inline> AllInlines()
    {
        foreach (var p in AllParagraphs())
            foreach (var i in Flatten(p.Inlines)) yield return i;
    }

    public static IEnumerable<Inline> Flatten(IEnumerable<Inline> inlines)
    {
        foreach (var i in inlines)
        {
            yield return i;
            if (i.If is null) continue;
            foreach (var x in Flatten(i.If.WhenTrue)) yield return x;
            foreach (var x in Flatten(i.If.WhenFalse)) yield return x;
        }
    }

    // ---- JSON view of the source, used for the LLM prompt and the UI -------

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["file"] = FileName,
            ["format"] = Format,
            ["page"] = new JsonObject
            {
                ["size"] = Page.Size, ["orientation"] = Page.Orientation,
                ["marginsMm"] = new JsonObject
                {
                    ["top"] = Page.MarginTopMm, ["right"] = Page.MarginRightMm,
                    ["bottom"] = Page.MarginBottomMm, ["left"] = Page.MarginLeftMm
                }
            },
            ["styles"] = new JsonArray(Styles.Select(s => (JsonNode)new JsonObject
            {
                ["id"] = s.Id, ["name"] = s.Name, ["basedOn"] = s.BasedOn,
                ["bold"] = s.Bold, ["italic"] = s.Italic, ["sizePt"] = s.SizePt
            }).ToArray()),
            ["header"] = BlocksJson(Header),
            ["body"] = BlocksJson(Body),
            ["footer"] = BlocksJson(Footer)
        };
    }

    private static JsonArray BlocksJson(List<Block> blocks)
    {
        var arr = new JsonArray();
        var n = 0;
        foreach (var b in blocks)
        {
            n++;
            if (b is ParagraphBlock p) arr.Add(ParaJson(p, n));
            else if (b is TableBlock t)
            {
                var rows = new JsonArray();
                foreach (var row in t.Rows)
                {
                    var cells = new JsonArray();
                    foreach (var cell in row)
                        cells.Add(new JsonArray(cell.Select(cp => (JsonNode)ParaJson(cp, null)).ToArray()));
                    rows.Add(cells);
                }
                arr.Add(new JsonObject { ["n"] = n, ["type"] = "table", ["rows"] = rows });
            }
        }
        return arr;
    }

    private static JsonObject ParaJson(ParagraphBlock p, int? n)
    {
        var o = new JsonObject();
        if (n is not null) o["n"] = n;
        o["type"] = "paragraph";
        o["style"] = p.Style;
        if (p.ListItem) o["listItem"] = true;
        if (p.PageBreakBefore) o["pageBreakBefore"] = true;
        o["inlines"] = InlinesJson(p.Inlines);
        return o;
    }

    public static JsonArray InlinesJson(IEnumerable<Inline> inlines)
    {
        var arr = new JsonArray();
        foreach (var i in inlines)
        {
            var o = new JsonObject();
            switch (i.Kind)
            {
                case InlineKind.Text:
                    o["text"] = i.Text;
                    if (i.Bold) o["bold"] = true;
                    if (i.Italic) o["italic"] = true;
                    break;
                case InlineKind.MergeField:
                    o["mergeField"] = i.Name;
                    if (i.Format is not null) o["format"] = i.Format;
                    break;
                case InlineKind.SystemField: o["systemField"] = i.Name; break;
                case InlineKind.RepeatStart: o["repeatStart"] = i.Name; break;
                case InlineKind.RepeatEnd: o["repeatEnd"] = i.Name; break;
                case InlineKind.Placeholder:
                    o["placeholder"] = i.Name; o["raw"] = i.Text; o["convention"] = i.Convention; break;
                case InlineKind.Instruction: o["instruction"] = i.Text; break;
                case InlineKind.IfField:
                    o["ifField"] = new JsonObject
                    {
                        ["left"] = i.If!.Left, ["op"] = i.If.Op, ["right"] = i.If.Right,
                        ["whenTrue"] = InlinesJson(i.If.WhenTrue), ["whenFalse"] = InlinesJson(i.If.WhenFalse)
                    };
                    break;
            }
            arr.Add(o);
        }
        return arr;
    }
}

// ---------------------------------------------------------------------------
// Results
// ---------------------------------------------------------------------------

public enum Severity { Error, Warning, Info }

public sealed record Finding(Severity Severity, string Check, string Message);

public sealed class ValidationResult
{
    public List<Finding> Findings { get; } = new();
    public double WordRecall { get; set; } = 1;       // share of source words kept
    public double WordPrecision { get; set; } = 1;    // share of output words that came from the source
    public double FieldCoverage { get; set; } = 1;    // share of source fields bound to a variable
    public double LogicCoverage { get; set; } = 1;    // share of IF fields, repeats and instructions accounted for
    public int NeedsReview { get; set; }
    public IEnumerable<Finding> Errors => Findings.Where(f => f.Severity == Severity.Error);
    public IEnumerable<Finding> Warnings => Findings.Where(f => f.Severity == Severity.Warning);
    public bool Passed => !Errors.Any();
    public double Fidelity => Math.Round(100 * (0.4 * WordRecall + 0.2 * WordPrecision + 0.2 * FieldCoverage + 0.2 * LogicCoverage), 1);
}

public sealed class Attempt
{
    public int Number { get; init; }
    public string Mapper { get; init; } = "";
    public int Errors { get; init; }
    public int Warnings { get; init; }
    public double Fidelity { get; init; }
    public long Millis { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public List<string> ErrorMessages { get; init; } = new();
}

public sealed class RenderVariant
{
    public string Name { get; init; } = "";
    public JsonObject Data { get; init; } = new();
    public string Html { get; init; } = "";
}

public sealed class ConversionResult
{
    public string File { get; init; } = "";
    public string Status { get; set; } = "";          // Converted | Needs review | Failed checks | Unreadable
    public string Mapper { get; set; } = "";
    public double Fidelity { get; set; }
    public SourceTemplate? Source { get; init; }
    public JsonObject? Template { get; set; }
    public ValidationResult? Validation { get; set; }
    public List<Attempt> Attempts { get; } = new();
    public List<RenderVariant> Renders { get; set; } = new();
    public string? Error { get; set; }
    public long Millis { get; set; }

    public JsonObject ToJson(bool includeSource = true)
    {
        var o = new JsonObject
        {
            ["file"] = File, ["status"] = Status, ["mapper"] = Mapper, ["fidelity"] = Fidelity,
            ["millis"] = Millis, ["error"] = Error
        };
        if (Validation is not null)
        {
            o["scores"] = new JsonObject
            {
                ["wordsKept"] = Math.Round(Validation.WordRecall, 4),
                ["wordsNotInvented"] = Math.Round(Validation.WordPrecision, 4),
                ["fieldsBound"] = Math.Round(Validation.FieldCoverage, 4),
                ["logicAccountedFor"] = Math.Round(Validation.LogicCoverage, 4),
                ["needsReview"] = Validation.NeedsReview
            };
            o["findings"] = new JsonArray(Validation.Findings.Select(f => (JsonNode)new JsonObject
            {
                ["severity"] = f.Severity.ToString().ToLowerInvariant(), ["check"] = f.Check, ["message"] = f.Message
            }).ToArray());
        }
        o["attempts"] = new JsonArray(Attempts.Select(a => (JsonNode)new JsonObject
        {
            ["number"] = a.Number, ["mapper"] = a.Mapper, ["errors"] = a.Errors, ["warnings"] = a.Warnings,
            ["fidelity"] = a.Fidelity, ["millis"] = a.Millis, ["inputTokens"] = a.InputTokens,
            ["outputTokens"] = a.OutputTokens,
            ["errorMessages"] = new JsonArray(a.ErrorMessages.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray())
        }).ToArray());
        if (Template is not null)
        {
            o["counts"] = new JsonObject
            {
                ["variables"] = (Template["variables"] as JsonArray)?.Count ?? 0,
                ["rules"] = (Template["rules"] as JsonArray)?.Count ?? 0,
                ["blocks"] = (Template["blocks"] as JsonArray)?.Count ?? 0
            };
            o["template"] = Template.DeepClone();
        }
        o["renders"] = new JsonArray(Renders.Select(r => (JsonNode)new JsonObject
        {
            ["name"] = r.Name, ["data"] = r.Data.DeepClone(), ["html"] = r.Html
        }).ToArray());
        if (includeSource && Source is not null) o["source"] = Source.ToJson();
        return o;
    }
}
