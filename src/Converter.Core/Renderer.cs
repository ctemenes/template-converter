using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Converter.Core;

/// <summary>
/// Renders a Platform template with data. It exists so a conversion can be
/// proved: if the converted template cannot produce a document from sample
/// data, down both branches of every condition, it has not been converted.
/// </summary>
public static class Renderer
{
    public const string Missing = "⟦missing⟧";
    private static readonly CultureInfo Au = CultureInfo.GetCultureInfo("en-AU");

    public static string RenderHtml(JsonObject template, JsonObject data)
    {
        template = WithDefaultFormats(template);
        var sb = new StringBuilder();
        var header = Inline(template["header"] as JsonArray, data, null);
        if (header.Length > 0) sb.Append("<div class=\"hdr\">").Append(header).Append("</div>");
        Blocks(template["blocks"] as JsonArray, data, sb);
        var footer = Inline(template["footer"] as JsonArray, data, null);
        if (footer.Length > 0) sb.Append("<div class=\"ftr\">").Append(footer).Append("</div>");
        return sb.ToString();
    }

    public static string RenderText(JsonObject template, JsonObject data)
    {
        var html = RenderHtml(template, data);
        html = Regex.Replace(html, @"</(p|h1|h2|li|tr|div)>|<hr[^>]*>", "\n");
        html = Regex.Replace(html, @"</t[dh]>", " | ");
        html = Regex.Replace(html, @"<li>", "- ");
        html = Regex.Replace(html, @"<[^>]+>", "");
        return WebUtility.HtmlDecode(Regex.Replace(html, @"\n{2,}", "\n")).Trim();
    }

