using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Converter.Core;

/// <summary>
/// One template in, one result out:
/// read -> deterministic draft -> model mapping -> validate -> repair loop -> render -> report.
/// </summary>
public sealed class ConversionPipeline
{
    private readonly ITemplateMapper? _llm;
    private readonly int _maxAttempts;

    public ConversionPipeline(ITemplateMapper? llm = null, int maxAttempts = 3)
    {
        _llm = llm;
        _maxAttempts = Math.Max(1, maxAttempts);
    }

    public string Mode => _llm?.Name ?? "rules";

    public async Task<ConversionResult> ConvertAsync(string fileName, byte[] bytes, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        SourceTemplate source;
        try { source = DocxReader.Read(fileName, bytes); }
        catch (Exception ex)
        {
            return new ConversionResult { File = fileName, Status = "Unreadable", Error = ex.Message, Millis = clock.ElapsedMilliseconds };
        }

        var result = new ConversionResult { File = fileName, Source = source };

        // Attempt 0: the deterministic pass. Always produced, always validated.
        var stepClock = Stopwatch.StartNew();
        var draft = new RuleMapper().Map(source);
        var best = draft;
        var bestValidation = Validator.Validate(source, draft);
        var bestMapper = "rules";
        result.Attempts.Add(Log(0, "rules", bestValidation, stepClock.ElapsedMilliseconds, null, null));

        if (_llm is not null)
        {
            JsonObject? previous = null;
            var errors = new List<string>();
            for (var attempt = 1; attempt <= _maxAttempts; attempt++)
            {
                stepClock.Restart();
                try
                {
                    var mapped = await _llm.MapAsync(source, draft, previous, errors, ct);
                    var validation = Validator.Validate(source, mapped.Template);
                    result.Attempts.Add(Log(attempt, _llm.Name, validation, stepClock.ElapsedMilliseconds, mapped.InputTokens, mapped.OutputTokens));

                    if (Better(validation, bestValidation, bestMapper))
                    {
                        best = mapped.Template; bestValidation = validation; bestMapper = _llm.Name;
                    }
                    if (validation.Passed) break;
                    previous = mapped.Template;
                    errors = validation.Errors.Select(e => e.Message).ToList();
                }
                catch (ModelUnavailableException ex)
                {
                    // The model is down or refusing calls. Record it and keep the best result so far.
                    result.Attempts.Add(new Attempt
                    {
                        Number = attempt, Mapper = _llm.Name, Errors = 1, Millis = stepClock.ElapsedMilliseconds,
                        ErrorMessages = { ex.Message }
                    });
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Attempts.Add(new Attempt
                    {
                        Number = attempt, Mapper = _llm.Name, Errors = 1, Millis = stepClock.ElapsedMilliseconds,
                        ErrorMessages = { ex.Message }
                    });
                    // A malformed answer is repairable. Tell the model what went wrong and try again.
                    errors = new List<string> { ex.Message };
                }
            }
        }

        result.Template = best;
        result.Validation = bestValidation;
        result.Mapper = bestMapper;
        result.Fidelity = bestValidation.Fidelity;
        result.Status = !bestValidation.Passed ? "Failed checks"
            : bestValidation.NeedsReview > 0 ? "Needs review"
            : "Converted";

        if (bestValidation.Passed)
        {
            result.Renders = SampleData.Variants(best).Select(v => new RenderVariant
            {
                Name = v.Name, Data = v.Data, Html = Renderer.RenderHtml(best, v.Data)
            }).ToList();
        }
        result.Millis = clock.ElapsedMilliseconds;
        return result;
    }

    /// <summary>
    /// A model answer replaces the current best when it passes and the best does not, or when both pass and it
    /// leaves less for a person to review, or when neither passes and it has fewer errors.
    /// </summary>
    private static bool Better(ValidationResult candidate, ValidationResult best, string bestMapper)
    {
        if (candidate.Passed != best.Passed) return candidate.Passed;
        if (!candidate.Passed) return candidate.Errors.Count() < best.Errors.Count();
        if (candidate.NeedsReview != best.NeedsReview) return candidate.NeedsReview < best.NeedsReview;
        return bestMapper == "rules" || candidate.Fidelity > best.Fidelity;
    }

    private static Attempt Log(int number, string mapper, ValidationResult v, long millis, int? input, int? output) => new()
    {
        Number = number, Mapper = mapper, Errors = v.Errors.Count(), Warnings = v.Warnings.Count(),
        Fidelity = v.Fidelity, Millis = millis, InputTokens = input, OutputTokens = output,
        ErrorMessages = v.Errors.Select(e => $"[{e.Check}] {e.Message}").ToList()
    };

    /// <summary>Converts many templates with a cap on how many run at once.</summary>
    public async Task<List<ConversionResult>> ConvertBatchAsync(IEnumerable<(string name, byte[] bytes)> files,
        int concurrency = 4, CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = files.Select(async f =>
        {
            await gate.WaitAsync(ct);
            try { return await ConvertAsync(f.name, f.bytes, ct); }
            finally { gate.Release(); }
        }).ToList();
        return (await Task.WhenAll(tasks)).ToList();
    }
}
