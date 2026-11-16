namespace CurveGuard;

internal sealed record CurveInputs(
    DateOnly ValuationDate,
    OvernightSOFR.SOFRFixing SofrFixing,
    EFFRCalculator.EFFRFixing EffrFixing,
    OISSOFR.OISSwapSnapshot OisSnapshot,
    IReadOnlyList<FOMCCalculator.MeetingProbability> FomcDecisions,
    IReadOnlyList<FutureImpliedRate.ZqImpliedRate>? ZqImpliedRates = null);

internal sealed record ValidationFinding(string Code, string Message,
    string? Tenor = null, DateOnly? QuoteDate = null, DateOnly? ExpectedDate = null, string? Contract = null);

internal sealed record ValidationResult(string Decision, string Bootstrap, IReadOnlyList<ValidationFinding> Findings);

internal sealed record CurveRun(CurveInputs Inputs, object? Curve, ValidationResult Validation);

internal static class CurveValidation
{
    public static async Task<CurveRun> RunAsync(CurveInputs inputs,
        Func<Task<List<FOMCCalculator.MeetingProbability>>>? loadFomcDecisions = null)
    {
        var findings = CheckOisQuotes(inputs.ValuationDate, inputs.OisSnapshot);
        CheckFixings(inputs, findings);
        CheckZqCloseDates(inputs, findings);
        if (findings.Count > 0)
            return new CurveRun(inputs, null, new ValidationResult("QUARANTINE", "SKIPPED", findings));

        try
        {
            if (loadFomcDecisions is not null)
                inputs = inputs with { FomcDecisions = await loadFomcDecisions() };

            var curve = SOFRCurve.Build(inputs.ValuationDate, inputs.SofrFixing,
                inputs.OisSnapshot, inputs.EffrFixing, inputs.FomcDecisions);
            return new CurveRun(inputs, curve, new ValidationResult("PASS", "PASS", []));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or ApplicationException)
        {
            return new CurveRun(inputs, null,
                new ValidationResult("QUARANTINE", "FAIL", [new("CURVE_BUILD_FAILED", error.Message)]));
        }
    }

    private static void CheckZqCloseDates(CurveInputs inputs, List<ValidationFinding> findings)
    {
        if (inputs.ZqImpliedRates is not { Count: > 0 } quotes)
        {
            findings.Add(new("MISSING_ZQ_QUOTES", "The run requires ZQ futures quotes with observed close dates."));
            return;
        }

        foreach (var quote in quotes)
        {
            if (quote is null)
            {
                findings.Add(new("MISSING_ZQ_QUOTE", "The ZQ futures quote is missing."));
                continue;
            }

            var contract = quote.Ticker ?? $"{quote.Year:D4}-{quote.Month:D2}";
            var expectedDate = inputs.ValuationDate;
            if (quote.CloseDate is not { } closeDate)
                findings.Add(new("MISSING_ZQ_CLOSE_DATE", "The ZQ futures quote has no observed close date.",
                    ExpectedDate: expectedDate, Contract: contract));
            else if (closeDate < expectedDate)
                findings.Add(new("STALE_ZQ_CLOSE", "The ZQ futures close predates the valuation date.",
                    QuoteDate: closeDate, ExpectedDate: expectedDate, Contract: contract));
            else if (closeDate > expectedDate)
                findings.Add(new("FUTURE_ZQ_CLOSE", "The ZQ futures close follows the valuation date.",
                    QuoteDate: closeDate, ExpectedDate: expectedDate, Contract: contract));
        }
    }

    private static void CheckFixings(CurveInputs inputs, List<ValidationFinding> findings)
    {
        DateOnly expectedDate;
        try
        {
            expectedDate = SOFRCurve.PreviousFixingDate(inputs.ValuationDate);
        }
        catch (Exception error) when (error is ArgumentException or ApplicationException)
        {
            findings.Add(new("INVALID_VALUATION_DATE", error.Message));
            return;
        }

        var sofrDate = inputs.SofrFixing.EffectiveDate;
        if (sofrDate != expectedDate)
            findings.Add(new("SOFR_FIXING_DATE_MISMATCH", "The overnight SOFR fixing must be from the previous business day.",
                QuoteDate: sofrDate, ExpectedDate: expectedDate));

        var effr = inputs.EffrFixing;
        var effrDate = DateOnly.FromDateTime(effr.Date);
        if (effrDate != expectedDate)
            findings.Add(new("EFFR_FIXING_DATE_MISMATCH", "The EFFR fixing must be from the previous business day.",
                QuoteDate: effrDate, ExpectedDate: expectedDate));

        if (!FitsDecimal(effr.Rate))
            findings.Add(new("INVALID_EFFR_RATE", "The EFFR fixing must be a finite rate representable as a decimal."));

        if (!FitsDecimal(effr.TargetLower) || !FitsDecimal(effr.TargetUpper) || effr.TargetLower > effr.TargetUpper)
            findings.Add(new("INVALID_EFFR_TARGET_RANGE", "The EFFR target range must have finite, ordered bounds representable as decimals."));
    }

    private static bool FitsDecimal(double value) => Math.Abs(value) < (double)decimal.MaxValue;

    private static List<ValidationFinding> CheckOisQuotes(DateOnly valuationDate, OISSOFR.OISSwapSnapshot snapshot)
    {
        var findings = new List<ValidationFinding>();
        var snapshotDate = DateOnly.FromDateTime(snapshot.ObservationDate);
        if (snapshotDate != valuationDate)
            findings.Add(new("OIS_SNAPSHOT_DATE_MISMATCH", "The OIS snapshot date differs from the valuation date.",
                QuoteDate: snapshotDate, ExpectedDate: valuationDate));

        var tenors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var quote in snapshot.Quotes)
        {
            if (quote is not { } || string.IsNullOrWhiteSpace(quote.Tenor) || quote.Rate is null ||
                string.IsNullOrWhiteSpace(quote.Source) || string.IsNullOrWhiteSpace(quote.Method) ||
                quote.Trades is < 0 || quote.CloseTrades is < 0)
            {
                findings.Add(new("INVALID_OIS_QUOTE", "The OIS quote has missing or invalid fields.", quote?.Tenor));
                continue;
            }

            var tenor = quote.Tenor == "12M" ? "1Y" : quote.Tenor;
            if (!tenors.Add(tenor))
                findings.Add(new("DUPLICATE_OIS_TENOR", "More than one OIS quote was supplied for the same tenor.", tenor));

            if (quote.Date is not { } quoteDate)
            {
                findings.Add(new("MISSING_OIS_QUOTE_DATE", "The OIS quote has no observation date.", tenor,
                    ExpectedDate: valuationDate));
                continue;
            }

            var observed = DateOnly.FromDateTime(quoteDate);
            if (observed < valuationDate)
                findings.Add(new("STALE_OIS_QUOTE", "The OIS quote predates the valuation date.", tenor, observed, valuationDate));
            else if (observed > valuationDate)
                findings.Add(new("FUTURE_OIS_QUOTE", "The OIS quote follows the valuation date.", tenor, observed, valuationDate));
        }

        if (!tenors.Contains("1Y"))
            findings.Add(new("MISSING_1Y_OIS_QUOTE", "The one-year OIS anchor is required to calibrate the FOMC path.", "1Y"));

        return findings;
    }
}
