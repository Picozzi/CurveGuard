
using System.Globalization;
using System.Text.Json.Serialization;
using QL = QuantLib;

namespace CurveGuard
{
    internal sealed record OISTrade(string Tenor, decimal FixedCouponPercent, double NotionalUsd)
    {
        public string Side => "pay_fixed";
    }

    internal sealed record OISTradeValuation(string StartDate, string MaturityDate, double ModelParRatePercent, double PvUsd,
        double FixedLegAnnuity);

    internal sealed record OISRiskScenario(int BumpBp, decimal MarketQuotePercent, double? ModelParRatePercent, double? PvUsd, double? PvChangeUsd, double? FixedLegAnnuity);

    internal sealed record OISRiskCheck(string Name, int? BumpBp, double? Actual, double? Expected, double? Tolerance, bool Passed);

    public static class CurveRiskValidation
    {
        private const double UsdRoundingTolerance = 0.01;
        private const double BumpRatioTolerance = 0.5;

        public static async Task<ValidationResult> RunPricingValidation(CurveInputs inputs, SOFRCurve curve)
        {
            var base5YRate = inputs.OisSnapshot.Quotes.Where(quote => quote?.Tenor == "5Y").Take(2).ToArray() is [{ Rate: { } rate }] ? rate : -1;

            if(base5YRate == -1)
                return new ValidationResult("MISSING_5Y_OIS_QUOTE", "Live pricing checks require a 5Y OIS quote.", []);
            
            var trade = CreateTrade(base5YRate);
            OISTradeValuation valuation = PriceTrade(curve, inputs.ValuationDate, trade);

            var bumps = new Dictionary<int, CurveInputs>
            {
                [1] = CreateLiveBump(inputs, 1),
                [10] = CreateLiveBump(inputs, 10),
            };

            return await CheckScenariosAsync(inputs, trade, bumps, valuation, curve);

            //return risk.Passed ? run : Quarantine(run, new("PRICING_RISK_FAILED", "The 5Y OIS pricing checks failed. See pricing_risk for details.", "5Y"));
        }

        private static OISTradeValuation PriceTrade(SOFRCurve curve, DateOnly valuationDate, OISTrade trade)
        {
            var repricing = curve.Repricings.SingleOrDefault(row => row.Instrument == "ois" && row.Tenor == trade.Tenor)
                ?? throw new ArgumentException($"No calibration swap is available for the sample trade tenor {trade.Tenor}.");

            lock (SOFRCurveCreation.QuantLibLock)
            {
                using var settings = QL.Settings.instance();
                using var previousDate = settings.getEvaluationDate();
                using var referenceDate = SOFRCurveCreation.ToQuantLibDate(valuationDate);
                settings.setEvaluationDate(referenceDate);
                try
                {
                    using var discountCurve = RestoreDiscountCurve(curve);
                    using var discountHandle = new QL.YieldTermStructureHandle(discountCurve);
                    using var index = new QL.Sofr(discountHandle);
                    using var calendar = new QL.UnitedStates(QL.UnitedStates.Market.GovernmentBond);
                    using var dayCount = new QL.Actual360();
                    // Reuse the calibration schedules and give the priced swap its own coupon and curve.
                    using var helper = SOFRCurveCreation.CreateOisHelper(trade.Tenor, repricing.QuotePercent, index, calendar, dayCount);
                    using var template = helper.swap();
                    using var fixedSchedule = template.fixedSchedule();
                    using var floatingSchedule = template.floatingSchedule();
                    using var notionals = new QL.DoubleVector { trade.NotionalUsd };
                    using var swap = new QL.OvernightIndexedSwap(QL.OvernightIndexedSwap.Type.Payer, notionals,
                        fixedSchedule, (double)(trade.FixedCouponPercent / 100m), dayCount, notionals, floatingSchedule,
                        index, spread: 0, paymentLag: curve.OisSource.PaymentLagBusinessDays,
                        paymentAdjustment: QL.BusinessDayConvention.Following, paymentCalendar: calendar,
                        telescopicValueDates: false, averagingMethod: template.averagingMethod(),
                        lookbackDays: template.lookbackDays(), lockoutDays: template.lockoutDays(),
                        applyObservationShift: template.applyObservationShift());
                    using var engine = new QL.DiscountingSwapEngine(discountHandle);
                    swap.setPricingEngine(engine);

                    var pv = swap.NPV();
                    var parRate = swap.fairRate() * 100;
                    if (!double.IsFinite(pv) || !double.IsFinite(parRate))
                        throw new InvalidOperationException("The sample swap produced a nonfinite PV or par rate.");

                    using var start = swap.startDate();
                    using var maturity = swap.maturityDate();
                    return new OISTradeValuation(start.ISO(), maturity.ISO(), parRate, pv,
                        FixedLegAnnuity(swap, discountCurve));
                }
                finally
                {
                    settings.setEvaluationDate(previousDate);
                }
            }
        }

