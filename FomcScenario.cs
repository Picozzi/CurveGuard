// using System.Text.Json;

// namespace CurveGuard;

// internal sealed record FedWatchOutcome(decimal TargetLowerPercent, decimal TargetUpperPercent,
//     decimal ProbabilityPercent);

// internal sealed record FomcMeeting(DateOnly DecisionDate, FedWatchOutcome[] Outcomes);

// internal sealed record FomcScenario(DateOnly ValuationDate, DateOnly FrontEndDate,
//     decimal CurrentTargetLowerPercent, decimal CurrentTargetUpperPercent, string ProbabilitySource,
//     FomcMeeting[] Meetings)
// {
//     public decimal CurrentTargetMidpointPercent => (CurrentTargetLowerPercent + CurrentTargetUpperPercent) / 2m;

//     public static async Task<FomcScenario> LoadAsync(string path)
//     {
//         using var stream = File.OpenRead(path);
//         var scenario = await JsonSerializer.DeserializeAsync<FomcScenario>(stream, new JsonSerializerOptions
//         {
//             PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
//             RespectRequiredConstructorParameters = true,
//         }) ?? throw new JsonException("Expected an FOMC probability snapshot");

//         if (string.IsNullOrWhiteSpace(scenario.ProbabilitySource) ||
//             !ValidTargetRange(scenario.CurrentTargetLowerPercent, scenario.CurrentTargetUpperPercent))
//         {
//             throw new FormatException("Specify the probability source and the current fed funds target range in percent");
//         }

//         if (scenario.Meetings is not { Length: 8 })
//             throw new FormatException("Supply the next eight FOMC policy decision dates and distributions");

//         var previousDate = scenario.ValuationDate;
//         foreach (var meeting in scenario.Meetings)
//         {
//             if (meeting is null || meeting.DecisionDate <= previousDate || meeting.DecisionDate >= scenario.FrontEndDate)
//                 throw new FormatException("Decision dates must be unique, ordered, and inside the modeled front end");

//             if (meeting.Outcomes is not { Length: > 0 } || meeting.Outcomes.Any(outcome =>
//                 outcome is null || outcome.ProbabilityPercent is < 0 or > 100 ||
//                 !ValidTargetRange(outcome.TargetLowerPercent, outcome.TargetUpperPercent)))
//             {
//                 throw new FormatException($"Invalid target range or probability for {meeting.DecisionDate:yyyy-MM-dd}");
//             }

//             var total = meeting.Outcomes.Sum(outcome => outcome.ProbabilityPercent);
//             if (Math.Abs(total - 100m) > 0.1m)
//                 throw new FormatException($"Probabilities must sum to 100% for {meeting.DecisionDate:yyyy-MM-dd}");

//             previousDate = meeting.DecisionDate;
//         }

//         return scenario;
//     }

//     private static bool ValidTargetRange(decimal lower, decimal upper) => lower >= -100m && upper <= 100m && lower <= upper;
// }
