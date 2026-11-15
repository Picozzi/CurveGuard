// using System.Globalization;
// using QL = QuantLib;

// namespace CurveGuard;

// internal static class FomcSofrCurve
// {
//     private sealed record Segment(DateOnly StartDate, DateOnly EndDate, decimal ExpectedTargetMidpointPercent,
//         decimal ForwardSofrPercent, double DiscountFactorAtEnd);

//     public static DateOnly PreviousFixingDate(DateOnly valuationDate)
//     {
//         using var calendar = new QL.UnitedStates(QL.UnitedStates.Market.GovernmentBond);
//         using var date = QuantLibDate(valuationDate);
//         if (!calendar.isBusinessDay(date))
//             throw new ArgumentException("Valuation date must be a US Government Bond business day");

//         using var previous = calendar.advance(date, -1, QL.TimeUnit.Days);
//         return DateOnly.ParseExact(previous.ISO(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
//     }

//     public static object Build(FomcScenario scenario, OvernightSOFR.SOFRFixing fixing, OISSOFR.OISSwapSnapshot swaps)
//     {
//         if (fixing.EffectiveDate != PreviousFixingDate(scenario.ValuationDate) ||
//             DateOnly.FromDateTime(swaps.ObservationDate) != scenario.ValuationDate)
//         {
//             throw new ArgumentException("SOFR fixing or OIS snapshot date does not match the valuation inputs");
//         }

//         var segments = ProjectSegments(scenario, fixing.RatePercent);
//         using var settings = QL.Settings.instance();
//         using var previousDate = settings.getEvaluationDate();
//         using var referenceDate = QuantLibDate(scenario.ValuationDate);
//         settings.setEvaluationDate(referenceDate);
//         try
//         {
//             using var helpers = new QL.RateHelperVector();
//             using var dayCount = new QL.Actual365Fixed();
//             using var unadjustedCalendar = new QL.NullCalendar();
//             foreach (var segment in segments)
//             {
//                 var days = segment.EndDate.DayNumber - scenario.ValuationDate.DayNumber;
//                 using var tenor = new QL.Period(days, QL.TimeUnit.Days);
//                 // The synthetic deposit pins the integrated front-end discount factor at this boundary.
//                 var equivalentDepositRate = (1.0 / segment.DiscountFactorAtEnd - 1.0) / (days / 365.0);
//                 using var helper = new QL.DepositRateHelper(equivalentDepositRate, tenor, 0, unadjustedCalendar,
//                     QL.BusinessDayConvention.Unadjusted, false, dayCount);
//                 helpers.Add(helper);
//             }

//             using var sofrIndex = new QL.Sofr();
//             using var calendar = new QL.UnitedStates(QL.UnitedStates.Market.GovernmentBond);
//             using var frontEndDate = QuantLibDate(scenario.FrontEndDate);
//             var marketQuotes = new List<(string Tenor, decimal RatePercent)>();
//             var overlappingTenors = new List<string>();
//             foreach (var quote in swaps.Quotes)
//             {
//                 if (quote.Tenor is not { } tenor || quote.Rate is not { } rate)
//                     throw new FormatException("Each OIS quote must have a tenor and par rate");

//                 using var helper = CreateOisHelper(tenor, rate, sofrIndex, calendar);
//                 using var maturity = helper.maturityDate();
//                 if (maturity.serialNumber() <= frontEndDate.serialNumber())
//                 {
//                     overlappingTenors.Add(tenor);
//                     continue;
//                 }

//                 helpers.Add(helper);
//                 marketQuotes.Add((tenor, rate));
//             }

//             if (marketQuotes.Count == 0)
//                 throw new InvalidOperationException("No OIS quote matures beyond the modeled front end");

//             using var curve = new QL.PiecewiseLogLinearDiscount(referenceDate, helpers, dayCount);
//             using var dates = curve.dates();
//             using var discounts = curve.data();
//             var pillars = Enumerable.Range(0, dates.Count).Select(index =>
//             {
//                 using var date = dates[index];
//                 return new { Date = date.ISO(), DiscountFactor = discounts[index] };
//             }).ToArray();

//             var repricing = marketQuotes.Select((quote, index) =>
//             {
//                 using var helper = helpers[segments.Count + index];
//                 var implied = helper.impliedQuote();
//                 return new { quote.Tenor, quote.RatePercent, ImpliedRatePercent = implied * 100,
//                     ResidualBp = (implied - (double)(quote.RatePercent / 100m)) * 10_000 };
//             }).ToArray();

