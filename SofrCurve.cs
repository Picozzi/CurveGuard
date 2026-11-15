using QL = QuantLib;

namespace CurveGuard;

internal sealed record CurvePillar(string Date, double? DiscountFactor);
internal sealed record Repricing(string Tenor, string QuoteId, string MaturityDate, string PillarDate,
    decimal QuotedRate, double? ImpliedRate, double? ResidualBp);
internal sealed record CurveReport(string ReferenceDate, List<CurvePillar> Pillars, List<Repricing> Repricing);
internal sealed record CurveMeasurements(CurveReport Curve, bool PositiveDiscountFactors,
    int SampledDateCount, double? MaxAbsoluteResidualBp);

internal static class SofrCurve
{
    // Keep the reported engine version in sync with the package pin in CurveGuard.csproj.
    public const string QuantLibVersion = "1.44";
    private const uint SettlementDays = 2;
    private const int PaymentLagDays = 2;

    public static readonly object Conventions = new
    {
        SettlementDays,
        PaymentLagDays,
        Calendar = "US Government Bond",
        CouponFrequency = "Annual",
        FixedDayCount = "Actual/360",
        FloatingDayCount = "Actual/360",
        Averaging = "Compounded SOFR",
        AccrualAdjustment = "Modified Following",
        PaymentAdjustment = "Following",
        EndOfMonth = false,
        ObservationShift = false,
        LockoutDays = 0,
        ScheduleRule = "Backward",
        Pillar = "Last relevant date",
        CurveDayCount = "Actual/365 (Fixed)",
        Interpolation = "Log-linear discount factors",
        Extrapolation = false,
    };

    public static CurveMeasurements Build(Snapshot snapshot, DateOnly valuationDate)
    {
        using var referenceDate = new QL.Date(valuationDate.Day, (QL.Month)valuationDate.Month, valuationDate.Year);
        using var calendar = new QL.UnitedStates(QL.UnitedStates.Market.GovernmentBond);
        if (!calendar.isBusinessDay(referenceDate))
            throw new ArgumentException("valuation date must be a US Government Bond business day");

        using var settings = QL.Settings.instance();
        using var previousDate = settings.getEvaluationDate();
        settings.setEvaluationDate(referenceDate);
        try
        {
            using var index = new QL.Sofr();
            using var helpers = new QL.RateHelperVector();
            var quotes = MarketData.Tenors.Where(snapshot.Quotes.ContainsKey)
                .Select(tenor => snapshot.Quotes[tenor]).ToArray();
            foreach (var quote in quotes)
            {
                using var helper = CreateHelper(quote, index, calendar);
                helpers.Add(helper);
            }

            using var dayCount = new QL.Actual365Fixed();
            using var curve = new QL.PiecewiseLogLinearDiscount(referenceDate, helpers, dayCount);
            return Measure(curve, referenceDate, quotes, helpers);
        }
        finally
        {
            settings.setEvaluationDate(previousDate);
        }
    }

    private static QL.OISRateHelper CreateHelper(Quote quote, QL.Sofr index, QL.Calendar calendar)
    {
        using var tenor = new QL.Period(quote.Tenor);
        using var simpleQuote = new QL.SimpleQuote((double)quote.Rate);
        using var rate = new QL.QuoteHandle(simpleQuote);
        using var discountCurve = new QL.YieldTermStructureHandle();
        using var forwardStart = new QL.Period(0, QL.TimeUnit.Days);
        using var customPillarDate = new QL.Date();
        using var endOfMonth = new QL.OptionalBool(false);
        using var fixedFrequency = new QL.OptionalFrequency();
        using var fixedDayCount = new QL.Actual360();

        return new QL.OISRateHelper(SettlementDays, tenor, rate, index, discountCurve,
            telescopicValueDates: false, paymentLag: PaymentLagDays,
            paymentConvention: QL.BusinessDayConvention.Following, paymentFrequency: QL.Frequency.Annual,
            paymentCalendar: calendar, forwardStart: forwardStart, overnightSpread: 0,
            pillar: QL.Pillar.Choice.LastRelevantDate, customPillarDate: customPillarDate,
            averagingMethod: QL.RateAveraging.Type.Compound, endOfMonth: endOfMonth,
            fixedPaymentFrequency: fixedFrequency, fixedCalendar: calendar, lookbackDays: 0, lockoutDays: 0,
            applyObservationShift: false, pricer: null, rule: QL.DateGeneration.Rule.Backward,
            overnightCalendar: calendar, convention: QL.BusinessDayConvention.ModifiedFollowing,
            fixedDayCount: fixedDayCount);
    }

    private static CurveMeasurements Measure(QL.PiecewiseLogLinearDiscount curve, QL.Date referenceDate,
        Quote[] quotes, QL.RateHelperVector helpers)
    {
        // QuantLib calibrates lazily. Reading the pillar data forces the bootstrap.
        using var pillarDates = curve.dates();
        using var pillarValues = curve.data();
        using var maxDate = curve.maxDate();
        var pillars = new List<CurvePillar>();
        var sampleDates = new SortedSet<int>();
        for (var index = 0; index < pillarDates.Count; index++)
        {
            using var date = pillarDates[index];
            pillars.Add(new CurvePillar(date.ISO(), Finite(pillarValues[index])));
            sampleDates.Add(date.serialNumber());
        }

        var maxSerial = maxDate.serialNumber();
        for (var month = 1; ; month++)
        {
            using var period = new QL.Period(month, QL.TimeUnit.Months);
            using var date = referenceDate.Add(period);
            if (date.serialNumber() >= maxSerial)
                break;
            sampleDates.Add(date.serialNumber());
        }

        var positiveDiscountFactors = pillars.All(pillar => pillar.DiscountFactor is > 0);
        foreach (var serial in sampleDates)
        {
            using var date = new QL.Date(serial);
            var discount = curve.discount(date);
            positiveDiscountFactors &= double.IsFinite(discount) && discount > 0;
        }

        var repricing = new List<Repricing>();
        for (var index = 0; index < quotes.Length; index++)
        {
            var quote = quotes[index];
            using var helper = helpers[index];
            using var maturity = helper.maturityDate();
            using var pillar = helper.pillarDate();
            var implied = helper.impliedQuote();
            repricing.Add(new Repricing(quote.Tenor, quote.Id, maturity.ISO(), pillar.ISO(), quote.Rate,
                Finite(implied), Finite((implied - (double)quote.Rate) * 10_000)));
        }

        double? maxResidual = repricing.All(row => row.ResidualBp.HasValue)
            ? repricing.Max(row => Math.Abs(row.ResidualBp!.Value))
            : null;
        return new CurveMeasurements(new CurveReport(referenceDate.ISO(), pillars, repricing),
            positiveDiscountFactors, sampleDates.Count, maxResidual);
    }

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;
}
