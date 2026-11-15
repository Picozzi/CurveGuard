namespace CurveGuard
{
    public class FOMCCalculator
    {
        private const decimal PolicyStep = 0.25m;
        private readonly DateTime effectiveDate;
        public FOMCCalculator(DateTime effectiveDate)
        {
            this.effectiveDate = effectiveDate;
        }

        public async Task GetFOMCResults()
        {
            EFFRCalculator effrCalc = new EFFRCalculator();
            var effrFixing = await effrCalc.GetEffrFixingAsync(effectiveDate);

            //Scheduled FOMC Decision Dates
            FOMCMeeting meeting = new FOMCMeeting();
            List<DateTime> fomcDates = await meeting.LoadMeetingsAsync();

            // 3. Fetch historical 30-Day Fed Funds (ZQ) futures closes from Yahoo Finance
            FutureImpliedRate futureImpliedRate = new FutureImpliedRate(effectiveDate);
            await futureImpliedRate.GetFuturesImpliedRates();
            var futuresImpliedRates = futureImpliedRate.futuresImpliedRates;

            var results = CalculateFedWatch(effectiveDate, (decimal)effrFixing.TargetLower,
                (decimal)effrFixing.TargetUpper, fomcDates, futuresImpliedRates);

            Console.WriteLine($"ZQ-implied FOMC probabilities (As of {effectiveDate:yyyy-MM-dd} | EFFR: {effrFixing.Rate:F2}%)\n");
            foreach (var result in results)
            {
                var ranges = result.TargetRanges.Select(range =>
                    $"Range [{range.Lower:F2}%-{range.Upper:F2}%]: {range.Probability:P1}");
                var moveBps = (result.PostMeetingRate - result.PreMeetingRate) * 100m;
                Console.WriteLine($"Meeting: {result.MeetingDate:yyyy-MM-dd} | Implied EFFR: {result.PostMeetingRate:F3}% | " +
                    $"Meeting move: {moveBps:+0.0;-0.0;0.0} bp | {string.Join(" | ", ranges)}");
            }
        }

        private static List<MeetingProbability> CalculateFedWatch(DateTime asOfDate, decimal targetLower, decimal targetUpper,
            List<DateTime> fomcDates, Dictionary<(int Year, int Month), decimal> futuresRates)
        {
            var upcomingMeetings = fomcDates.Where(date => date.Date > asOfDate.Date).OrderBy(date => date).ToList();
            if (upcomingMeetings.Count == 0)
                return [];

            var ratesByMonth = futuresRates.ToDictionary(quote => new DateTime(quote.Key.Year, quote.Key.Month, 1),
                quote => quote.Value);
            var meetingsByMonth = fomcDates.ToLookup(MonthOf);
            var valuationMonth = MonthOf(asOfDate);
            // The bundled calendar covers complete calendar years.
            var calendarYear = upcomingMeetings[^1].Year;
            var meetingRates = InferMeetingRates(valuationMonth, calendarYear, meetingsByMonth, ratesByMonth);
            var output = new List<MeetingProbability>();
            var distribution = new Dictionary<int, decimal> { [0] = 1m };
            foreach (var meeting in upcomingMeetings)
            {
                var month = MonthOf(meeting);
                if (!meetingRates.TryGetValue(month, out var rates) || rates.Start < 0m || rates.End < 0m)
                {
                    string reason = !ratesByMonth.ContainsKey(month) ? $"missing {month:yyyy-MM} ZQ close"
                        : meetingsByMonth[month].Count() > 1 ? "multiple meetings in the same contract month"
                        : meetingRates.ContainsKey(month) ? "the inferred EFFR is negative"
                        : "missing a usable ZQ anchor from a month without a meeting";
                    Console.Error.WriteLine($"Could not infer the rate path from {meeting:yyyy-MM-dd} onward: {reason}.");
                    break;
                }

                var expectedSteps = decimal.Round((rates.End - rates.Start) / PolicyStep, 12);
                distribution = ApplyMeetingMove(distribution, expectedSteps);
                var targetRanges = distribution.OrderBy(outcome => outcome.Key)
                    .Select(outcome => new TargetRangeProbability(targetLower + outcome.Key * PolicyStep,
                        targetUpper + outcome.Key * PolicyStep, outcome.Value))
                    .ToList();
                output.Add(new MeetingProbability(meeting, rates.Start, rates.End, targetRanges));
            }
            return output;
        }

        private static Dictionary<DateTime, (decimal Start, decimal End)> InferMeetingRates(DateTime valuationMonth,
            int calendarYear, ILookup<DateTime, DateTime> meetings, Dictionary<DateTime, decimal> monthlyRates)
        {
            var rates = new Dictionary<DateTime, (decimal Start, decimal End)>();
            var anchors = monthlyRates.Where(quote => quote.Key > valuationMonth && quote.Key.Year <= calendarYear &&
                !meetings.Contains(quote.Key));

            foreach (var (anchorMonth, anchorRate) in anchors)
            {
                // A meeting-free contract pins the end of the prior month. Solve earlier meetings backward.
                var endRate = anchorRate;
                for (var month = anchorMonth.AddMonths(-1); month >= valuationMonth; month = month.AddMonths(-1))
                {
                    if (!TryGetMeetingMonth(month, out var average, out var daysBefore, out var daysAfter))
                        break;

                    var startRate = (average * (daysBefore + daysAfter) - endRate * daysAfter) / daysBefore;
                    rates[month] = (startRate, endRate);
                    endRate = startRate;
                }

                // A preceding anchor can solve one meeting forward if a later anchor hasn't solved it backward.
                var followingMonth = anchorMonth.AddMonths(1);
                if (TryGetMeetingMonth(followingMonth, out var nextAverage, out var nextDaysBefore, out var nextDaysAfter) &&
                    nextDaysAfter > 0)
                {
                    var nextEnd = (nextAverage * (nextDaysBefore + nextDaysAfter) - anchorRate * nextDaysBefore) / nextDaysAfter;
                    rates.TryAdd(followingMonth, (anchorRate, nextEnd));
                }
            }
            return rates;

            bool TryGetMeetingMonth(DateTime month, out decimal average, out int daysBefore, out int daysAfter)
            {
                daysBefore = 0;
                daysAfter = 0;
                if (!monthlyRates.TryGetValue(month, out average) || meetings[month].Take(2).ToArray() is not [var meeting])
                    return false;

                daysBefore = meeting.Day; // The new rate takes effect after the decision day.
                daysAfter = DateTime.DaysInMonth(month.Year, month.Month) - daysBefore;
                return true;
            }
        }

        private static Dictionary<int, decimal> ApplyMeetingMove(Dictionary<int, decimal> distribution, decimal expectedSteps)
        {
            var lowerSteps = (int)decimal.Floor(expectedSteps);
            var upperProbability = expectedSteps - lowerSteps;
            var next = new Dictionary<int, decimal>();
            foreach (var (steps, probability) in distribution)
            {
                var lower = steps + lowerSteps;
                next[lower] = next.GetValueOrDefault(lower) + probability * (1m - upperProbability);
                if (upperProbability > 0m)
                    next[lower + 1] = next.GetValueOrDefault(lower + 1) + probability * upperProbability;
            }
            return next;
        }

        private static DateTime MonthOf(DateTime date) => new(date.Year, date.Month, 1);

        public sealed record MeetingProbability(DateTime MeetingDate, decimal PreMeetingRate, decimal PostMeetingRate,
            IReadOnlyList<TargetRangeProbability> TargetRanges);

        public sealed record TargetRangeProbability(decimal Lower, decimal Upper, decimal Probability);
    }
}