        private static QL.LogMixedLinearCubicDiscountCurve RestoreDiscountCurve(SOFRCurve curve)
        {
            var handoff = curve.FomcFrontEnd.HandoffDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var handoffIndex = curve.Pillars.FindIndex(pillar => pillar.Date == handoff);
            if (handoffIndex < 1 || handoffIndex >= curve.Pillars.Count - 1)
                throw new InvalidOperationException("The discount curve must have a handoff pillar followed by OIS pillars.");

            using var dates = new QL.DateVector();
            using var discounts = new QL.DoubleVector();
            foreach (var pillar in curve.Pillars)
            {
                var date = DateOnly.ParseExact(pillar.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                using var qlDate = SOFRCurveCreation.ToQuantLibDate(date);
                dates.Add(qlDate);
                discounts.Add(pillar.DiscountFactor);
            }

            // Use the bootstrap's interpolation and handoff slope to recover discounts between pillars.
            using var interpolation = new QL.LogMixedLinearCubic((uint)handoffIndex, QL.MixedInterpolation.Behavior.SplitRanges,
                QL.CubicInterpolation.DerivativeApprox.Spline, false, QL.CubicInterpolation.BoundaryCondition.FirstDerivative,
                -curve.FomcFrontEnd.HandoffForwardPercent / 100, QL.CubicInterpolation.BoundaryCondition.SecondDerivative, 0);
            using var dayCount = new QL.Actual365Fixed();
            using var calendar = new QL.NullCalendar();
            return new QL.LogMixedLinearCubicDiscountCurve(dates, discounts, dayCount, calendar, interpolation);
        }

        private static double FixedLegAnnuity(QL.OvernightIndexedSwap swap, QL.YieldTermStructure curve)
        {
            using var fixedLeg = swap.fixedLeg();
            var annuity = 0.0;
            for (var i = 0; i < fixedLeg.Count; i++)
            {
                using var cashFlow = fixedLeg[i];
                using var coupon = QL.NQuantLibc.as_fixed_rate_coupon(cashFlow);
                using var paymentDate = coupon.date();
                annuity += coupon.accrualPeriod() * curve.discount(paymentDate);
            }

            if (!double.IsFinite(annuity) || annuity <= 0)
                throw new InvalidOperationException("The sample swap must have a finite, positive fixed leg annuity.");

            return annuity;
        }

        private static async Task<ValidationResult> CheckScenariosAsync(CurveInputs inputs, OISTrade trade, IReadOnlyDictionary<int, CurveInputs> bumps, OISTradeValuation baseValuation, SOFRCurve curve)
        {
            var valuationDate = inputs.ValuationDate;
            var baseRate = trade.FixedCouponPercent;
            var scenarios = new List<OISRiskScenario>
            {
                new(0, baseRate, baseValuation?.ModelParRatePercent, baseValuation?.PvUsd, 0.0, baseValuation?.FixedLegAnnuity),
            };
            // Allow for the curve's par quote residual when using market quotes to predict PV.
            var baseDollarPerBp = baseValuation?.FixedLegAnnuity * trade.NotionalUsd / 10_000;
            var checks = new List<OISRiskCheck>
            {
                WithinTolerance("baseline_pv_usd", 0, baseValuation?.PvUsd, 0,
                    baseDollarPerBp * SOFRCurveCreation.RepricingToleranceBp + UsdRoundingTolerance),
            };

            if (baseValuation is not null)
            {
                foreach (var (bumpBp, bumpedInputs) in bumps.OrderBy(entry => entry.Key))
                {
                    var bumpedRate = bumpedInputs.OisSnapshot.Quotes.Where(quote => quote?.Tenor == "5Y").Take(2).ToArray() is [{ Rate: { } rate }] ? rate : -1;
                    var differenceBp = (bumpedRate - baseRate) * 100m;
                    if (bumpedInputs.ValuationDate != valuationDate || differenceBp != bumpBp)
                        throw new ArgumentException($"The +{bumpBp} bp scenario must use the baseline valuation date and a 5Y rate {bumpBp} bp above the baseline.");

                    var bumpedRun = SOFRCurveCreation.Build(bumpedInputs);
                    var bumpedValuation = PriceTrade(bumpedRun, bumpedInputs.ValuationDate, trade);                    

                    var pvChange = bumpedValuation?.PvUsd - baseValuation.PvUsd;
                    var dollarPerBp = bumpedValuation?.FixedLegAnnuity * trade.NotionalUsd / 10_000;
                    scenarios.Add(new OISRiskScenario(bumpBp, bumpedRate, bumpedValuation?.ModelParRatePercent, bumpedValuation?.PvUsd, pvChange, bumpedValuation?.FixedLegAnnuity));
                    checks.Add(new OISRiskCheck("positive_pv_change", bumpBp, pvChange, 0, null, pvChange > 0));
                    checks.Add(WithinTolerance("pv_change_usd", bumpBp, pvChange, (double)differenceBp * dollarPerBp,
                        (baseDollarPerBp + dollarPerBp) * SOFRCurveCreation.RepricingToleranceBp + UsdRoundingTolerance));
                }
            }

            if (bumps.ContainsKey(1) && bumps.ContainsKey(10))  
            {
                var oneBp = scenarios.Find(scenario => scenario.BumpBp == 1)?.PvChangeUsd;
                var tenBp = scenarios.Find(scenario => scenario.BumpBp == 10)?.PvChangeUsd;
                var ratio = oneBp > 0 ? tenBp / oneBp : null;
                checks.Add(WithinTolerance("ten_bp_to_one_bp_ratio", null, ratio, 10, BumpRatioTolerance));
            }

            bool passed = checks.All(x => x.Passed);


            return new ValidationResult(passed ? "PASS" : "QUARANTINE", passed ? "PASS" : "FAIL", checks.Select(check => new ValidationFinding(
                check.Name, check.Passed ? "passed" : "failed",
                Tenor: trade.Tenor, QuoteDate: valuationDate, ExpectedDate: null, Contract: null)).ToList());
        }

        private static OISTrade CreateTrade(decimal rate) => new("5Y", rate, 1_000_000);

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
 
        private static OISRiskCheck WithinTolerance(string name, int? bumpBp, double? actual, double? expected, double? tolerance)
        {
            var passed = actual is { } value && expected is { } target && tolerance is { } limit &&
                Math.Abs(value - target) <= limit;
            return new OISRiskCheck(name, bumpBp, actual, expected, tolerance, passed);
        }
    }
}
