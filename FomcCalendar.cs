// using System.Globalization;
// using System.Text.RegularExpressions;

// namespace CurveGuard;

// internal static class FomcCalendar
// {
//     public const string Source = "https://www.federalreserve.gov/monetarypolicy/fomccalendars.htm";

//     public static async Task<DateOnly[]> GetNextDecisionsAsync(HttpClient client, DateOnly after)
//     {
//         var html = await client.GetStringAsync(Source);
//         var headings = Regex.Matches(html, @">(?<year>\d{4}) FOMC Meetings<");
//         var dates = new SortedSet<DateOnly>();
//         for (var index = 0; index < headings.Count; index++)
//         {
//             var heading = headings[index];
//             var year = int.Parse(heading.Groups["year"].Value, CultureInfo.InvariantCulture);
//             if (year < after.Year)
//                 continue;

//             var end = index + 1 < headings.Count ? headings[index + 1].Index : html.Length;
//             var section = html[heading.Index..end];
//             var rows = Regex.Split(section, """(?=<div\s+class="[^"]*\brow\s+fomc-meeting(?=\s|"))""");
//             foreach (var row in rows.Skip(1))
//             {
//                 if (row.Contains("strategy meeting", StringComparison.OrdinalIgnoreCase) ||
//                     row.Contains("notation vote", StringComparison.OrdinalIgnoreCase))
//                     continue;

//                 var month = Regex.Match(row, """fomc-meeting__month[^>]*>\s*<strong>\s*(?<month>[A-Za-z/]+)\s*</strong>""");
//                 var days = Regex.Match(row, """fomc-meeting__date[^>]*>\s*(?:\d{1,2}\s*[-–]\s*)?(?<day>\d{1,2})\*?\s*</div>""");
//                 var finalMonth = month.Groups["month"].Value.Split('/')[^1];
//                 var dateText = $"{finalMonth}/{days.Groups["day"].Value}/{year}";
//                 if (!DateOnly.TryParseExact(dateText, ["MMMM/d/yyyy", "MMM/d/yyyy"], CultureInfo.InvariantCulture,
//                     DateTimeStyles.None, out var decision))
//                 {
//                     throw new FormatException($"Could not read an FOMC decision date in the {year} calendar");
//                 }

//                 if (decision > after)
//                     dates.Add(decision);
//             }
//         }

//         if (dates.Count < 8)
//             throw new FormatException($"The published calendar has {dates.Count} dated policy meetings after {after:yyyy-MM-dd}");

//         return dates.Take(8).ToArray();
//     }
// }
