using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Converter.Core;

public sealed record MapResult(JsonObject Template, int? InputTokens, int? OutputTokens);

/// <summary>The model could not be reached or refused the call. Not something a repair prompt can fix.</summary>
public sealed class ModelUnavailableException(string message) : Exception(message);

public interface ITemplateMapper
{
    string Name { get; }

    /// <param name="draft">The deterministic first pass.</param>
    /// <param name="previous">The model's last answer, when this is a repair attempt.</param>
    /// <param name="errors">Validator errors for <paramref name="previous"/>.</param>
    Task<MapResult> MapAsync(SourceTemplate source, JsonObject draft, JsonObject? previous,
        IReadOnlyList<string> errors, CancellationToken ct);
}

/// <summary>
/// The model step. It gets the exact structural read of the source, the
/// deterministic draft, and (on a repair attempt) the validator's errors.
/// It does the parts rules cannot: reading plain-English instructions,
/// spotting that two differently written placeholders are the same thing,
/// and choosing sensible names and types.
/// </summary>
public sealed class LlmMapper : ITemplateMapper
{
    private const int MaxTransportAttempts = 4;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private readonly HttpClient _http;
    private readonly string _model;
    public string Name => "llm:" + _model;

    public LlmMapper(HttpClient http, string apiKey, string? model = null)
    {
        _http = http;
        _model = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-5-5" : model;
        _http.BaseAddress ??= new Uri("https://api.anthropic.com/");
        _http.DefaultRequestHeaders.Remove("x-api-key");
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Remove("anthropic-version");
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        _http.Timeout = TimeSpan.FromMinutes(3);
    }

    /// <summary>Returns a mapper if ANTHROPIC_API_KEY is set, otherwise null (rule-based mode).</summary>
    public static LlmMapper? FromEnvironment(HttpClient? http = null)
    {
        var key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        return string.IsNullOrWhiteSpace(key)
            ? null
            : new LlmMapper(http ?? new HttpClient(), key, Environment.GetEnvironmentVariable("ANTHROPIC_MODEL"));
    }

    public async Task<MapResult> MapAsync(SourceTemplate source, JsonObject draft, JsonObject? previous,
        IReadOnlyList<string> errors, CancellationToken ct)
    {
        var request = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 12000,
            ["system"] = Prompts.System,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = Prompts.User(source, draft, previous, errors) })
        };

        var payload = request.ToJsonString();
        var body = "";
        for (var attempt = 1; ; attempt++)
        {
            using var response = await _http.PostAsync("v1/messages",
                new StringContent(payload, Encoding.UTF8, "application/json"), ct);
            body = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode) break;

            // Rate limited or overloaded: wait and try again. Anything else will not get better by waiting.
            var status = (int)response.StatusCode;
            var busy = status is 429 or 500 or 502 or 503 or 529;
            if (!busy || attempt >= MaxTransportAttempts)
                throw new ModelUnavailableException($"Model call failed ({status}): {Shorten(body)}");
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
            await Task.Delay(wait > MaxBackoff ? MaxBackoff : wait, ct);
        }

        var json = JsonNode.Parse(body) ?? throw new InvalidOperationException("Empty model response.");
        var text = string.Concat((json["content"] as JsonArray ?? new JsonArray())
            .Where(c => c?["type"]?.ToString() == "text").Select(c => c!["text"]!.ToString()));
        return new MapResult(ExtractJson(text), (int?)json["usage"]?["input_tokens"], (int?)json["usage"]?["output_tokens"]);
    }

    /// <summary>Pulls the JSON object out of the model's reply, tolerating code fences or stray prose.</summary>
    public static JsonObject ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("The model did not return a JSON object.");
        try
        {
            return JsonNode.Parse(text[start..(end + 1)]) as JsonObject
                   ?? throw new InvalidOperationException("The model's JSON was not an object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The model returned invalid JSON: " + ex.Message);
        }
    }

    private static string Shorten(string s) => s.Length <= 300 ? s : s[..300] + "...";
}

