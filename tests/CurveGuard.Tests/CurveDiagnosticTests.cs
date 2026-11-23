using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace CurveGuard.Tests;

public class CurveDiagnosticTests
{
    [Fact]
    public async Task CleanInputValidationReturnsNoDiagnosticExplanation()
    {
        var inputs = await SavedCurveFixtures.ReadInputsAsync(SavedCurveFixtures.Baseline);
        var validation = await CurveInputValidation.RunInputValidation(inputs);

        var report = await CurveDiagnostic.AnalyzeAsync(inputs.ValuationDate, validation, "openai", "test-model",
            _ => throw new InvalidOperationException("Clean inputs must not call the diagnostic provider."));

        Assert.Same(validation, report.Validation);
        Assert.Equal("PASS", report.Evidence.Decision);
        Assert.Empty(report.Evidence.InputChecks);
        Assert.Null(report.Diagnostic);
        Assert.Null(report.DiagnosticError);
    }

    [Theory]
    [InlineData(true, "input-1")]
    [InlineData(false, "guard", "input-1")]
    [InlineData(false, "guard")]
    [InlineData(false, "unknown-check")]
    public async Task OpenAiDiagnosticAcceptsOnlyKnownInputCitations(bool accepted, params string[] citations)
    {
        var inputs = await SavedCurveFixtures.ReadInputsAsync(SavedCurveFixtures.StaleFiveYear);
        var validation = await CurveInputValidation.RunInputValidation(inputs);
        var reply = JsonSerializer.Serialize(new
        {
            explanation = "The observed 5Y quote date precedes the valuation date.",
            evidence_ids = citations,
            unknowns = new[] { "PV impact." },
            recommended_next_action = "Obtain a correctly dated quote.",
        });

        using var client = new HttpClient(new DiagnosticHandler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer test-key", request.Headers.Authorization?.ToString());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("test-model", body.RootElement.GetProperty("model").GetString());
            Assert.False(body.RootElement.GetProperty("store").GetBoolean());
            Assert.True(body.RootElement.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
            var input = body.RootElement.GetProperty("input").GetString()!;
            Assert.DoesNotContain("stale-5y", input);
            using var evidence = JsonDocument.Parse(input);
            Assert.Equal("QUARANTINE", evidence.RootElement.GetProperty("decision").GetString());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    status = "completed",
                    output = new object[]
                    {
                        new { type = "reasoning" },
                        new { type = "message", content = new[] { new { type = "output_text", text = reply } } },
                    },
                }),
            };
        }));
        var provider = new OpenAiCurveDiagnostic(client, "test-key", "test-model");

        var report = await CurveDiagnostic.AnalyzeAsync(inputs.ValuationDate, validation, "openai", "test-model", provider.ExplainAsync);

        Assert.Same(validation, report.Validation);
        if (accepted)
        {
            Assert.Null(report.DiagnosticError);
            Assert.Equal(citations, Assert.IsType<DiagnosticExplanation>(report.Diagnostic).EvidenceIds.ToArray());
        }
        else
        {
            Assert.Null(report.Diagnostic);
            Assert.Contains("valid failure evidence", report.DiagnosticError);
        }
    }

    [Fact]
    public async Task OpenAiRequestFailurePreservesOriginalGuardReport()
    {
        var inputs = await SavedCurveFixtures.ReadInputsAsync(SavedCurveFixtures.StaleFiveYear);
        var validation = await CurveInputValidation.RunInputValidation(inputs);
        using var client = new HttpClient(new DiagnosticHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
        var provider = new OpenAiCurveDiagnostic(client, "test-key", "test-model");

        var report = await CurveDiagnostic.AnalyzeAsync(inputs.ValuationDate, validation, "openai", "test-model", provider.ExplainAsync);

        Assert.Same(validation, report.Validation);
        Assert.Equal("QUARANTINE", report.Evidence.Decision);
        Assert.Null(report.Diagnostic);
        Assert.Equal("OpenAI returned HTTP 401.", report.DiagnosticError);
    }

    [Theory]
    [InlineData(true, "pricing-1")]
    [InlineData(true, "pricing-1", "pricing-2")]
    [InlineData(false, "pricing-2")]
    [InlineData(false, "guard", "pricing-1")]
    public async Task PricingQuarantineRequiresCitationOfAFailedCheck(bool accepted, params string[] citations)
    {
        var date = new DateOnly(2026, 11, 2);
        var validation = new ValidationResult("QUARANTINE", "PASS", "PRICING_VALIDATION_FAILED One or more OIS pricing checks failed.", [],
            [new("pv_change_usd", 1, 890.51, 445.25, 8.92, false), new("ten_bp_to_one_bp_ratio", null, 10, 10, 0.5, true)]);
        var report = await CurveDiagnostic.AnalyzeAsync(date, validation, "openai", "test-model", evidence =>
        {
            Assert.Equal("PASS", evidence.Bootstrap);
            Assert.Empty(evidence.InputChecks);
            Assert.Equal(890.51, evidence.PricingChecks[0].Check.Actual);
            Assert.Equal(445.25, evidence.PricingChecks[0].Check.Expected);
            return Task.FromResult<DiagnosticExplanation?>(new DiagnosticExplanation(
                "A pricing check failed.", citations, ["Cause of the mismatch."], "Review the PV calculation."));
        });

        Assert.Same(validation, report.Validation);
        Assert.Equal(accepted, report.Diagnostic is not null);
        Assert.Equal(accepted, report.DiagnosticError is null);
    }

    [Fact]
    public async Task BootstrapFailureUsesTheGuardCodeAsEvidence()
    {
        var date = new DateOnly(2026, 11, 2);
        var validation = new ValidationResult("QUARANTINE", "FAIL", "CURVE_BUILD_FAILED Internal details", [], []);
        var report = await CurveDiagnostic.AnalyzeAsync(date, validation, "openai", "test-model", evidence =>
        {
            Assert.Equal("FAIL", evidence.Bootstrap);
            Assert.Equal("CURVE_BUILD_FAILED", evidence.FailureCode);
            return Task.FromResult<DiagnosticExplanation?>(new DiagnosticExplanation(
                "Curve building failed.", [evidence.Id], ["Cause of the failure."], "Review the saved guard details."));
        });

        Assert.Same(validation, report.Validation);
        Assert.NotNull(report.Diagnostic);
        Assert.Null(report.DiagnosticError);
    }

    private sealed class DiagnosticHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
