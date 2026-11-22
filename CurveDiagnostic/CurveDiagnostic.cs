using System.Text.Json;

namespace CurveGuard;

internal sealed record CitedCheck<T>(string Id, T Check);

internal sealed record DiagnosticEvidence(string Id, DateOnly ValuationDate, string Decision, string Bootstrap,
    string? FailureCode, IReadOnlyList<CitedCheck<ValidationCheck>> InputChecks,
    IReadOnlyList<CitedCheck<PricingRiskCheck>> PricingChecks);

internal sealed record DiagnosticExplanation(string Explanation, IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<string> Unknowns, string RecommendedNextAction);

internal sealed record DiagnosticReport(ValidationResult Validation, DiagnosticEvidence Evidence,
    string DiagnosticMode, string? Model, DiagnosticExplanation? Diagnostic, string? DiagnosticError);

internal static class CurveDiagnostic
{
    //stub function for unit testing, isn't live or anything -MP
    public static Task<DiagnosticReport> AnalyzeWithStubAsync(DateOnly valuationDate, ValidationResult validation)
        => AnalyzeAsync(valuationDate, validation, "stub", null, evidence =>
            Task.FromResult(ExplainStaleFiveYearQuote(evidence)));

    public static async Task<DiagnosticReport> AnalyzeAsync(DateOnly valuationDate, ValidationResult validation,
        string mode, string? model, Func<DiagnosticEvidence, Task<DiagnosticExplanation?>> explain)
    {
        var inputChecks = validation.validationRisks
            .Select((check, index) => new CitedCheck<ValidationCheck>($"input-{index + 1}", check))
            .ToArray();
        var pricingChecks = validation.pricingRisks
            .Select((check, index) => new CitedCheck<PricingRiskCheck>($"pricing-{index + 1}", check))
            .ToArray();
        var failureCode = string.IsNullOrWhiteSpace(validation.message) ? null : validation.message.Split(' ', 2)[0];
        var bootstrap = pricingChecks.Length > 0 || failureCode == "PRICING_VALIDATION_FAILED" ? "PASS" : validation.Result;
        var evidence = new DiagnosticEvidence("guard", valuationDate, validation.Decision, bootstrap, failureCode, inputChecks, pricingChecks);
        var report = new DiagnosticReport(validation, evidence, mode, model, null, null);
        if (evidence.Decision != "QUARANTINE")
            return report;

        var failedIds = inputChecks.Select(item => item.Id)
            .Concat(pricingChecks.Where(item => !item.Check.Passed).Select(item => item.Id))
            .ToHashSet(StringComparer.Ordinal);
        if (failedIds.Count == 0 && failureCode is not null)
            failedIds.Add(evidence.Id);
        if (failedIds.Count == 0)
            return report;

        var knownIds = inputChecks.Select(item => item.Id)
            .Concat(pricingChecks.Select(item => item.Id))
            .Append(evidence.Id)
            .ToHashSet(StringComparer.Ordinal);

        try
        {
            var explanation = await explain(evidence);
            if (explanation is not null &&
                (!explanation.EvidenceIds.All(knownIds.Contains) || !explanation.EvidenceIds.Any(failedIds.Contains)))
                return report with { DiagnosticError = "The diagnostic did not cite valid failure evidence." };

            return report with { Diagnostic = explanation };
        }
        catch (HttpRequestException error)
        {
            var message = error.StatusCode is { } status
                ? $"OpenAI returned HTTP {(int)status}."
                : "The OpenAI request could not be completed.";
            return report with { DiagnosticError = message };
        }
        catch (OperationCanceledException)
        {
            return report with { DiagnosticError = "The OpenAI request timed out or was canceled." };
        }
        catch (JsonException)
        {
            return report with { DiagnosticError = "OpenAI returned a diagnostic with an invalid response format." };
        }
        catch (FormatException)
        {
            return report with { DiagnosticError = "The OpenAI request configuration was invalid." };
        }
        catch (InvalidOperationException error)
        {
            return report with { DiagnosticError = error.Message };
        }
    }

    private static DiagnosticExplanation? ExplainStaleFiveYearQuote(DiagnosticEvidence evidence)
    {
        if (evidence.Decision != "QUARANTINE" || evidence.Bootstrap != "SKIPPED")
            return null;

        var staleQuote = evidence.InputChecks.FirstOrDefault(item => item.Check is
            { Code: "STALE_OIS_QUOTE", Tenor: "5Y", QuoteDate: not null, ExpectedDate: not null });
        if (staleQuote?.Check is not { QuoteDate: { } observed, ExpectedDate: { } expected })
            return null;

        var summary = FormattableString.Invariant(
            $"The 5Y SOFR OIS quote is dated {observed:yyyy-MM-dd}. CurveGuard required {expected:yyyy-MM-dd}, quarantined the inputs, and skipped the bootstrap.");
        var nextAction = FormattableString.Invariant(
            $"Obtain a 5Y SOFR OIS quote for {expected:yyyy-MM-dd} and rerun input validation.");
        return new DiagnosticExplanation(summary, [evidence.Id, staleQuote.Id],
            ["Why the pricing data contained a stale quote.", "The PV impact, because the bootstrap was skipped."],
            nextAction);
    }
}
