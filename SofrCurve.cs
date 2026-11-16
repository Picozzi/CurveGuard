using System.Globalization;
using QL = QuantLib;

namespace CurveGuard
{
    public static class SOFRCurve
    {
        private const uint SettlementDays = 2;
        private const int PaymentLagDays = 2;
        private const double RepricingToleranceBp = 0.01;
        private const double Notional = 1_000_000;
        private static readonly object QuantLibLock = new();

        public static DateOnly PreviousFixingDate(DateOnly valuationDate)
        {
            using var calendar = new QL.UnitedStates(QL.UnitedStates.Market.GovernmentBond);
            using var date = ToQuantLibDate(valuationDate);
            if (!calendar.isBusinessDay(date))
                throw new ArgumentException("Valuation date must be a US Government Bond business day.");

            using var previous = calendar.advance(date, -1, QL.TimeUnit.Days);
            return DateOnly.ParseExact(previous.ISO(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        public static object Build(DateOnly valuationDate, OvernightSOFR.SOFRFixing fixing, OISSOFR.OISSwapSnapshot swaps, EFFRCalculator.EFFRFixing effr, IReadOnlyList<FOMCCalculator.MeetingProbability> decisions)
            => Build(valuationDate, fixing, swaps, effr, decisions, trade: null).Report;

        internal static CurveBuildResult Build(DateOnly valuationDate, OvernightSOFR.SOFRFixing fixing,
            OISSOFR.OISSwapSnapshot swaps, EFFRCalculator.EFFRFixing effr,
            IReadOnlyList<FOMCCalculator.MeetingProbability> decisions, OisTrade? trade)
        {
            if (fixing.EffectiveDate != PreviousFixingDate(valuationDate) ||
                DateOnly.FromDateTime(effr.Date) != fixing.EffectiveDate ||
                DateOnly.FromDateTime(swaps.ObservationDate) != valuationDate)
                throw new ArgumentException("SOFR fixing or OIS snapshot date does not match the valuation date.");

            if (swaps.Quotes.Count == 0)
                throw new ArgumentException("At least one OIS quote is required.");

            // QuantLib's evaluation date is shared by relative-date helpers.
            lock (QuantLibLock)
            {
                using var settings = QL.Settings.instance();
                using var previousDate = settings.getEvaluationDate();
                using var referenceDate = ToQuantLibDate(valuationDate);
                settings.setEvaluationDate(referenceDate);
                try
                {
                    return Bootstrap(referenceDate, valuationDate, fixing, swaps, effr, decisions, trade);
                }
                finally
                {
                    settings.setEvaluationDate(previousDate);
                }
            }
        }

        private static CurveBuildResult Bootstrap(QL.Date referenceDate, DateOnly valuationDate, OvernightSOFR.SOFRFixing fixing,
            OISSOFR.OISSwapSnapshot swaps, EFFRCalculator.EFFRFixing effr,
            IReadOnlyList<FOMCCalculator.MeetingProbability> decisions, OisTrade? trade)
        {
            using var calendar = new QL.UnitedStates(QL.UnitedStates.Market.GovernmentBond);
            using var overnight = new QL.Period(1, QL.TimeUnit.Days);
            using var accrual = new QL.Actual360();
            using var index = new QL.Sofr();
            using var deposit = new QL.DepositRateHelper((double)fixing.RateDecimal, overnight, 0, calendar,
                QL.BusinessDayConvention.Following, false, accrual);
            using var helpers = new QL.RateHelperVector();
            helpers.Add(deposit);

            var handoffDate = valuationDate.AddYears(1);
            using var handoff = ToQuantLibDate(handoffDate);
            using var depositPillar = deposit.pillarDate();
            var occupiedPillars = new HashSet<int> { depositPillar.serialNumber() };
            var excludedTenors = new List<string>();
            var swapsByPillar = new SortedDictionary<int, (OISSOFR.OisApiQuote Quote, QL.OISRateHelper Helper)>();
            try
            {
                foreach (var quote in swaps.Quotes)
                {
                    if (quote.Tenor is not { } tenor || quote.Rate is not { } rate ||
                        quote.Date is not { } date || date.Date != swaps.ObservationDate.Date)
                        throw new ArgumentException("Each OIS quote must have a tenor, rate and matching observation date.");

                    var helper = CreateOisHelper(tenor, rate, index, calendar, accrual);
                    using var pillar = helper.pillarDate();
                    using var maturity = helper.maturityDate();
                    if (maturity.serialNumber() < handoff.serialNumber() || pillar.serialNumber() <= handoff.serialNumber())
                    {
                        excludedTenors.Add(tenor);
                        helper.Dispose();
                        continue;
                    }
                    if (!occupiedPillars.Add(pillar.serialNumber()))
                    {
                        helper.Dispose();
                        throw new ArgumentException($"Duplicate calibration pillar {pillar.ISO()} for OIS {tenor}.");
                    }

                    swapsByPillar.Add(pillar.serialNumber(), (quote, helper));
                    helpers.Add(helper);
                }

                if (swapsByPillar.Count == 0)
                    throw new ArgumentException("At least one OIS pillar beyond the one-year handoff is required.");

                using var curveDayCount = new QL.Actual365Fixed();
                var front = BuildFrontEnd(valuationDate, handoffDate, calendar, ToDateOnly(depositPillar), fixing, effr, decisions);
                var oneYear = swapsByPillar.Values.FirstOrDefault(swap => swap.Quote.Tenor is "1Y" or "12M");
                var levelAdjustment = oneYear.Helper is not null && front.Count > 1
                    ? FitOneYearLevel(front, oneYear.Helper, oneYear.Quote.Rate!.Value, accrual)
                    : 0.0;
                var frontSegments = new List<FrontSegment>(front.Count);
                var integratedRate = 0.0;
                var switchIndex = 1u; // The reference date is index zero and the overnight deposit is index one.
                using var unadjusted = new QL.NullCalendar();
                foreach (var (segment, i) in front.Select((segment, i) => (segment, i)))
                {
                    var calibratedRate = segment.RawRate + (i == 0 ? 0 : levelAdjustment);
                    integratedRate += calibratedRate * YearFraction(segment.StartDate, segment.EndDate);
                    var discount = RequireDiscount(Math.Exp(-integratedRate));
                    frontSegments.Add(new FrontSegment(segment.StartDate, segment.EndDate, segment.DecisionDate,
                        segment.ExpectedEffrPercent, segment.RawRate * 100, calibratedRate * 100, discount));
                    using var end = ToQuantLibDate(segment.EndDate);
                    if (end.serialNumber() == depositPillar.serialNumber())
                        continue;
                    using var tenor = new QL.Period(segment.EndDate.DayNumber - valuationDate.DayNumber, QL.TimeUnit.Days);
                    var simpleRate = (1 / discount - 1) / YearFraction(valuationDate, segment.EndDate);
                    using var helper = new QL.DepositRateHelper(simpleRate, tenor, 0, unadjusted,
                        QL.BusinessDayConvention.Unadjusted, false, curveDayCount);
                    helpers.Add(helper);
                    switchIndex++;
                }

                var terminalRate = frontSegments[^1].CalibratedForwardPercent / 100;
                using var interpolation = new QL.LogMixedLinearCubic(switchIndex, QL.MixedInterpolation.Behavior.SplitRanges,
                    QL.CubicInterpolation.DerivativeApprox.Spline, false, QL.CubicInterpolation.BoundaryCondition.FirstDerivative,
                    -terminalRate, QL.CubicInterpolation.BoundaryCondition.SecondDerivative, 0);
                using var jumps = new QL.QuoteHandleVector();
                using var jumpDates = new QL.DateVector();
                using var curve = new QL.PiecewiseLogMixedLinearCubicDiscount(referenceDate, helpers, curveDayCount,
                    jumps, jumpDates, interpolation);
                using var dates = curve.dates(); // Forces iterative calibration.
                using var values = curve.data();
                var pillars = new List<Pillar>(dates.Count);
                for (var i = 0; i < dates.Count; i++)
                {
                    using var date = dates[i];
                    var discount = RequireDiscount(values[i]);
                    var t = curveDayCount.yearFraction(referenceDate, date);
                    double? zero = t > 0 ? -Math.Log(discount) / t * 100 : null;
                    pillars.Add(new Pillar(date.ISO(), discount, zero));
                }

                var repricing = new List<Repricing> { RepriceDeposit(curve, deposit, accrual, fixing.RatePercent) };
                OisTradeValuation? tradeValuation = null;
                foreach (var (quote, helper) in swapsByPillar.Values)
                {
                    var repriced = RepriceSwap(quote, helper);
                    repricing.Add(repriced);
                    if (trade is not null && repriced.Tenor == trade.Tenor)
                    {
                        using var swap = helper.swap();
                        var pv = SwapPvAtCoupon(swap, trade.FixedCouponPercent, trade.NotionalUsd);
                        if (!double.IsFinite(pv))
                            throw new InvalidOperationException("The sample swap produced a nonfinite PV.");

                        tradeValuation = new OisTradeValuation(repriced.StartDate, repriced.MaturityDate,
                            repriced.ModelPercent, pv);
                    }
                }

                if (trade is not null && tradeValuation is null)
                    throw new ArgumentException($"No calibration swap is available for the sample trade tenor {trade.Tenor}.");

                var maxResidual = repricing.Max(row => Math.Abs(row.ResidualBp));
                var maxPv = repricing.Max(row => Math.Abs(row.NpvPerMillion));
                if (!double.IsFinite(maxResidual) || !double.IsFinite(maxPv) || maxResidual > RepricingToleranceBp)
                    throw new InvalidOperationException("Curve calibration failed its repricing check.");

                using var lastDate = curve.maxDate();
                var forwards = SampleForwards(curve, referenceDate, lastDate, dates, curveDayCount, frontSegments);
                var maxFrontError = CheckFrontEnd(curve, frontSegments);
                var handoffTime = curveDayCount.yearFraction(referenceDate, handoff);
                const double slopeCheckInterval = 1e-6;
                var handoffDiscount = curve.discount(handoff);
                var rightForward = Math.Log(handoffDiscount / curve.discount(handoffTime + slopeCheckInterval)) / slopeCheckInterval;
                var forwardGapBp = (rightForward - terminalRate) * 10_000;
                if (!double.IsFinite(forwardGapBp) || Math.Abs(forwardGapBp) > RepricingToleranceBp)
                    throw new InvalidOperationException("The cubic OIS spline did not match the terminal FOMC forward.");
                var report = new
                {
                    ReferenceDate = referenceDate.ISO(),
                    QuantLibVersion = "1.44",
                    Bootstrap = "QuantLib iterative discount-factor bootstrap",
                    Interpolation = "Log-linear discount factors through the one-year handoff, cubic log-discount spline with a matched forward at the handoff thereafter",
                    CurveDayCount = "Actual/365 Fixed",
                    Extrapolation = false,
                    OvernightAnchor = new
                    {
                        fixing.EffectiveDate, fixing.RatePercent, Source = "Federal Reserve Bank of New York",
                        Use = "Previous business day's published fixing as a proxy for a same-day-start overnight deposit",
                    },
                    OisSource = new
                    {
                        swaps.Source, swaps.Url, swaps.ProviderCollectionTime, swaps.ObservationDate,
                        SpotLagBusinessDays = SettlementDays, PaymentLagBusinessDays = PaymentLagDays,
                        Convention = "US Government Bond calendar, annual Actual/360 fixed leg, compounded SOFR",
                    },
                    FomcFrontEnd = new
                    {
                        HandoffDate = handoffDate, HandoffDiscountFactor = handoffDiscount,
                        HandoffForwardPercent = terminalRate * 100,
                        EffrFixingPercent = effr.Rate, StartingSofrMinusEffrBp = (fixing.RatePercent - (decimal)effr.Rate) * 100m,
                        PostFirstDecisionLevelAdjustmentBp = levelAdjustment * 10_000,
                        Source = "Dated Yahoo ZQ closes, Federal Reserve FOMC schedule and New York Fed fixings",
                        Segments = frontSegments,
                        ExcludedShortOisTenors = excludedTenors,
                    },
                    Coverage = new { OvernightPillar = pillars[1].Date, FirstOisTenor = repricing[1].Tenor,
                        FirstOisPillar = repricing[1].PillarDate, LastPillar = pillars[^1].Date },
                    Pillars = pillars,
                    Repricing = repricing,
                    MonthlyAndPillarForwards = forwards,
                    Checks = new { RepricingToleranceBp, MaxAbsoluteResidualBp = maxResidual, MaxAbsoluteNpvPerMillion = maxPv,
                        MaxFrontEndDiscountError = maxFrontError, HandoffForwardGapBp = forwardGapBp },
                };
                return new CurveBuildResult(report, tradeValuation);
            }
            finally
            {
                foreach (var (_, helper) in swapsByPillar.Values)
                    helper.Dispose();
            }
        }

        private static List<ProjectedSegment> BuildFrontEnd(DateOnly valuationDate, DateOnly handoff, QL.Calendar calendar,
            DateOnly overnightEnd, OvernightSOFR.SOFRFixing fixing, EFFRCalculator.EFFRFixing effr,
            IReadOnlyList<FOMCCalculator.MeetingProbability> decisions)
        {
            var overnightDays = overnightEnd.DayNumber - valuationDate.DayNumber;
            var forward = Math.Log(1 + (double)fixing.RateDecimal * overnightDays / 360) / YearFraction(valuationDate, overnightEnd);
            var expectedEffr = (decimal)effr.Rate;
            var segments = new List<ProjectedSegment>();
            var start = valuationDate;
            DateOnly? priorDecision = null;
            foreach (var decision in decisions)
            {
                var decisionDate = DateOnly.FromDateTime(decision.MeetingDate);
                using var date = ToQuantLibDate(decisionDate);
                using var nextBusinessDay = calendar.advance(date, 1, QL.TimeUnit.Days);
                var effective = ToDateOnly(nextBusinessDay);
                if (effective >= handoff)
                    break;
                if (effective < overnightEnd || effective <= start)
                    throw new ArgumentException("FOMC effective dates must be ordered and outside the overnight accrual interval.");

                segments.Add(new ProjectedSegment(start, effective, priorDecision, expectedEffr, forward));
                var move = decision.PostMeetingRate - decision.PreMeetingRate;
                expectedEffr += move;
                forward += (double)(move / 100m) * 365 / 360;
                start = effective;
                priorDecision = decisionDate;
            }
            segments.Add(new ProjectedSegment(start, handoff, priorDecision, expectedEffr, forward));
            return segments;
        }

        private static double FitOneYearLevel(List<ProjectedSegment> front, QL.OISRateHelper swap, decimal parRatePercent,
            QL.DayCounter dayCount)
        {
            // An annual 1Y OIS has one payment on each leg. The common payment discount cancels in the par condition.
            using var start = swap.earliestDate();
            using var maturity = swap.maturityDate();
            var startDate = ToDateOnly(start);
            var endDate = ToDateOnly(maturity);
            var integrated = 0.0;
            var adjustableYears = 0.0;
            for (var i = 0; i < front.Count; i++)
            {
                var segment = front[i];
                var from = segment.StartDate > startDate ? segment.StartDate : startDate;
                // Extend the final step to the spot-starting 1Y swap maturity for the level fit.
                var to = i == front.Count - 1 || segment.EndDate > endDate ? endDate : segment.EndDate;
                if (to <= from)
                    continue;
                var years = YearFraction(from, to);
                integrated += segment.RawRate * years;
                if (i > 0)
                    adjustableYears += years;
            }

            var requiredIntegral = Math.Log(1 + (double)(parRatePercent / 100m) * dayCount.yearFraction(start, maturity));
            var adjustment = (requiredIntegral - integrated) / adjustableYears;
            return double.IsFinite(adjustment) ? adjustment
                : throw new ArgumentException("The 1Y OIS quote cannot be used to fit the FOMC front-end level.");
        }

        private static double CheckFrontEnd(QL.YieldTermStructure curve, List<FrontSegment> segments)
        {
            var maxError = 0.0;
            var startDiscount = 1.0;
            foreach (var segment in segments)
            {
                for (var day = segment.StartDate.DayNumber; day <= segment.EndDate.DayNumber; day++)
                {
                    var date = DateOnly.FromDayNumber(day);
                    using var qlDate = ToQuantLibDate(date);
                    var expected = startDiscount * Math.Exp(-segment.CalibratedForwardPercent / 100 * YearFraction(segment.StartDate, date));
                    maxError = Math.Max(maxError, Math.Abs(curve.discount(qlDate) - expected));
                }
                startDiscount = segment.DiscountFactorAtEnd;
            }
            if (!double.IsFinite(maxError) || maxError > 1e-9)
                throw new InvalidOperationException("The bootstrapped curve does not reproduce the forward-flat FOMC segments.");
            return maxError;
        }

        private static QL.OISRateHelper CreateOisHelper(string tenorText, decimal percent, QL.Sofr index, QL.Calendar calendar,
            QL.DayCounter fixedDayCount)
        {
            using var tenor = new QL.Period(tenorText);
            using var quote = new QL.SimpleQuote((double)(percent / 100m));
            using var rate = new QL.QuoteHandle(quote);
            using var discountCurve = new QL.YieldTermStructureHandle();
            using var forwardStart = new QL.Period(0, QL.TimeUnit.Days);
            using var customPillar = new QL.Date();
            using var endOfMonth = new QL.OptionalBool(false);
            using var fixedFrequency = new QL.OptionalFrequency();

            return new QL.OISRateHelper(SettlementDays, tenor, rate, index, discountCurve,
                telescopicValueDates: false, paymentLag: PaymentLagDays,
                paymentConvention: QL.BusinessDayConvention.Following, paymentFrequency: QL.Frequency.Annual,
                paymentCalendar: calendar, forwardStart: forwardStart, overnightSpread: 0,
                pillar: QL.Pillar.Choice.LastRelevantDate, customPillarDate: customPillar,
                averagingMethod: QL.RateAveraging.Type.Compound, endOfMonth: endOfMonth,
                fixedPaymentFrequency: fixedFrequency, fixedCalendar: calendar, lookbackDays: 0, lockoutDays: 0,
                applyObservationShift: false, pricer: null, rule: QL.DateGeneration.Rule.Backward,
                overnightCalendar: calendar, convention: QL.BusinessDayConvention.ModifiedFollowing,
                fixedDayCount: fixedDayCount);
        }

        private static Repricing RepriceDeposit(QL.YieldTermStructure curve, QL.DepositRateHelper helper, QL.DayCounter dayCount,
            decimal percent)
        {
            using var start = helper.earliestDate();
            using var maturity = helper.maturityDate();
            using var pillar = helper.pillarDate();
            var rate = (double)(percent / 100m);
            var accrual = dayCount.yearFraction(start, maturity);
            var pv = Notional * ((1 + rate * accrual) * curve.discount(maturity) - curve.discount(start));
            var modelRate = helper.impliedQuote();
            return new Repricing("deposit_proxy", "ON", "New York Fed", "published_fixing_proxy", null, null,
                start.ISO(), maturity.ISO(), pillar.ISO(), percent, modelRate * 100, (modelRate - rate) * 10_000, pv);
        }

        private static Repricing RepriceSwap(OISSOFR.OisApiQuote quote, QL.OISRateHelper helper)
        {
            using var start = helper.earliestDate();
            using var maturity = helper.maturityDate();
            using var pillar = helper.pillarDate();
            using var swap = helper.swap();
            var percent = quote.Rate!.Value;
            var rate = (double)(percent / 100m);
            var modelRate = helper.impliedQuote();
            var pv = SwapPvAtCoupon(swap, percent, Notional);
            return new Repricing("ois", quote.Tenor!, quote.Source, quote.Method, quote.Trades, quote.CloseTrades,
                start.ISO(), maturity.ISO(), pillar.ISO(), percent, modelRate * 100, (modelRate - rate) * 10_000, pv);
        }

        private static double SwapPvAtCoupon(QL.OvernightIndexedSwap swap, decimal couponPercent, double notional)
        {
            // QuantLib's fixed leg BPS is the signed PV change for a 1 bp increase in the coupon.
            var couponChangeBp = ((double)(couponPercent / 100m) - swap.fixedRate()) * 10_000;
            return (swap.NPV() + couponChangeBp * swap.fixedLegBPS()) * notional / swap.nominal();
        }

        private static List<ForwardSample> SampleForwards(QL.YieldTermStructure curve, QL.Date referenceDate, QL.Date lastDate,
            QL.DateVector pillarDates, QL.DayCounter dayCount, List<FrontSegment> front)
        {
            var sampleDates = new SortedSet<int>();
            for (var i = 0; i < pillarDates.Count; i++)
            {
                using var date = pillarDates[i];
                sampleDates.Add(date.serialNumber());
            }

            for (var month = 1; ; month++)
            {
                using var period = new QL.Period(month, QL.TimeUnit.Months);
                using var date = referenceDate.Add(period);
                if (date.serialNumber() > lastDate.serialNumber())
                    break;
                sampleDates.Add(date.serialNumber());
            }

            var samples = new List<ForwardSample>(sampleDates.Count);
            foreach (var serial in sampleDates)
            {
                using var date = new QL.Date(serial);
                using var forward = curve.forwardRate(date, date, dayCount, QL.Compounding.Continuous);
                var calendarDate = ToDateOnly(date);
                // At a decision knot, report the new forward instead of a centered average across the step.
                var segment = front.Find(segment => segment.StartDate <= calendarDate && calendarDate < segment.EndDate);
                var value = segment?.CalibratedForwardPercent ?? forward.rate() * 100;
                if (!double.IsFinite(value))
                    throw new InvalidOperationException($"Curve produced a nonfinite forward rate on {date.ISO()}.");
                samples.Add(new ForwardSample(date.ISO(), RequireDiscount(curve.discount(date)), value));
            }
            return samples;
        }

        private static double RequireDiscount(double discount)
        {
            if (!double.IsFinite(discount) || discount <= 0)
                throw new InvalidOperationException("Curve produced a nonpositive or nonfinite discount factor.");
            return discount;
        }

        private static QL.Date ToQuantLibDate(DateOnly date) => new(date.Day, (QL.Month)date.Month, date.Year);
        private static DateOnly ToDateOnly(QL.Date date) => DateOnly.ParseExact(date.ISO(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        private static double YearFraction(DateOnly start, DateOnly end) => (end.DayNumber - start.DayNumber) / 365.0;

        private sealed record ProjectedSegment(DateOnly StartDate, DateOnly EndDate, DateOnly? DecisionDate,
            decimal ExpectedEffrPercent, double RawRate);
        private sealed record FrontSegment(DateOnly StartDate, DateOnly EndDate, DateOnly? DecisionDate,
            decimal ZqImpliedEffrPercent, double RawForwardPercent, double CalibratedForwardPercent, double DiscountFactorAtEnd);
        private sealed record Pillar(string Date, double DiscountFactor, double? ContinuousZeroPercent);
        private sealed record ForwardSample(string Date, double DiscountFactor, double InstantaneousForwardPercent);
        // Signed PV from the lender's perspective for the deposit and the fixed payer's perspective for OIS.
        private sealed record Repricing(string Instrument, string Tenor, string? Source, string? Method, int? Trades,
            int? CloseTrades, string StartDate, string MaturityDate, string PillarDate, decimal QuotePercent,
            double ModelPercent, double ResidualBp, double NpvPerMillion);
    }
}
