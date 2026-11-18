using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Converter.Core;

/// <summary>
/// Reads a Word .docx template into the intermediate representation.
/// A .docx is a zip of XML parts, so this uses only the base class library:
/// no Word install and no third-party packages.
/// It understands real mail-merge fields (including IF fields with merge
/// fields nested inside them) and the hand-typed conventions people use
/// when they do not use fields.
/// </summary>
public static class DocxReader
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string MergeTokenOpen = "";   // private-use markers for a merge field nested in an IF
    private const string MergeTokenClose = "";

    public static SourceTemplate Read(string fileName, byte[] bytes)
    {
        var warnings = new List<string>();
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var doc = LoadPart(zip, "word/document.xml")
                  ?? throw new InvalidDataException("Not a Word document: word/document.xml is missing.");
        var body = doc.Root?.Element(W + "body")
                   ?? throw new InvalidDataException("The document has no body.");

        var rels = LoadRelationships(zip);
        var sect = body.Elements(W + "sectPr").LastOrDefault()
                   ?? body.Descendants(W + "sectPr").LastOrDefault();

        return new SourceTemplate
        {
            FileName = fileName,
            Page = ReadPage(sect),
            Styles = ReadStyles(zip),
            Header = ReadLinkedPart(zip, rels, sect, "headerReference", warnings),
            Body = ReadBlocks(body, warnings),
            Footer = ReadLinkedPart(zip, rels, sect, "footerReference", warnings),
            ReaderWarnings = warnings
        };
    }

    // ---- parts ------------------------------------------------------------

    private static XDocument? LoadPart(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry is null) return null;
        using var s = entry.Open();
        return XDocument.Load(s);
    }

    private static Dictionary<string, string> LoadRelationships(ZipArchive zip)
    {
        var map = new Dictionary<string, string>();
        var rels = LoadPart(zip, "word/_rels/document.xml.rels");
        if (rels?.Root is null) return map;
        foreach (var rel in rels.Root.Elements())
        {
            var id = (string?)rel.Attribute("Id");
            var target = (string?)rel.Attribute("Target");
            if (id is not null && target is not null) map[id] = target;
        }
        return map;
    }

    private static List<Block> ReadLinkedPart(ZipArchive zip, Dictionary<string, string> rels, XElement? sect,
        string referenceName, List<string> warnings)
    {
        var reference = sect?.Elements(W + referenceName)
            .FirstOrDefault(e => (string?)e.Attribute(W + "type") is "default" or null);
        var id = (string?)reference?.Attribute(R + "id");
        if (id is null || !rels.TryGetValue(id, out var target)) return new List<Block>();
        var part = LoadPart(zip, "word/" + target.TrimStart('/'));
        return part?.Root is null ? new List<Block>() : ReadBlocks(part.Root, warnings);
    }

    private static PageSetup ReadPage(XElement? sect)
    {
        var size = sect?.Element(W + "pgSz");
        var mar = sect?.Element(W + "pgMar");
        double Twips(XElement? e, string attr) => e is null ? 0 : ((double?)e.Attribute(W + attr) ?? 0);
        double Mm(double twips) => Math.Round(twips / 1440.0 * 25.4, 1);
        var w = Twips(size, "w");
        var h = Twips(size, "h");
        var landscape = (string?)size?.Attribute(W + "orient") == "landscape" || w > h;
        var shortSide = Math.Min(w, h);
        var name = shortSide switch
        {
            > 11800 and < 12000 => "A4",
            > 12100 and < 12350 => "Letter",
            0 => "A4",
            _ => $"{Mm(Math.Min(w, h))}x{Mm(Math.Max(w, h))}mm"
        };
        return new PageSetup
        {
            Size = name, Orientation = landscape ? "landscape" : "portrait",
            MarginTopMm = Mm(Twips(mar, "top")), MarginRightMm = Mm(Twips(mar, "right")),
            MarginBottomMm = Mm(Twips(mar, "bottom")), MarginLeftMm = Mm(Twips(mar, "left"))
        };
    }

    private static List<StyleInfo> ReadStyles(ZipArchive zip)
    {
        var list = new List<StyleInfo>();
        var styles = LoadPart(zip, "word/styles.xml");
        if (styles?.Root is null) return list;
        foreach (var s in styles.Root.Elements(W + "style"))
        {
            if ((string?)s.Attribute(W + "type") != "paragraph") continue;
            var rpr = s.Element(W + "rPr");
            var half = (double?)rpr?.Element(W + "sz")?.Attribute(W + "val");
            list.Add(new StyleInfo
            {
                Id = (string?)s.Attribute(W + "styleId") ?? "",
                Name = (string?)s.Element(W + "name")?.Attribute(W + "val") ?? "",
                BasedOn = (string?)s.Element(W + "basedOn")?.Attribute(W + "val"),
                Bold = IsOn(rpr?.Element(W + "b")),
                Italic = IsOn(rpr?.Element(W + "i")),
                SizePt = half is null ? null : half / 2
            });
        }
        return list;
    }

    private static bool IsOn(XElement? toggle)
    {
        if (toggle is null) return false;
        var v = (string?)toggle.Attribute(W + "val");
        return v is null || (v != "0" && v != "false");
    }

    // ---- blocks -----------------------------------------------------------

    private static List<Block> ReadBlocks(XElement container, List<string> warnings)
    {
        var blocks = new List<Block>();
        foreach (var el in container.Elements())
        {
            if (el.Name == W + "p") blocks.Add(ReadParagraph(el, warnings));
            else if (el.Name == W + "tbl")
            {
                var table = new TableBlock();
                foreach (var tr in el.Elements(W + "tr"))
                {
                    var row = new List<List<ParagraphBlock>>();
                    foreach (var tc in tr.Elements(W + "tc"))
                        row.Add(tc.Elements(W + "p").Select(p => ReadParagraph(p, warnings)).ToList());
                    table.Rows.Add(row);
                }
                blocks.Add(table);
            }
            else if (el.Name == W + "sdt")
            {
                // Content controls: read what is inside them.
                var content = el.Element(W + "sdtContent");
                if (content is not null) blocks.AddRange(ReadBlocks(content, warnings));
            }
        }
        // Empty paragraphs are layout spacing in Word. They carry no content.
        blocks.RemoveAll(b => b is ParagraphBlock { Inlines.Count: 0, PageBreakBefore: false });
        return blocks;
    }

    private sealed class FieldContext
    {
        public StringBuilder Instruction { get; } = new();
        public bool InResult { get; set; }
    }

    private static ParagraphBlock ReadParagraph(XElement p, List<string> warnings)
    {
        var ppr = p.Element(W + "pPr");
        var raw = new List<Inline>();
        var stack = new Stack<FieldContext>();
        var pageBreak = IsOn(ppr?.Element(W + "pageBreakBefore"));

        void CloseField(FieldContext ctx)
        {
            var field = ParseField(ctx.Instruction.ToString(), warnings);
            if (field is null) return;
            if (stack.Count == 0) { raw.Add(field); return; }
            var parent = stack.Peek();
            if (parent.InResult) return;                       // cached result text of the parent: ignore
            if (field.Kind == InlineKind.MergeField)           // merge field nested inside an IF instruction
                parent.Instruction.Append(MergeTokenOpen).Append(field.Name).Append(MergeTokenClose);
        }

        void Walk(XElement container)
        {
            foreach (var el in container.Elements())
            {
                if (el.Name == W + "r")
                {
                    var rpr = el.Element(W + "rPr");
                    bool bold = IsOn(rpr?.Element(W + "b")), italic = IsOn(rpr?.Element(W + "i"));
                    foreach (var c in el.Elements())
                    {
                        if (c.Name == W + "fldChar")
                        {
                            var type = (string?)c.Attribute(W + "fldCharType");
                            if (type == "begin") stack.Push(new FieldContext());
                            else if (type == "separate" && stack.Count > 0) stack.Peek().InResult = true;
                            else if (type == "end" && stack.Count > 0) CloseField(stack.Pop());
                        }
                        else if (c.Name == W + "instrText")
                        {
                            if (stack.Count > 0 && !stack.Peek().InResult) stack.Peek().Instruction.Append(c.Value);
                        }
                        else if (c.Name == W + "t")
                        {
                            if (stack.Count == 0)
                                raw.Add(new Inline { Kind = InlineKind.Text, Text = c.Value, Bold = bold, Italic = italic });
                        }
                        else if (c.Name == W + "tab")
                        {
                            if (stack.Count == 0) raw.Add(new Inline { Kind = InlineKind.Text, Text = " ", Bold = bold, Italic = italic });
                        }
                        else if (c.Name == W + "br")
                        {
                            if ((string?)c.Attribute(W + "type") == "page") pageBreak = true;
                            else if (stack.Count == 0) raw.Add(new Inline { Kind = InlineKind.Text, Text = " " });
                        }
                    }
                }
                else if (el.Name == W + "fldSimple")
                {
                    var field = ParseField((string?)el.Attribute(W + "instr") ?? "", warnings);
                    if (field is not null && stack.Count == 0) raw.Add(field);
                }
                else if (el.Name == W + "hyperlink" || el.Name == W + "smartTag" || el.Name == W + "ins")
                {
                    Walk(el);
                }
                else if (el.Name == W + "sdt")
                {
                    var content = el.Element(W + "sdtContent");
                    if (content is not null) Walk(content);
                }
            }
        }

        Walk(p);
        if (stack.Count > 0)
            warnings.Add("A field starts in one paragraph and ends in another. Its contents were not converted and need a manual check.");

        return new ParagraphBlock
        {
            Style = (string?)ppr?.Element(W + "pStyle")?.Attribute(W + "val") ?? "Normal",
            ListItem = ppr?.Element(W + "numPr") is not null,
            PageBreakBefore = pageBreak,
            Inlines = SplitTypedPlaceholders(MergeTextRuns(raw))
        };
    }

    // ---- fields -----------------------------------------------------------

    private static readonly HashSet<string> SystemFields = new(StringComparer.OrdinalIgnoreCase)
        { "PAGE", "NUMPAGES", "DATE", "TIME", "SECTIONPAGES", "FILENAME", "AUTHOR", "CREATEDATE", "PRINTDATE", "SAVEDATE" };

    internal static Inline? ParseField(string instruction, List<string> warnings)
    {
        var tokens = Tokenise(instruction);
        if (tokens.Count == 0) return null;
        var keyword = tokens[0].ToUpperInvariant();

        if (keyword == "MERGEFIELD" && tokens.Count >= 2)
        {
            var name = tokens[1];
            string? format = null;
            for (var i = 2; i < tokens.Count - 1; i++)
                if (tokens[i] is "\\@" or "\\#") format = tokens[i + 1];
            if (name.StartsWith("TableStart:", StringComparison.OrdinalIgnoreCase))
                return new Inline { Kind = InlineKind.RepeatStart, Name = name[11..], Text = instruction.Trim(), Convention = "MERGEFIELD" };
            if (name.StartsWith("TableEnd:", StringComparison.OrdinalIgnoreCase))
                return new Inline { Kind = InlineKind.RepeatEnd, Name = name[9..], Text = instruction.Trim(), Convention = "MERGEFIELD" };
            return new Inline { Kind = InlineKind.MergeField, Name = name, Format = format, Text = instruction.Trim(), Convention = "MERGEFIELD" };
        }

        if (keyword == "IF" && tokens.Count >= 5)
        {
            return new Inline
            {
                Kind = InlineKind.IfField,
                Text = Printable(instruction).Trim(),
                Convention = "IF",
                If = new IfSpec
                {
                    Left = StripMergeToken(tokens[1]),
                    Op = tokens[2],
                    Right = StripMergeToken(tokens[3]),
                    WhenTrue = BranchInlines(tokens[4]),
                    WhenFalse = tokens.Count > 5 ? BranchInlines(tokens[5]) : new List<Inline>()
                }
            };
        }

        if (SystemFields.Contains(keyword))
            return new Inline { Kind = InlineKind.SystemField, Name = keyword, Text = instruction.Trim() };

        warnings.Add($"Field type not converted: {Printable(instruction).Trim()}");
        return null;
    }

    private static string Printable(string s) => s.Replace(MergeTokenOpen, "«").Replace(MergeTokenClose, "»");

    private static string StripMergeToken(string s) => s.Replace(MergeTokenOpen, "").Replace(MergeTokenClose, "").Trim();

    /// <summary>Splits a field instruction on spaces, keeping "quoted strings" whole.</summary>
    private static List<string> Tokenise(string instruction)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        var hadQuotes = false;
        foreach (var ch in instruction)
        {
            if (ch == '"') { inQuotes = !inQuotes; hadQuotes = true; continue; }
            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (sb.Length > 0 || hadQuotes) tokens.Add(sb.ToString());
                sb.Clear(); hadQuotes = false;
                continue;
            }
            sb.Append(ch);
        }
        if (sb.Length > 0 || hadQuotes) tokens.Add(sb.ToString());
        return tokens;
    }

    private static List<Inline> BranchInlines(string branch)
    {
        var list = new List<Inline>();
        var rest = branch;
        while (rest.Length > 0)
        {
            var open = rest.IndexOf(MergeTokenOpen, StringComparison.Ordinal);
            if (open < 0) { list.Add(new Inline { Kind = InlineKind.Text, Text = rest }); break; }
            if (open > 0) list.Add(new Inline { Kind = InlineKind.Text, Text = rest[..open] });
            var close = rest.IndexOf(MergeTokenClose, open, StringComparison.Ordinal);
            if (close < 0) break;
            var name = rest[(open + 1)..close];
            list.Add(new Inline { Kind = InlineKind.MergeField, Name = name, Text = "MERGEFIELD " + name, Convention = "MERGEFIELD" });
            rest = rest[(close + 1)..];
        }
        return SplitTypedPlaceholders(list);
    }

    // ---- hand-typed placeholders -------------------------------------------

    private static List<Inline> MergeTextRuns(List<Inline> inlines)
    {
        // Word splits text into runs at arbitrary points, so <<Customer Name>> can be
        // spread over several runs. Join neighbours that share formatting first.
        var merged = new List<Inline>();
        foreach (var i in inlines)
        {
            var last = merged.Count > 0 ? merged[^1] : null;
            if (i.Kind == InlineKind.Text && last is { Kind: InlineKind.Text } && last.Bold == i.Bold && last.Italic == i.Italic)
                merged[^1] = new Inline { Kind = InlineKind.Text, Text = last.Text + i.Text, Bold = i.Bold, Italic = i.Italic };
            else merged.Add(i);
        }
        return merged;
    }

    private static readonly Regex Typed = new(
        @"<<\s*(?<a>[^<>]{1,60}?)\s*>>|«\s*(?<b>[^«»]{1,60}?)\s*»|\{\{\s*(?<c>[^{}]{1,60}?)\s*\}\}|\[(?<d>[^\[\]]{2,400})\]",
        RegexOptions.Compiled);

    private static readonly Regex InstructionStart = new(
        @"^(include|insert|delete|remove|only|if|use|optional|omit|add|note|select|choose|check|confirm|see|when|where|do not|don't)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static List<Inline> SplitTypedPlaceholders(List<Inline> inlines)
    {
        var result = new List<Inline>();
        foreach (var inline in inlines)
        {
            if (inline.Kind != InlineKind.Text) { result.Add(inline); continue; }
            var text = inline.Text;
            var pos = 0;
            foreach (Match m in Typed.Matches(text))
            {
                if (m.Index > pos) result.Add(TextLike(inline, text[pos..m.Index]));
                if (m.Groups["a"].Success) result.Add(Typed_(m.Value, m.Groups["a"].Value, "<<>>"));
                else if (m.Groups["b"].Success) result.Add(Typed_(m.Value, m.Groups["b"].Value, "«»"));
                else if (m.Groups["c"].Success) result.Add(Typed_(m.Value, m.Groups["c"].Value, "{{}}"));
                else
                {
                    var inner = m.Groups["d"].Value.Trim();
                    var words = inner.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                    var isInstruction = InstructionStart.IsMatch(inner) || words > 5;
                    result.Add(isInstruction
                        ? new Inline { Kind = InlineKind.Instruction, Text = inner, Convention = "[]" }
                        : Typed_(m.Value, inner, "[]"));
                }
                pos = m.Index + m.Length;
            }
            if (pos < text.Length) result.Add(TextLike(inline, text[pos..]));
        }
        return result;

        static Inline TextLike(Inline source, string text) =>
            new() { Kind = InlineKind.Text, Text = text, Bold = source.Bold, Italic = source.Italic };
        static Inline Typed_(string raw, string name, string convention) =>
            new() { Kind = InlineKind.Placeholder, Text = raw, Name = name.Trim(), Convention = convention };
    }
}
