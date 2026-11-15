using System.Text.Json;

namespace CurveGuard
{
    public class FOMCMeeting
    {
        //FedRateCalc. "FOMC meeting dates 2026-2027" (dataset), data as of November 6, 2026. CC BY 4.0. https://fedratecalc.com/data/fomc-meetings-2026-2027/
        
        private readonly DataRetrieval _dataRetrieval;
        private static readonly string FOMCMeetingPath = Path.Combine(
            AppContext.BaseDirectory, "MeetingCSVs", "fomc-meetings-2026-2027.json");
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

        internal async Task<List<DateTime>> LoadPolicyDecisionDatesAsync(DateTime valuationDate)
        {
            var dates = await LoadMeetingsAsync();
            // The Fed announced this revision on Nov 12, 2026. Earlier replays use the original schedule.
            if (valuationDate.Date < new DateTime(2026, 11, 12))
                return dates;

            return dates.Where(date => date != new DateTime(2027, 10, 27) && date != new DateTime(2027, 12, 8))
                .Append(new DateTime(2027, 11, 17)).Order().ToList();
        }
    }
}
