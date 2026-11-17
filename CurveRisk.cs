namespace CurveGuard;

internal sealed record OisTrade(string Tenor, decimal FixedCouponPercent, double NotionalUsd)
{
    public string Side => "pay_fixed";
}

internal sealed record OisTradeValuation(string StartDate, string MaturityDate, double ModelParRatePercent, double PvUsd,
    double FixedLegAnnuity);

internal sealed record CurveBuildResult(object Report, OisTradeValuation? TradeValuation);

internal sealed record OisRiskScenario(int BumpBp, decimal MarketQuotePercent, double? ModelParRatePercent,
    double? PvUsd, double? PvChangeUsd, ValidationResult Validation, double? FixedLegAnnuity);

internal sealed record OisRiskCheck(string Name, int? BumpBp, double? Actual, double? Expected, double? Tolerance, bool Passed);

internal sealed record OisRiskReport(DateOnly ValuationDate, OisTrade Trade, string? StartDate, string? MaturityDate,
    IReadOnlyList<OisRiskScenario> Scenarios, IReadOnlyList<OisRiskCheck> Checks)
{
    public bool Passed => Scenarios.All(scenario => scenario.Validation.Decision == "PASS") && Checks.All(check => check.Passed);
}

internal static class CurveRisk
{
    private const double UsdRoundingTolerance = 0.01;
    private const double BumpRatioTolerance = 0.5;

    public static async Task<OisRiskReport> RunAsync(CurveInputs inputs, IReadOnlyDictionary<int, CurveInputs> savedBumps,
        IReadOnlyDictionary<int, double>? reportedPvOverrides = null)
    {
        var baseRate = GetFiveYearRate(inputs.OisSnapshot);
        var trade = new OisTrade("5Y", baseRate, 1_000_000);
        var baseRun = await CurveValidation.RunAsync(inputs, trade: trade);
        var baseValuation = baseRun.TradeValuation;
        var scenarios = new List<OisRiskScenario>
        {
            new(0, baseRate, baseValuation?.ModelParRatePercent, baseValuation?.PvUsd,
                baseValuation is null ? null : 0.0, baseRun.Validation, baseValuation?.FixedLegAnnuity),
        };
        // Allow for the curve's par quote residual when using market quotes to predict PV.
        var baseDollarPerBp = baseValuation?.FixedLegAnnuity * trade.NotionalUsd / 10_000;
        var checks = new List<OisRiskCheck>
        {
            WithinTolerance("baseline_pv_usd", 0, baseValuation?.PvUsd, 0,
                baseDollarPerBp * SOFRCurve.RepricingToleranceBp + UsdRoundingTolerance),
        };

        if (baseValuation is not null)
        {
            foreach (var (bumpBp, bumpedInputs) in savedBumps.OrderBy(entry => entry.Key))
            {
                var bumpedRate = GetFiveYearRate(bumpedInputs.OisSnapshot);
                var differenceBp = (bumpedRate - baseRate) * 100m;
                if (bumpedInputs.ValuationDate != inputs.ValuationDate || differenceBp != bumpBp)
                    throw new ArgumentException($"The +{bumpBp} bp fixture must use the baseline valuation date and a 5Y rate {bumpBp} bp above the baseline.");

                var bumpedRun = await CurveValidation.RunAsync(bumpedInputs, trade: trade);
                var bumpedValuation = bumpedRun.TradeValuation;
                var reportedPv = bumpedValuation?.PvUsd;
                if (reportedPv is not null && reportedPvOverrides?.TryGetValue(bumpBp, out var fixturePv) == true)
                    reportedPv = fixturePv;

                var pvChange = reportedPv - baseValuation.PvUsd;
                var dollarPerBp = bumpedValuation?.FixedLegAnnuity * trade.NotionalUsd / 10_000;
                scenarios.Add(new OisRiskScenario(bumpBp, bumpedRate, bumpedValuation?.ModelParRatePercent,
                    reportedPv, pvChange, bumpedRun.Validation, bumpedValuation?.FixedLegAnnuity));
                checks.Add(new OisRiskCheck("positive_pv_change", bumpBp, pvChange, 0, null, pvChange > 0));
                checks.Add(WithinTolerance("pv_change_usd", bumpBp, pvChange, (double)differenceBp * dollarPerBp,
                    (baseDollarPerBp + dollarPerBp) * SOFRCurve.RepricingToleranceBp + UsdRoundingTolerance));
            }
        }

        if (savedBumps.ContainsKey(1) && savedBumps.ContainsKey(10))
        {
            var oneBp = scenarios.Find(scenario => scenario.BumpBp == 1)?.PvChangeUsd;
            var tenBp = scenarios.Find(scenario => scenario.BumpBp == 10)?.PvChangeUsd;
            var ratio = oneBp > 0 ? tenBp / oneBp : null;
            checks.Add(WithinTolerance("ten_bp_to_one_bp_ratio", null, ratio, 10, BumpRatioTolerance));
        }

        return new OisRiskReport(inputs.ValuationDate, trade, baseValuation?.StartDate, baseValuation?.MaturityDate,
            scenarios, checks);
    }

    private static OisRiskCheck WithinTolerance(string name, int? bumpBp, double? actual, double? expected, double? tolerance)
    {
        var passed = actual is { } value && expected is { } target && tolerance is { } limit &&
            Math.Abs(value - target) <= limit;
        return new OisRiskCheck(name, bumpBp, actual, expected, tolerance, passed);
    }

    private static decimal GetFiveYearRate(OISSOFR.OISSwapSnapshot snapshot)
    {
        if (snapshot.Quotes.Where(quote => quote?.Tenor == "5Y").Take(2).ToArray() is not [{ Rate: { } rate }])
            throw new ArgumentException("The pricing run requires exactly one 5Y OIS quote with a rate.");

        return rate;
    }
}
