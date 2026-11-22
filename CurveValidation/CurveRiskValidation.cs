
using System.Globalization;
using QL = QuantLib;

namespace CurveGuard
{
    record OISTrade(string Tenor, decimal FixedCouponPercent, double NotionalUsd)
    {
        public string Side => "pay_fixed";
    }

    record OISTradeValuation(string StartDate, string MaturityDate, double ModelParRatePercent, double PvUsd, double FixedLegAnnuity);

    public static class CurveRiskValidation
    {
        private const double UsdRoundingTolerance = 0.01;
        private const double BumpRatioTolerance = 0.5;

        public static async Task<ValidationResult> RunPricingValidation(CurveInputs inputs, SOFRCurve curve, IReadOnlyDictionary<int, CurveInputs>? savedBumps = null, IReadOnlyDictionary<int, double>? reportedPvOverrides = null)
        {
            if (FindFiveYearRate(inputs) is not { } base5YRate)
                return new ValidationResult("QUARANTINE", "PASS",
                    "MISSING_5Y_OIS_QUOTE Pricing checks were skipped because a unique 5Y OIS rate was unavailable.",
                    [new("MISSING_5Y_OIS_QUOTE", "Pricing checks require exactly one 5Y OIS quote with a rate.", "5Y")], []);
            
            var trade = CreateTrade(base5YRate);
            OISTradeValuation valuation = PriceTrade(curve, inputs.ValuationDate, trade);

            var bumps = savedBumps ?? new Dictionary<int, CurveInputs>
            {
                [1] = CreateLiveBump(inputs, 1),
                [10] = CreateLiveBump(inputs, 10),
            };

            return await CheckScenariosAsync(inputs, trade, bumps, valuation, reportedPvOverrides);
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

        private static async Task<ValidationResult> CheckScenariosAsync(CurveInputs inputs, OISTrade trade, IReadOnlyDictionary<int, CurveInputs> bumps, OISTradeValuation baseValuation, IReadOnlyDictionary<int, double>? reportedPvOverrides)
        {
            var valuationDate = inputs.ValuationDate;
            var baseRate = trade.FixedCouponPercent;
            double? oneBpPvChange = null;
            double? tenBpPvChange = null;
            // Allow for the curve's par quote residual when using market quotes to predict PV.
            var baseDollarPerBp = baseValuation?.FixedLegAnnuity * trade.NotionalUsd / 10_000;
            var checks = new List<PricingRiskCheck>
            {
                WithinTolerance("baseline_pv_usd", 0, baseValuation?.PvUsd, 0,
                    baseDollarPerBp * SOFRCurveCreation.RepricingToleranceBp + UsdRoundingTolerance),
            };

            if (baseValuation is not null)
            {
                foreach (var (bumpBp, bumpedInputs) in bumps.OrderBy(entry => entry.Key))
                {
                    if (FindFiveYearRate(bumpedInputs) is not { } bumpedRate ||
                        bumpedInputs.ValuationDate != valuationDate || (bumpedRate - baseRate) * 100m != bumpBp)
                        throw new ArgumentException($"The +{bumpBp} bp scenario must use the baseline valuation date and a 5Y rate {bumpBp} bp above the baseline.");

                    var bumpedRun = SOFRCurveCreation.Build(bumpedInputs);
                    var bumpedValuation = PriceTrade(bumpedRun, bumpedInputs.ValuationDate, trade);
                    var reportedPv = reportedPvOverrides?.TryGetValue(bumpBp, out var fixturePv) == true
                        ? fixturePv
                        : bumpedValuation.PvUsd;

                    var pvChange = reportedPv - baseValuation.PvUsd;
                    if (bumpBp == 1)
                        oneBpPvChange = pvChange;
                    else if (bumpBp == 10)
                        tenBpPvChange = pvChange;

                    var dollarPerBp = bumpedValuation?.FixedLegAnnuity * trade.NotionalUsd / 10_000;
                    checks.Add(new PricingRiskCheck("positive_pv_change", bumpBp, pvChange, 0, null, pvChange > 0));
                    checks.Add(WithinTolerance("pv_change_usd", bumpBp, pvChange, bumpBp * dollarPerBp,
                        (baseDollarPerBp + dollarPerBp) * SOFRCurveCreation.RepricingToleranceBp + UsdRoundingTolerance));
                }
            }

            if (bumps.ContainsKey(1) && bumps.ContainsKey(10))
            {
                var ratio = oneBpPvChange > 0 ? tenBpPvChange / oneBpPvChange : null;
                checks.Add(WithinTolerance("ten_bp_to_one_bp_ratio", null, ratio, 10, BumpRatioTolerance));
            }

            var passed = checks.All(check => check.Passed);
            var pricingChecks = checks.Select(check => check with { Tenor = trade.Tenor, QuoteDate = valuationDate }).ToList();
            var message = passed ? "" : "PRICING_VALIDATION_FAILED One or more OIS pricing checks failed.";
            return new ValidationResult(passed ? "PASS" : "QUARANTINE", "PASS", message, [], pricingChecks);
        }

        private static decimal? FindFiveYearRate(CurveInputs inputs)
            => inputs.OisSnapshot.Quotes.Where(quote => quote?.Tenor == "5Y").Take(2).ToArray() is [{ Rate: { } rate }]
                ? rate
                : null;

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
 
        private static PricingRiskCheck WithinTolerance(string code, int? bumpBp, double? actual, double? expected, double? tolerance)
        {
            var passed = actual is { } value && expected is { } target && tolerance is { } limit &&
                Math.Abs(value - target) <= limit;
            return new PricingRiskCheck(code, bumpBp, actual, expected, tolerance, passed);
        }
    }
}
