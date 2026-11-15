using System.Globalization;
using System.Text.Json;
using QuantLib;

namespace CurveGuard;

internal static class Program
{
    private const string Help = """
        Build and validate a USD SOFR OIS curve.

        --fomc PATH
            Build a synthetic SOFR front end from a saved FOMC probability snapshot and live market data sources.

        --fomc-calendar YYYY-MM-DD
            List the next eight policy decision dates in the currently published Fed calendar.

        --fomc-meetings PATH
            Read all meeting start and end dates from a local JSON file.

        --sofr [YYYY-MM-DD] --url URL
            Fetch a SOFR fixing. Supply the effective date to validate the response date.

        --ois YYYY-MM-DD --url URL
            Fetch a dated USD SOFR OIS snapshot in CheckMySwap format.

        --market-date YYYY-MM-DD --url URL
            Fetch a dated OIS snapshot and run the curve guard.

        --candidate PATH --reference PATH --as-of ISO_TIMESTAMP
            Evaluate two local JSON snapshots. The timestamp must include a UTC offset.

        Exit status: 0 = success or PASS, 1 = retrieval failure or QUARANTINE, 2 = invalid arguments.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public static async Task<int> Main(string[] args)
    {  
        DateTime valuationDate = new DateTime(2026, 11, 12);

        FOMCCalculator cc = new FOMCCalculator();
        await cc.GetFOMCResults();

        OvernightSOFR sofr = new OvernightSOFR();
        OISSOFR ois = new OISSOFR();

        try
        {
            await cc.GetFOMCResults();
            var sofrRate = await sofr.GetSOFRFixingAsync(valuationDate);
            var oisSnapshot = await ois.GetOisSwapRatesAsync(valuationDate);
            Console.WriteLine(JsonSerializer.Serialize(new { Sofr = sofrRate, Ois = oisSnapshot }, JsonOptions));
            return 0;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or KeyNotFoundException
            or OperationCanceledException or FormatException)
        {
            Console.Error.WriteLine($"CurveGuard: {error.Message}");
            return 1;
        }












        // if (args.Length > 0 && args[0] is "--sofr" or "--ois" or "--market-date")
        // {
        //     if (args.Length < 3 || args[^2] != "--url" ||
        //         !Uri.TryCreate(args[^1], UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        //     {
        //         return UsageError("finish the command with --url followed by a full HTTP or HTTPS URL");
        //     }

        //     using var httpClient = new 
        //     var retrieval = new DataRetrieval(httpClient);
        //     var command = args[..^2];
        //     return command[0] == "--sofr"
        //         ? await FetchSofr(command, retrieval, url)
        //         : await FetchOis(command, retrieval, url);
        // }


        












        // string? candidate = null;
        // string? reference = null;
        // DateTimeOffset? asOf = null;
        // DateOnly? marketDate = null;
        // var seen = new HashSet<string>();
        // for (var index = 0; index < args.Length; index += 2)
        // {
        //     var name = args[index];
        //     if (index + 1 == args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        //         return UsageError($"{name} requires a value");
        //     if (!seen.Add(name))
        //         return UsageError($"duplicate option {name}");

        //     var value = args[index + 1];
        //     switch (name)
        //     {
        //         case "--candidate":
        //             candidate = value;
        //             break;
        //         case "--reference":
        //             reference = value;
        //             break;
        //         case "--as-of":
        //             try
        //             {
        //                 asOf = MarketData.ParseTimestamp(value);
        //             }
        //             catch (FormatException error)
        //             {
        //                 return UsageError($"invalid --as-of: {error.Message}");
        //             }
        //             break;
        //         case "--market-date":
        //             if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
        //                 DateTimeStyles.None, out var parsedDate))
        //             {
        //                 return UsageError("--market-date requires a date in YYYY-MM-DD format");
        //             }
        //             marketDate = parsedDate;
        //             break;
        //         default:
        //             return UsageError($"unknown option {name}");
        //     }
        // }

        // Report report;
        // if (marketDate is { } date)
        // {
        //     if (seen.Count != 1)
        //         return UsageError("--market-date cannot be combined with local snapshot options");
        //     report = await Guard.ReplayMarket(date);
        // }
        // else
        // {
        //     if (candidate is null || reference is null || asOf is null)
        //         return UsageError("provide --market-date or all three local snapshot options");
        //     report = await Guard.EvaluateFiles(candidate, reference, asOf.Value);
        // }

        // Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        // return report.Decision == "PASS" ? 0 : 1;

    }

    

    // private static async Task<int> BuildFomcCurve(string path)
    // {
    //     try
    //     {
    //         var scenario = await FomcScenario.LoadAsync(path);
    //         var fixingDate = FomcSofrCurve.PreviousFixingDate(scenario.ValuationDate);
    //         var fixingTask = new OvernightSOFR().GetSOFRFixingAsync(fixingDate.ToDateTime(TimeOnly.MinValue));
    //         var swapsTask = new OISSOFR().GetOisSwapRatesAsync(scenario.ValuationDate.ToDateTime(TimeOnly.MinValue));
    //         await Task.WhenAll(fixingTask, swapsTask);
    //         var result = FomcSofrCurve.Build(scenario, await fixingTask, await swapsTask);
    //         Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
    //         return 0;
    //     }
    //     catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException
    //         or JsonException or HttpRequestException or KeyNotFoundException or OperationCanceledException
    //         or ArgumentException or InvalidOperationException or ApplicationException)
    //     {
    //         Console.Error.WriteLine($"CurveGuard: {error.Message}");
    //         return 1;
    //     }
    // }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"CurveGuard: {message}");
        Console.Error.WriteLine(Help);
        return 2;
    }
}
