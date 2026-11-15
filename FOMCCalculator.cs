using System.Text.Json;

namespace CurveGuard
{
    public class FOMCCalculator
    {
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

            // 3. Fetch 30-Day Fed Funds (ZQ) futures settlements from Yahoo Finance
            FutureImpliedRate futureImpliedRate = new FutureImpliedRate(effectiveDate);
            await futureImpliedRate.GetFuturesImpliedRates();
            var futuresImpliedRates = futureImpliedRate.futuresImpliedRates;

            // 4. Calculate CME FedWatch Probabilities
            var results = CalculateFedWatch(effectiveDate, effrFixing.Rate, effrFixing.TargetLower, effrFixing.TargetUpper, fomcDates, futuresImpliedRates);

            Console.WriteLine($"CME FedWatch Implied Probabilities (As of {effectiveDate:yyyy-MM-dd} | EFFR: {effrFixing.Rate:F2}%)\n");
            foreach (var r in results)
            {
                Console.WriteLine($"Meeting: {r.MeetingDate:yyyy-MM-dd} | Implied Rate: {r.PostMeetingRate:F3}% | " +
                                $"Range [{r.LowerRange:F2}%-{r.UpperRange:F2}%]: {r.ProbLower:P1} | " +
                                $"Range [{r.ShiftedLower:F2}%-{r.ShiftedUpper:F2}%]: {r.ProbShifted:P1}");
            }
        }

        private static List<MeetingProbability> CalculateFedWatch(DateTime asOfDate, double currentEffr, double targetLower, double targetUpper, List<DateTime> fomcDates, Dictionary<(int Year, int Month), decimal> futuresRates)
        {
            var output = new List<MeetingProbability>();
            var upcomingMeetings = fomcDates.Where(d => d > asOfDate).OrderBy(d => d).ToList();

            double prevRate = currentEffr;
            double currLower = targetLower;
            double currUpper = targetUpper;

            foreach (var meeting in upcomingMeetings)
            {
                var key = (meeting.Year, meeting.Month);
                if (!futuresRates.TryGetValue(key, out decimal impliedMonthAvg)) break;

                int daysInMonth = DateTime.DaysInMonth(meeting.Year, meeting.Month);
                int daysBefore = meeting.Day; // Rate change takes effect the day after the FOMC statement
                int daysAfter = daysInMonth - daysBefore;

                // Check if there is a second FOMC meeting in the same calendar month
                if (upcomingMeetings.Any(m => m != meeting && m.Year == meeting.Year && m.Month == meeting.Month))
                    break; // Multi-meeting months require anchoring off an adjacent non-meeting month

                // CME FedWatch Formula: Solve for post-meeting EFFR
                double postRate = ((double)impliedMonthAvg * daysInMonth - prevRate * daysBefore) / daysAfter;

                // Calculate probability of a 25 bps move relative to the current bracket midpoint
                double midPoint = (currLower + currUpper) / 2.0;
                double spread = postRate - midPoint;
                double probMove = Math.Clamp(Math.Abs(spread) / 0.25, 0.0, 1.0);
                int direction = spread >= 0 ? 1 : -1;

                output.Add(new MeetingProbability
                {
                    MeetingDate = meeting,
                    PostMeetingRate = postRate,
                    LowerRange = currLower,
                    UpperRange = currUpper,
                    ProbLower = 1.0 - probMove,
                    ShiftedLower = currLower + direction * 0.25,
                    ShiftedUpper = currUpper + direction * 0.25,
                    ProbShifted = probMove
                });

                // Roll forward expected EFFR and bracket for the next meeting node
                prevRate = postRate;
                if (probMove > 0.5)
                {
                    currLower += direction * 0.25;
                    currUpper += direction * 0.25;
                }
            }
            return output;
        }

        public class MeetingProbability
        {
            public DateTime MeetingDate { get; set; }
            public double PostMeetingRate { get; set; }
            public double LowerRange { get; set; }
            public double UpperRange { get; set; }
            public double ProbLower { get; set; }
            public double ShiftedLower { get; set; }
            public double ShiftedUpper { get; set; }
            public double ProbShifted { get; set; }
        }
    }
}
