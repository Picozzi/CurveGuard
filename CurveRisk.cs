namespace CurveGuard
{
    public sealed record CurveInputs
    (
        DateOnly ValuationDate,
        OvernightSOFR.SOFRFixing SofrFixing,
        EFFRCalculator.EFFRFixing EffrFixing,
        OISSOFR.OISSwapSnapshot OisSnapshot,
        List<FOMCCalculator.MeetingProbability> FomcDecisions,
        List<FutureImpliedRate.ZqImpliedRate> ZqImpliedRates
    );





    // internal sealed record OisTrade(string Tenor, decimal FixedCouponPercent, double NotionalUsd)
    // {
    //     public string Side => "pay_fixed";
    // }

    // internal sealed record OisTradeValuation(string StartDate, string MaturityDate, double ModelParRatePercent, double PvUsd,
    //     double FixedLegAnnuity);

    // internal sealed record CurveBuildResult(object Report, OisTradeValuation? TradeValuation);

    // internal sealed record OisRiskScenario(int BumpBp, decimal MarketQuotePercent, double? ModelParRatePercent,
    //     double? PvUsd, double? PvChangeUsd, ValidationResult Validation, double? FixedLegAnnuity);

    // internal sealed record OisRiskCheck(string Name, int? BumpBp, double? Actual, double? Expected, double? Tolerance, bool Passed);

    // internal sealed record OisRiskReport(DateOnly ValuationDate, OisTrade Trade, string? StartDate, string? MaturityDate,
    //     IReadOnlyList<OisRiskScenario> Scenarios, IReadOnlyList<OisRiskCheck> Checks)
    // {
    //     public bool Passed => Scenarios.All(scenario => scenario.Validation.Decision == "PASS") && Checks.All(check => check.Passed);
    // }

    public static class CurveRisk
    {
        private const double UsdRoundingTolerance = 0.01;
        private const double BumpRatioTolerance = 0.5;

        public static async Task<CurveRun> RunLiveAsync(CurveInputs inputs)
        {
            var baseRate = FindFiveYearRate(inputs.OisSnapshot);
            var trade = baseRate is { } rate ? CreateTrade(rate) : null;
            var baseRun = await CurveValidation.RunAsync(inputs, loadFomcDecisions, trade);
            if (baseRun.Curve is null)
                return baseRun;

            if (trade is null)
                return Quarantine(baseRun, new("MISSING_5Y_OIS_QUOTE", "Live pricing checks require a 5Y OIS quote.", "5Y"));

            var bumps = new Dictionary<int, CurveInputs>
            {
                [1] = CreateLiveBump(baseRun.Inputs, 1),
                [10] = CreateLiveBump(baseRun.Inputs, 10),
            };

            var risk = await CheckScenariosAsync(baseRun, trade, bumps);
            var run = baseRun with { PricingRisk = risk };
            return risk.Passed ? run : Quarantine(run, new("PRICING_RISK_FAILED", "The 5Y OIS pricing checks failed. See pricing_risk for details.", "5Y"));
        }

        public static async Task<OisRiskReport> RunAsync(CurveInputs inputs, IReadOnlyDictionary<int, CurveInputs> savedBumps,
            IReadOnlyDictionary<int, double>? reportedPvOverrides = null)
        {
            var trade = CreateTrade(GetFiveYearRate(inputs.OisSnapshot));
            var baseRun = await CurveValidation.RunAsync(inputs, trade: trade);
            return await CheckScenariosAsync(baseRun, trade, savedBumps, reportedPvOverrides);
        }

        private static async Task<OisRiskReport> CheckScenariosAsync(CurveRun baseRun, OisTrade trade,
            IReadOnlyDictionary<int, CurveInputs> bumps, IReadOnlyDictionary<int, double>? reportedPvOverrides = null)
        {
            var valuationDate = baseRun.Inputs.ValuationDate;
            var baseRate = trade.FixedCouponPercent;
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
                foreach (var (bumpBp, bumpedInputs) in bumps.OrderBy(entry => entry.Key))
                {
                    var bumpedRate = GetFiveYearRate(bumpedInputs.OisSnapshot);
                    var differenceBp = (bumpedRate - baseRate) * 100m;
                    if (bumpedInputs.ValuationDate != valuationDate || differenceBp != bumpBp)
                        throw new ArgumentException($"The +{bumpBp} bp scenario must use the baseline valuation date and a 5Y rate {bumpBp} bp above the baseline.");

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

            if (bumps.ContainsKey(1) && bumps.ContainsKey(10))
            {
                var oneBp = scenarios.Find(scenario => scenario.BumpBp == 1)?.PvChangeUsd;
                var tenBp = scenarios.Find(scenario => scenario.BumpBp == 10)?.PvChangeUsd;
                var ratio = oneBp > 0 ? tenBp / oneBp : null;
                checks.Add(WithinTolerance("ten_bp_to_one_bp_ratio", null, ratio, 10, BumpRatioTolerance));
            }

            return new OisRiskReport(valuationDate, trade, baseValuation?.StartDate, baseValuation?.MaturityDate,
                scenarios, checks);
        }

        private static OisTrade CreateTrade(decimal rate) => new("5Y", rate, 1_000_000);

        private static CurveInputs CreateLiveBump(CurveInputs baseline, int bumpBp)
        {
            var snapshot = baseline.OisSnapshot;
            var quotes = snapshot.Quotes.Select(quote => quote is { Tenor: "5Y", Rate: { } rate }
                ? quote with { Rate = rate + bumpBp / 100m }
                : quote).ToArray();
            return baseline with
            {
                OisSnapshot = new OISSOFR.OISSwapSnapshot(snapshot.ObservationDate, snapshot.Source, snapshot.Url,
                    snapshot.ProviderCollectionTime, quotes),
            };
        }

        private static CurveRun Quarantine(CurveRun run, ValidationFinding finding) => run with
        {
            Curve = null,
            Validation = run.Validation with { Decision = "QUARANTINE", Findings = [finding] },
        };

        private static OisRiskCheck WithinTolerance(string name, int? bumpBp, double? actual, double? expected, double? tolerance)
        {
            var passed = actual is { } value && expected is { } target && tolerance is { } limit &&
                Math.Abs(value - target) <= limit;
            return new OisRiskCheck(name, bumpBp, actual, expected, tolerance, passed);
        }

        private static decimal GetFiveYearRate(OISSOFR.OISSwapSnapshot snapshot) => FindFiveYearRate(snapshot)
            ?? throw new ArgumentException("The pricing run requires exactly one 5Y OIS quote with a rate.");

        private static decimal? FindFiveYearRate(OISSOFR.OISSwapSnapshot snapshot)
            => snapshot.Quotes.Where(quote => quote?.Tenor == "5Y").Take(2).ToArray() is [{ Rate: { } rate }]
                ? rate
                : null;
    }
}