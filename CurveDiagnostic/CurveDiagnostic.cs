using System.Text.Json;

namespace CurveGuard;

internal sealed record CitedValidationCheck(string Id, ValidationCheck Check);

internal sealed record DiagnosticEvidence(string Id, DateOnly ValuationDate, string Decision, string Bootstrap,
    IReadOnlyList<CitedValidationCheck> InputChecks);

internal sealed record DiagnosticExplanation(string Explanation, IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<string> Unknowns, string RecommendedNextAction);

internal sealed record DiagnosticReport(ValidationResult Validation, DiagnosticEvidence Evidence,
    string DiagnosticMode, string? Model, DiagnosticExplanation? Diagnostic, string? DiagnosticError);

internal static class CurveDiagnostic
{
    public static Task<DiagnosticReport> AnalyzeWithStubAsync(DateOnly valuationDate, ValidationResult validation)
        => AnalyzeAsync(valuationDate, validation, "stub", null, evidence =>
            Task.FromResult(ExplainStaleFiveYearQuote(evidence)));

    public static async Task<DiagnosticReport> AnalyzeAsync(DateOnly valuationDate, ValidationResult validation,
        string mode, string? model, Func<DiagnosticEvidence, Task<DiagnosticExplanation?>> explain)
    {
        var inputChecks = validation.validationRisks
            .Select((check, index) => new CitedValidationCheck($"input-{index + 1}", check))
            .ToArray();
        var evidence = new DiagnosticEvidence("guard", valuationDate, validation.Decision, validation.Result, inputChecks);
        var report = new DiagnosticReport(validation, evidence, mode, model, null, null);
        if (evidence.Decision != "QUARANTINE" || inputChecks.Length == 0)
            return report;

        try
        {
            var explanation = await explain(evidence);
            if (explanation is not null &&
                (!explanation.EvidenceIds.All(id => id == evidence.Id || inputChecks.Any(check => check.Id == id)) ||
                 !inputChecks.Any(check => explanation.EvidenceIds.Contains(check.Id))))
                return report with { DiagnosticError = "The diagnostic did not cite valid input check evidence." };

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