//             var maxFrontEndError = segments.Max(segment =>
//             {
//                 using var date = QuantLibDate(segment.EndDate);
//                 return Math.Abs(curve.discount(date) - segment.DiscountFactorAtEnd);
//             });
//             if (!double.IsFinite(maxFrontEndError) || maxFrontEndError > 1e-9 ||
//                 pillars.Any(pillar => !double.IsFinite(pillar.DiscountFactor) || pillar.DiscountFactor <= 0) ||
//                 repricing.Any(row => !double.IsFinite(row.ResidualBp) || Math.Abs(row.ResidualBp) > 0.01))
//             {
//                 throw new InvalidOperationException("The bootstrapped curve did not fit the front-end targets or OIS quotes");
//             }

//             return new
//             {
//                 Mode = "synthetic_fomc_front_end",
//                 scenario.ValuationDate, scenario.FrontEndDate, scenario.ProbabilitySource,
//                 scenario.CurrentTargetMidpointPercent, Fixing = fixing,
//                 SofrToTargetMidpointSpreadBp = (fixing.RatePercent - scenario.CurrentTargetMidpointPercent) * 100m,
//                 OisSource = swaps.Url, OisProviderCollectionTime = swaps.ProviderCollectionTime,
//                 OverlappingOisTenors = overlappingTenors,
//                 ForwardSegments = segments, MaxFrontEndDiscountFactorError = maxFrontEndError,
//                 Pillars = pillars, OisRepricing = repricing,
//             };
//         }
//         finally
//         {
//             settings.setEvaluationDate(previousDate);
//         }
//     }

//     private static List<Segment> ProjectSegments(FomcScenario scenario, decimal anchorPercent)
//     {
//         var segments = new List<Segment>();
//         var start = scenario.ValuationDate;
//         var target = scenario.CurrentTargetMidpointPercent;
//         var basis = anchorPercent - target;
//         var discount = 1.0;

//         foreach (var meeting in scenario.Meetings)
//         {
//             AddSegment(meeting.DecisionDate);
//             target = meeting.Outcomes.Sum(outcome =>
//                 (outcome.TargetLowerPercent + outcome.TargetUpperPercent) / 2m * outcome.ProbabilityPercent) /
//                 meeting.Outcomes.Sum(outcome => outcome.ProbabilityPercent);
//         }
//         AddSegment(scenario.FrontEndDate);
//         return segments;

//         void AddSegment(DateOnly end)
//         {
//             var forwardPercent = target + basis;
//             var calendarDays = end.DayNumber - start.DayNumber;
//             discount *= Math.Pow(1.0 + (double)(forwardPercent / 100m) / 360.0, -calendarDays);
//             segments.Add(new Segment(start, end, target, forwardPercent, discount));
//             start = end;
//         }
//     }

//     private static QL.Date QuantLibDate(DateOnly date) => new(date.Day, (QL.Month)date.Month, date.Year);

//     private static QL.OISRateHelper CreateOisHelper(string maturity, decimal percent, QL.Sofr index, QL.Calendar calendar)
//     {
//         using var tenor = new QL.Period(maturity);
//         using var quote = new QL.SimpleQuote((double)(percent / 100m));
//         using var rate = new QL.QuoteHandle(quote);
//         using var discountCurve = new QL.YieldTermStructureHandle();
//         using var forwardStart = new QL.Period(0, QL.TimeUnit.Days);
//         using var customPillar = new QL.Date();
//         using var endOfMonth = new QL.OptionalBool(false);
//         using var fixedFrequency = new QL.OptionalFrequency();
//         using var dayCount = new QL.Actual360();
//         return new QL.OISRateHelper(2, tenor, rate, index, discountCurve, telescopicValueDates: false,
//             paymentLag: 2, paymentConvention: QL.BusinessDayConvention.Following,
//             paymentFrequency: QL.Frequency.Annual, paymentCalendar: calendar,
//             forwardStart: forwardStart, overnightSpread: 0, pillar: QL.Pillar.Choice.LastRelevantDate,
//             customPillarDate: customPillar, averagingMethod: QL.RateAveraging.Type.Compound,
//             endOfMonth: endOfMonth, fixedPaymentFrequency: fixedFrequency, fixedCalendar: calendar,
//             lookbackDays: 0, lockoutDays: 0, applyObservationShift: false, pricer: null,
//             rule: QL.DateGeneration.Rule.Backward, overnightCalendar: calendar,
//             convention: QL.BusinessDayConvention.ModifiedFollowing, fixedDayCount: dayCount);
//     }
// }
