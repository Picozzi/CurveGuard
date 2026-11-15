using System.Text.Json;
using System.Text.Json.Serialization;

namespace CurveGuard;

internal sealed record Report(
    int SchemaVersion,
    string CurveId,
    DateTimeOffset? AsOf,
    string ValuationDate,
    string QuantlibVersion,
    object Inputs,
    object Conventions,
    object Policy,
    string Decision,
    string[] BlockingChecks,
    IReadOnlyList<Dictionary<string, object?>> Checks,
    CurveReport? Curve,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Mode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CurveCoverage = null);

internal static class Guard
{
    private const int MaxQuoteAgeSeconds = 60 * 60;
    private const decimal MaxSourceDeviationBp = 3m;
    private const double MaxRepricingResidualBp = 0.01;

    public static async Task<Report> EvaluateFiles(string candidatePath, string referencePath, DateTimeOffset asOf)
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var valuationDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(asOf, newYork).DateTime);
        var (candidate, candidateCheck) = await ReadInput("candidate", () => Task.FromResult(MarketData.Load(candidatePath)));
        var (reference, referenceCheck) = await ReadInput("reference", () => Task.FromResult(MarketData.Load(referencePath)));
        return Evaluate(valuationDate, asOf, candidate, reference,
        [
            candidateCheck,
            referenceCheck,
            Freshness("candidate", candidate, asOf),
            Freshness("reference", reference, asOf),
            .. CompareSources(candidate, reference),
        ]);
    }

    public static async Task<Report> ReplayMarket(DateOnly valuationDate, DataRetrieval retrieval, Uri url)
    {
        var (candidate, candidateCheck) = await ReadInput("candidate", async () =>
            MarketData.ForReplay(await retrieval.GetOisSwapRatesAsync(url, valuationDate)));
        const string noReference = "this replay has no independent reference feed";
        var report = Evaluate(valuationDate, null, candidate, null,
        [
            candidateCheck,
            Skip("reference_integrity", noReference),
            Skip("candidate_freshness", "the archive supplies dates without per-quote observation times"),
            Skip("reference_freshness", noReference),
            .. CompareSources(candidate, null),
        ]);
        return report with { Mode = "market_replay", CurveCoverage = "1Y–30Y OIS par rates" };
    }

    private static Report Evaluate(DateOnly valuationDate, DateTimeOffset? asOf, Snapshot? candidate,
        Snapshot? reference, List<Dictionary<string, object?>> checks)
    {
        var curve = MeasureCurve(candidate, valuationDate, checks);
        var blockers = checks.Where(check => !Equals(check["status"], "PASS"))
            .Select(check => (string)check["name"]!).ToArray();
        return new Report(1, MarketData.CurveId, asOf, MarketData.DateText(valuationDate), SofrCurve.QuantLibVersion,
            new { Candidate = candidate?.Provenance, Reference = reference?.Provenance },
            SofrCurve.Conventions,
            new { MaxQuoteAgeSeconds, MaxSourceDeviationBp, MaxRepricingResidualBp },
            blockers.Length == 0 ? "PASS" : "QUARANTINE", blockers, checks, curve);
    }

    private static async Task<(Snapshot?, Dictionary<string, object?>)> ReadInput(string label, Func<Task<Snapshot>> read)
    {
        try
        {
            var snapshot = await read();
            return (snapshot, Check($"{label}_integrity", true, ("quote_count", snapshot.Quotes.Count)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or FormatException or JsonException or HttpRequestException or OperationCanceledException)
        {
            return (null, Check($"{label}_integrity", false, ("error", error.Message)));
        }
    }

    private static Dictionary<string, object?> Freshness(string label, Snapshot? snapshot, DateTimeOffset asOf)
    {
        var name = $"{label}_freshness";
        if (snapshot is null)
            return Skip(name, $"{label} input failed integrity checks");

        var violations = new List<object>();
        foreach (var quote in snapshot.Quotes.Values)
        {
            if (quote.ObservedAt is not { } observedAt)
                return Skip(name, $"missing observation time for {quote.Id}");
            var ageSeconds = (asOf - observedAt).TotalSeconds;
            if (ageSeconds is < 0 or > MaxQuoteAgeSeconds)
            {
                violations.Add(new
                {
                    quote.Tenor, QuoteId = quote.Id, ObservedAt = observedAt, AgeSeconds = ageSeconds,
                    Reason = ageSeconds < 0 ? "future_timestamp" : "stale_quote",
                });
            }
        }

        return Check(name, violations.Count == 0, ("violations", violations));
    }

    private static IEnumerable<Dictionary<string, object?>> CompareSources(Snapshot? candidate, Snapshot? reference)
    {
        const string sourceCheck = "distinct_sources";
        const string comparisonCheck = "cross_source_deviation";
        if (candidate is null || reference is null)
        {
            const string reason = "candidate and reference must both pass integrity checks";
            return [Skip(sourceCheck, reason), Skip(comparisonCheck, reason)];
        }

        if (string.Equals(candidate.Source, reference.Source, StringComparison.OrdinalIgnoreCase))
            return [Check(sourceCheck, false), Skip(comparisonCheck, "source names match")];

        var comparisons = MarketData.Tenors.Select(tenor =>
        {
            var quote = candidate.Quotes[tenor];
            var benchmark = reference.Quotes[tenor];
            var deviation = (quote.Rate - benchmark.Rate) * 10_000m;
            return new
            {
                Tenor = tenor, CandidateQuoteId = quote.Id, ReferenceQuoteId = benchmark.Id,
                CandidateRate = quote.Rate, ReferenceRate = benchmark.Rate, DeviationBp = deviation,
                WithinTolerance = Math.Abs(deviation) <= MaxSourceDeviationBp,
            };
        }).ToArray();
        return [
            Check(sourceCheck, true),
            Check(comparisonCheck, comparisons.All(row => row.WithinTolerance), ("comparisons", comparisons)),
        ];
    }

    private static CurveReport? MeasureCurve(Snapshot? candidate, DateOnly valuationDate,
        List<Dictionary<string, object?>> checks)
    {
        if (candidate is null)
        {
            const string reason = "candidate input failed integrity checks";
            checks.AddRange([Skip("bootstrap", reason), Skip("discount_factors", reason), Skip("repricing", reason)]);
            return null;
        }

        CurveMeasurements measurements;
        try
        {
            measurements = SofrCurve.Build(candidate, valuationDate);
        }
        catch (Exception error) when (error is ApplicationException or ArgumentException or InvalidOperationException)
        {
            const string reason = "bootstrap or curve measurement failed";
            checks.AddRange([Check("bootstrap", false, ("error", error.Message)),
                Skip("discount_factors", reason), Skip("repricing", reason)]);
            return null;
        }

        checks.Add(Check("bootstrap", true));
        checks.Add(Check("discount_factors", measurements.PositiveDiscountFactors,
            ("sampled_date_count", measurements.SampledDateCount)));
        checks.Add(Check("repricing", measurements.MaxAbsoluteResidualBp is <= MaxRepricingResidualBp,
            ("max_absolute_residual_bp", measurements.MaxAbsoluteResidualBp)));
        return measurements.Curve;
    }

    private static Dictionary<string, object?> Check(string name, bool passed, params (string Key, object? Value)[] evidence)
    {
        var check = new Dictionary<string, object?> { ["name"] = name, ["status"] = passed ? "PASS" : "FAIL" };
        foreach (var (key, value) in evidence)
            check.Add(key, value);
        return check;
    }

    private static Dictionary<string, object?> Skip(string name, string reason) =>
        new() { ["name"] = name, ["status"] = "SKIPPED", ["reason"] = reason };
}
