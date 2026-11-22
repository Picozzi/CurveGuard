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
        var evidence = new DiagnosticEvidence("guard", valuationDate, validation.Decision, validation.BootstrapResult,
            failureCode, inputChecks, pricingChecks);
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
}