    /// <summary>
    /// A variable printed without a format falls back to its declared type: currency to two decimals,
    /// dates to a long date. Works on a copy, so the stored template is never changed by rendering it.
    /// </summary>
    private static JsonObject WithDefaultFormats(JsonObject template)
    {
        var defaults = new Dictionary<string, string>();
        foreach (var v in (template["variables"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            Note(v["name"]?.ToString(), v);
            foreach (var f in (v["fields"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                Note(v["name"] + "." + f["name"], f);
        }
        if (defaults.Count == 0) return template;
        var copy = (JsonObject)template.DeepClone();
        Apply(copy);
        return copy;

        void Note(string? reference, JsonObject declaration)
        {
            if (reference is null) return;
            var format = declaration["format"]?.ToString() ?? declaration["type"]?.ToString() switch
            {
                "currency" => "#,##0.00",
                "date" => "d MMMM yyyy",
                _ => null
            };
            if (format is not null) defaults[reference] = format;
        }

        void Apply(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var x in array) Apply(x); return; }
            if (node is not JsonObject o) return;
            // A content item has "var" and no "op". An object with both is a condition.
            if (o["var"] is not null && o["op"] is null && o["format"] is null
                && defaults.TryGetValue(o["var"]!.ToString(), out var format))
                o["format"] = format;
            foreach (var (_, child) in o.ToList()) Apply(child);
        }
    }

    private static void Blocks(JsonArray? blocks, JsonObject data, StringBuilder sb)
    {
        if (blocks is null) return;
        var inList = false;
        foreach (var node in blocks)
        {
            if (node is not JsonObject b) continue;
            var isListItem = b["type"]?.ToString() == "paragraph" && b["list"]?.GetValue<bool>() == true;
            if (inList && !isListItem) { sb.Append("</ul>"); inList = false; }
            switch (b["type"]?.ToString())
            {
                case "paragraph":
                    var content = Inline(b["content"] as JsonArray, data, null);
                    if (isListItem)
                    {
                        if (!inList) { sb.Append("<ul>"); inList = true; }
                        sb.Append("<li>").Append(content).Append("</li>");
                        break;
                    }
                    var style = b["style"]?.ToString() ?? "Normal";
                    var tag = style.StartsWith("Heading1", StringComparison.OrdinalIgnoreCase) ? "h1"
                        : style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) ? "h2" : "p";
                    sb.Append('<').Append(tag).Append(" class=\"s-").Append(WebUtility.HtmlEncode(style)).Append("\">")
                      .Append(content).Append("</").Append(tag).Append('>');
                    break;
                case "conditional":
                    Blocks(Evaluate(b["when"], data, null) ? b["blocks"] as JsonArray : b["else"] as JsonArray, data, sb);
                    break;
                case "table":
                    sb.Append("<table>");
                    if (b["header"] is JsonArray headerCells) Row(headerCells, data, null, sb, "th");
                    foreach (var row in b["rows"] as JsonArray ?? new JsonArray())
                        if (row is JsonArray cells) Row(cells, data, null, sb, "td");
                    if (b["repeat"] is JsonObject repeat)
                    {
                        var over = repeat["over"]?.ToString() ?? "";
                        foreach (var item in data[over] as JsonArray ?? new JsonArray())
                            if (repeat["cells"] is JsonArray repeatCells)
                                Row(repeatCells, data, (over, item as JsonObject ?? new JsonObject()), sb, "td");
                    }
                    sb.Append("</table>");
                    break;
                case "pageBreak":
                    sb.Append("<hr class=\"pagebreak\">");
                    break;
            }
        }
        if (inList) sb.Append("</ul>");
    }

    private static void Row(JsonArray cells, JsonObject data, (string list, JsonObject item)? scope, StringBuilder sb, string tag)
    {
        sb.Append("<tr>");
        foreach (var cell in cells)
            sb.Append('<').Append(tag).Append('>').Append(Inline(cell as JsonArray, data, scope)).Append("</").Append(tag).Append('>');
        sb.Append("</tr>");
    }

    private static string Inline(JsonArray? content, JsonObject data, (string list, JsonObject item)? scope)
    {
        if (content is null) return "";
        var sb = new StringBuilder();
        foreach (var node in content)
        {
            if (node is not JsonObject c) continue;
            if (c["text"] is not null)
            {
                var text = WebUtility.HtmlEncode(c["text"]!.ToString());
                if (c["bold"]?.GetValue<bool>() == true) text = "<strong>" + text + "</strong>";
                if (c["italic"]?.GetValue<bool>() == true) text = "<em>" + text + "</em>";
                sb.Append(text);
            }
            else if (c["var"] is not null)
            {
                var value = Lookup(c["var"]!.ToString(), data, scope);
                sb.Append("<span class=\"v\">")
                  .Append(value is null ? Missing : WebUtility.HtmlEncode(Format(value, c["format"]?.ToString())))
                  .Append("</span>");
            }
            else if (c["sys"] is not null)
            {
                sb.Append(c["sys"]!.ToString() switch { "pageNumber" => "1", "pageCount" => "1", _ => DateTime.Today.ToString("d MMMM yyyy", Au) });
            }
            else if (c["when"] is not null)
            {
                sb.Append(Inline(Evaluate(c["when"], data, scope) ? c["content"] as JsonArray : c["else"] as JsonArray, data, scope));
            }
        }
        return sb.ToString();
    }

    private static JsonNode? Lookup(string reference, JsonObject data, (string list, JsonObject item)? scope)
    {
        var dot = reference.IndexOf('.');
        if (dot > 0)
        {
            var list = reference[..dot];
            var field = reference[(dot + 1)..];
            if (scope is not null && scope.Value.list == list) return scope.Value.item[field];
            return (data[list] as JsonArray)?.FirstOrDefault()?[field];
        }
        return data[reference];
    }

    public static bool Evaluate(JsonNode? condition, JsonObject data, (string list, JsonObject item)? scope)
    {
        if (condition is not JsonObject c) return false;
        if (c["all"] is JsonArray all) return all.All(x => Evaluate(x, data, scope));
        if (c["any"] is JsonArray any) return any.Any(x => Evaluate(x, data, scope));
        if (c["not"] is not null) return !Evaluate(c["not"], data, scope);

        var actual = Lookup(c["var"]?.ToString() ?? "", data, scope);
        var expected = c["value"];
        switch (c["op"]?.ToString())
        {
            case "exists": return actual is not null && actual.ToString().Length > 0;
            case "isTrue": return Truthy(actual);
            case "isFalse": return !Truthy(actual);
            case "in": return expected is JsonArray options && options.Any(o => Same(actual, o));
            case "eq": return Same(actual, expected);
            case "ne": return !Same(actual, expected);
        }
        if (!TryNumber(actual, out var a) || !TryNumber(expected, out var b)) return false;
        return c["op"]?.ToString() switch { "gt" => a > b, "ge" => a >= b, "lt" => a < b, "le" => a <= b, _ => false };
    }

    private static bool Truthy(JsonNode? n) => n is not null && n.ToString().ToLowerInvariant() is "true" or "y" or "yes" or "1";

    private static bool Same(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (TryNumber(a, out var x) && TryNumber(b, out var y)) return Math.Abs(x - y) < 1e-9;
        return string.Equals(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNumber(JsonNode? n, out double value)
    {
        value = 0;
        return n is not null && double.TryParse(n.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string Format(JsonNode value, string? format)
    {
        var s = value.ToString();
        if (DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            try { return date.ToString(string.IsNullOrWhiteSpace(format) || format.Contains('#') ? "d MMMM yyyy" : format, Au); }
            catch (FormatException) { return date.ToString("d MMMM yyyy", Au); }
        }
        if (value is JsonValue && TryNumber(value, out var number) && format is not null && format.Contains('#'))
        {
            try { return number.ToString(format, Au); } catch (FormatException) { return number.ToString("N2", Au); }
        }
        return s;
    }
}

/// <summary>Builds believable sample data for a template, with one variant per side of its conditions.</summary>
public static class SampleData
{
    /// <summary>How many conditions get a render of their own. Past this, only all-true and all-false are added.</summary>
    public const int MaxSingleConditionVariants = 8;

    public static List<RenderVariant> Variants(JsonObject template)
    {
        var conditions = new List<JsonObject>();
        Collect(template["blocks"], conditions);
        Collect(template["header"], conditions);
        Collect(template["footer"], conditions);

        if (conditions.Count == 0)
            return new List<RenderVariant> { new() { Name = "Sample data", Data = Base(template) } };

        // All true, all false, and each condition true on its own with the rest false.
        // The last group is what reaches an "else" branch that has another condition inside it.
        var variants = new List<(string name, JsonObject data)>();
        var allTrue = Base(template);
        foreach (var c in conditions) Apply(c, allTrue, true);
        variants.Add(("All conditions true", allTrue));
        if (conditions.Count > 1)
        {
            foreach (var only in conditions.Take(MaxSingleConditionVariants))
            {
                var data = Base(template);
                foreach (var c in conditions) Apply(c, data, false);
                Apply(only, data, true);
                variants.Add(("Only: " + Describe(only), data));
            }
        }
        var allFalse = Base(template);
        foreach (var c in conditions) Apply(c, allFalse, false);
        variants.Add(("All conditions false", allFalse));

        // Two conditions on the same variable can produce the same data twice. Keep one of each.
        variants = variants.GroupBy(v => v.data.ToJsonString()).Select(g => g.First()).ToList();
        return variants.Select(v => new RenderVariant { Name = v.name, Data = v.data }).ToList();
    }

    private static string Describe(JsonObject condition)
    {
        var op = condition["op"]?.ToString() switch
        {
            "eq" => "=", "ne" => "not", "gt" => ">", "ge" => ">=", "lt" => "<", "le" => "<=",
            "isTrue" => "is true", "isFalse" => "is false", "exists" => "has a value", var other => other ?? ""
        };
        return $"{condition["var"]} {op} {condition["value"]}".Trim();
    }

    private static void Collect(JsonNode? node, List<JsonObject> found)
    {
        if (node is JsonArray arr) { foreach (var x in arr) Collect(x, found); return; }
        if (node is not JsonObject o) return;
        if (o["var"] is not null && o["op"] is not null) { found.Add(o); return; }
        foreach (var (_, child) in o) Collect(child, found);
    }

    private static void Apply(JsonObject condition, JsonObject data, bool satisfy)
    {
        var name = condition["var"]!.ToString();
        if (name.Contains('.')) return;
        var value = condition["value"];
        var number = value is not null && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : (double?)null;
        JsonNode? set = condition["op"]!.ToString() switch
        {
            "eq" => satisfy ? value?.DeepClone() : number is not null ? number + 1 : "Other",
            "ne" => satisfy ? (number is not null ? number + 1 : "Other") : value?.DeepClone(),
            "gt" or "ge" => number is null ? null : satisfy ? number + 25 : number - 25,
            "lt" or "le" => number is null ? null : satisfy ? number - 25 : number + 25,
            "in" => satisfy ? (value as JsonArray)?.FirstOrDefault()?.DeepClone() : "Other",
            "isTrue" => satisfy,
            "isFalse" => !satisfy,
            "exists" => satisfy ? "Yes" : "",
            _ => null
        };
        if (set is not null) data[name] = set;
    }

    private static JsonObject Base(JsonObject template)
    {
        var data = new JsonObject();
        foreach (var v in (template["variables"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var name = v["name"]?.ToString() ?? "";
            if (v["type"]?.ToString() == "list")
            {
                var rows = new JsonArray();
                for (var i = 0; i < 3; i++)
                {
                    var row = new JsonObject();
                    foreach (var f in (v["fields"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                        row[f["name"]?.ToString() ?? ""] = Value(f["name"]?.ToString() ?? "", f["type"]?.ToString(), i);
                    rows.Add(row);
                }
                data[name] = rows;
            }
            else data[name] = Value(name, v["type"]?.ToString(), 0);
        }
        return data;
    }

    private static JsonNode Value(string name, string? type, int i)
    {
        var n = name.ToLowerInvariant();
        switch (type)
        {
            case "date": return new DateTime(2026, 12, 1).AddDays(i * 7 + (n.Contains("due") ? 14 : n.Contains("end") || n.Contains("expiry") ? 30 : 0)).ToString("yyyy-MM-dd");
            case "currency": return Math.Round(1250.5 / (i + 1) + (n.Contains("levy") || n.Contains("excess") || n.Contains("fee") ? -1180 : 0), 2);
            case "number": return n.Contains("day") ? 15 + i : 3 + i;
            case "boolean": return true;
        }
        if (n.Contains("firstname")) return "Alex";
        if (n.Contains("lastname") || n.Contains("surname")) return "Nguyen";
        if (n == "title") return "Ms";
        if (n.Contains("consultant") && n.Contains("phone")) return "07 3000 0000";
        if (n.Contains("consultant")) return "Sam Taylor";
        if (n.Contains("name") && (n.Contains("customer") || n.Contains("account"))) return "Alex Nguyen";
        if (n.Contains("product")) return "Home and Contents";
        if (n.Contains("plan")) return "Everyday";
        if (n.Contains("state")) return "QLD";
        if (n.Contains("postcode")) return "4000";
        if (n.Contains("suburb")) return "Brisbane";
        if (n.Contains("address")) return "12 Example Street";
        if (n.Contains("last4")) return "4821";
        if (n.Contains("phone")) return "07 3000 0000";
        if (n.Contains("reason")) return "the damage happened before the policy started";
        if (n.Contains("losstype")) return "storm damage";
        if (n.Contains("description")) return new[] { "Usage charge", "Supply charge", "Payment received" }[i % 3];
        if (n.EndsWith("number") || n.EndsWith("no") || n.EndsWith("id")) return "10023" + (4567 + i);
        if (n.Contains("renew")) return "Y";
        return "Sample " + System.Text.RegularExpressions.Regex.Replace(name, "([a-z])([A-Z])", "$1 $2").ToLowerInvariant();
    }
}
