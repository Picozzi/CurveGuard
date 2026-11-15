using System.Text.Json;

namespace CurveGuard
{
    public class FOMCCalculator
    {
        public class FOMCMeeting
        {
            //FedRateCalc. "FOMC meeting dates 2026-2027" (dataset), data as of November 6, 2026. CC BY 4.0. https://fedratecalc.com/data/fomc-meetings-2026-2027/
           
            private readonly DataRetrieval _dataRetrieval;
            private const string FOMCMeetingPath = "MeetingCSVs/fomc-meetings-2026-2027.json";
            public List<DateTime> MeetingDates { get; set; } = new();
            public sealed record MeetingRow(DateOnly MeetingStartDate, DateOnly MeetingEndDate);
            private sealed record MeetingFile(MeetingRow[] Rows);

            public FOMCMeeting()
            {
                JsonSerializerOptions jsonOptions = new()
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                    RespectRequiredConstructorParameters = true,
                };

                _dataRetrieval = new DataRetrieval(jsonOptions);
            }

            public async Task<List<DateTime>> LoadMeetingsAsync()
            {
                var file = await _dataRetrieval.GetJsonAsync<MeetingFile>(FOMCMeetingPath);
                if (file.Rows is null || file.Rows.Any(row => row is null || row.MeetingEndDate < row.MeetingStartDate))
                    throw new JsonException("The FOMC meeting file must contain rows with valid start and end dates.");

                MeetingDates = file.Rows.Select(row => row.MeetingEndDate.ToDateTime(TimeOnly.MinValue)).ToList();
                return MeetingDates;
            }
        }

        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        
        private static readonly char[] MonthCodes = { 'F', 'G', 'H', 'J', 'K', 'M', 'N', 'Q', 'U', 'V', 'X', 'Z' };


        public FOMCCalculator()
        {}

        public async Task GetFOMCResults()
        {
            EFFRCalculator effrCalc = new EFFRCalculator();
            var effrFixing = await effrCalc.GetLatestEffrAsync();

            //Scheduled FOMC Decision Dates
            FOMCMeeting meeting = new FOMCMeeting();
            List<DateTime> fomcDates = await meeting.LoadMeetingsAsync();

            // 3. Fetch 30-Day Fed Funds (ZQ) futures settlements from Yahoo Finance
            var futuresImpliedRates = new Dictionary<(int Year, int Month), double>();
            for (int i = 0; i < 14; i++)
            {
                var dt = asOfDate.AddMonths(i);
                string ticker = $"ZQ{MonthCodes[dt.Month - 1]}{dt.Year % 100:D2}.CBT";
                double? closePrice = await GetYahooClosePriceAsync(ticker);
                if (closePrice.HasValue)
                {
                    futuresImpliedRates[(dt.Year, dt.Month)] = 100.0 - closePrice.Value;
                }
            }

            // 4. Calculate CME FedWatch Probabilities
            var results = CalculateFedWatch(asOfDate, effr, targetLower, targetUpper, fomcDates, futuresImpliedRates);

            Console.WriteLine($"CME FedWatch Implied Probabilities (As of {asOfDate:yyyy-MM-dd} | EFFR: {effr:F2}%)\n");
            foreach (var r in results)
            {
                Console.WriteLine($"Meeting: {r.MeetingDate:yyyy-MM-dd} | Implied Rate: {r.PostMeetingRate:F3}% | " +
                                $"Range [{r.LowerRange:F2}%-{r.UpperRange:F2}%]: {r.ProbLower:P1} | " +
                                $"Range [{r.ShiftedLower:F2}%-{r.ShiftedUpper:F2}%]: {r.ProbShifted:P1}");
            }
        }



        private static async Task<double?> GetYahooClosePriceAsync(string ticker)
        {
            string url = $"https://yahoo.com{ticker}?range=5d&interval=1d";
            try
            {
                string json = await Http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var closes = doc.RootElement
                    .GetProperty("chart").GetProperty("result")[0]
                    .GetProperty("indicators").GetProperty("quote")[0]
                    .GetProperty("close");

                for (int i = closes.GetArrayLength() - 1; i >= 0; i--)
                {
                    if (closes[i].ValueKind == JsonValueKind.Number)
                        return closes[i].GetDouble();
                }
            }
            catch { /* Contract illiquid or unlisted */ }
            return null;
        }

        private static List<MeetingProbability> CalculateFedWatch(DateTime asOfDate, double currentEffr, double targetLower, double targetUpper, List<DateTime> fomcDates, Dictionary<(int Year, int Month), double> futuresRates)
        {
            var output = new List<MeetingProbability>();
            var upcomingMeetings = fomcDates.Where(d => d > asOfDate).OrderBy(d => d).ToList();

            double prevRate = currentEffr;
            double currLower = targetLower;
            double currUpper = targetUpper;

            foreach (var meeting in upcomingMeetings)
            {
                var key = (meeting.Year, meeting.Month);
                if (!futuresRates.TryGetValue(key, out double impliedMonthAvg)) break;

                int daysInMonth = DateTime.DaysInMonth(meeting.Year, meeting.Month);
                int daysBefore = meeting.Day; // Rate change takes effect the day after the FOMC statement
                int daysAfter = daysInMonth - daysBefore;

                // Check if there is a second FOMC meeting in the same calendar month
                if (upcomingMeetings.Any(m => m != meeting && m.Year == meeting.Year && m.Month == meeting.Month))
                    break; // Multi-meeting months require anchoring off an adjacent non-meeting month

                // CME FedWatch Formula: Solve for post-meeting EFFR
                double postRate = (impliedMonthAvg * daysInMonth - prevRate * daysBefore) / daysAfter;

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