public static class Prompts
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    public const string Schema = """
        PLATFORM TEMPLATE FORMAT (schemaVersion 1.0)

        {
          "schemaVersion": "1.0",
          "name": string,
          "source": { "file": string, "format": "docx" },
          "page": { "size": string, "orientation": "portrait" | "landscape", "marginsMm": { "top", "right", "bottom", "left" } },
          "variables": [ Variable ],
          "header": [ Content ],
          "footer": [ Content ],
          "blocks": [ Block ],
          "rules": [ Rule ]
        }

        Variable = { "name": lowerCamelCase, "type": "string" | "number" | "currency" | "date" | "boolean" | "list",
                     "sourceNames": [ every name this value has in the source, exactly as written there ],
                     "format"?: string, "description"?: string,
                     "fields"?: [ Variable ]   // only when type is "list"
                   }

        Content = exactly one of
          { "text": string, "bold"?: true, "italic"?: true }
          { "var": "name" or "listName.fieldName", "format"?: string }
          { "sys": "pageNumber" | "pageCount" | "today" }
          { "when": Condition, "content": [ Content ], "else"?: [ Content ] }      // conditional words inside a sentence

        Block = one of
          { "type": "paragraph", "style": string, "list"?: true, "content": [ Content ] }
          { "type": "conditional", "when": Condition, "blocks": [ Block ], "else"?: [ Block ] }   // whole paragraphs
          { "type": "table", "header"?: [ cell ], "rows"?: [ [ cell ] ], "repeat"?: { "over": listVariableName, "cells": [ cell ] } }
              where cell = [ Content ]
          { "type": "pageBreak" }

        Condition = { "var": name, "op": "eq" | "ne" | "gt" | "ge" | "lt" | "le" | "in" | "isTrue" | "isFalse" | "exists", "value"?: any }
                  | { "all": [ Condition ] } | { "any": [ Condition ] } | { "not": Condition }

        Rule = { "id": "R1", "source": the instruction text exactly as it is in the source,
                 "interpretation": one plain sentence saying what you did with it,
                 "resolution": "conditional" | "repeat" | "removed" | "needs-review",
                 "confidence": number from 0 to 1 }
        """;

    public const string System = """
        You convert legacy document templates into the Platform template format.

        You are given SOURCE, an exact structural read of the legacy file, and DRAFT, a deterministic first pass
        that has already converted the unambiguous parts. On a repair attempt you are also given your previous
        answer and the validator's errors.

        Return one JSON object: the complete Platform template. No prose and no code fences.

        Rules
        1. Wording. Copy every word of literal text from the source exactly, including spacing and punctuation.
           Never add, reword, tidy or correct text. Two exceptions, both driven by an instruction in the source:
           wording an instruction tells the writer to insert becomes real text, and labels that exist only so an
           instruction can point at a paragraph (such as "A." and "B.") are removed.
        2. Fields. Every merge field and every hand-typed placeholder becomes a variable. Put the exact source
           name in "sourceNames". If the same value is written two ways in the source, use one variable with both
           source names. Choose the type from the name and how the value is used. Keep a "format" only when the
           source gives one.
        3. IF fields become "when" conditions: inline when they sit inside a sentence, a "conditional" block when
           they cover whole paragraphs. Compare numbers as numbers.
        4. Repeating rows. A row between TableStart:X and TableEnd:X becomes a table "repeat" over a list variable
           whose sourceNames contains X. Fields in that row are fields of the list and are referenced as "list.field".
        5. Instructions are bracketed notes written for the person filling in the letter. Never print them.
           Work out what each one means and turn it into conditions. If that needs a value the source does not
           have, declare a new variable with an empty "sourceNames" and a "description" saying what it holds.
           Add one rule per instruction with "source" set to the instruction text exactly.
        6. Do not guess. If an instruction cannot be decided from the document alone, because it needs a person's
           judgement or knowledge from outside the document, leave the affected text unconditional, set the rule's
           resolution to "needs-review" and say in "interpretation" exactly what the reviewer has to decide.
        7. Keep styles, list items, page breaks, tables, header and footer from the draft unless a rule above
           requires a change. Variable names are lowerCamelCase.
        8. On a repair attempt, fix every listed error and change nothing else.

        """ + Schema;

    public static string User(SourceTemplate source, JsonObject draft, JsonObject? previous, IReadOnlyList<string> errors)
    {
        var sb = new StringBuilder();
        sb.Append("SOURCE\n").Append(source.ToJson().ToJsonString(Compact)).Append("\n\n");
        sb.Append("DRAFT\n").Append(draft.ToJsonString(Compact)).Append("\n\n");
        if (previous is not null)
        {
            sb.Append("YOUR PREVIOUS ANSWER\n").Append(previous.ToJsonString(Compact)).Append("\n\n");
            sb.Append("VALIDATOR ERRORS\n");
            foreach (var e in errors) sb.Append("- ").Append(e).Append('\n');
            sb.Append("\nReturn the corrected template as one JSON object.");
        }
        else
        {
            if (errors.Count > 0)
            {
                sb.Append("YOUR PREVIOUS ANSWER COULD NOT BE USED\n");
                foreach (var e in errors) sb.Append("- ").Append(e).Append('\n');
                sb.Append('\n');
            }
            sb.Append("Return the Platform template as one JSON object.");
        }
        return sb.ToString();
    }
}
