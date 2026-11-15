using System.Globalization;
using System.Text.Json;
using QuantLib;

namespace CurveGuard;

internal static class Program
{
    private const string Help = """
        Build and validate a USD SOFR OIS curve.

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
        if (args is ["--help"] or ["-h"])
        {
            Console.WriteLine(Help);
            return 0;
        }
        
        DateTime valuationDate = new DateTime(2026, 11, 14);











        if (args.Length > 0 && args[0] is "--sofr" or "--ois" or "--market-date")
        {
            if (args.Length < 3 || args[^2] != "--url" ||
                !Uri.TryCreate(args[^1], UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
            {
                return UsageError("finish the command with --url followed by a full HTTP or HTTPS URL");
            }

            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15),
                MaxResponseContentBufferSize = 256 * 1024,
            };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CurveGuard/1.0");
            var retrieval = new DataRetrieval(httpClient);
            var command = args[..^2];
            return command[0] == "--sofr"
                ? await FetchSofr(command, retrieval, url)
                : await FetchOis(command, retrieval, url);
        }


        












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

        return UsageError("provide --sofr, --ois, or --market-date with --url URL");
    }

    private static async Task<int> FetchSofr(string[] args, DataRetrieval retrieval, Uri url)
    {
        DateOnly? effectiveDate = null;
        if (args.Length > 1)
        {
            if (args.Length != 2 || !DateOnly.TryParseExact(args[1], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
            {
                return UsageError("--sofr takes an optional effective date in YYYY-MM-DD format");
            }
            effectiveDate = date;
        }

        try
        {
            var fixing = await retrieval.GetSofrFixingAsync(url, effectiveDate);
            if (fixing is null)
            {
                Console.Error.WriteLine(effectiveDate is { } missingDate
                    ? $"No New York Fed SOFR fixing for {MarketData.DateText(missingDate)}."
                    : "The New York Fed returned no overnight SOFR fixing.");
                return 1;
            }

            Console.WriteLine(JsonSerializer.Serialize(fixing, JsonOptions));
            return 0;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            Console.Error.WriteLine($"Could not retrieve New York Fed SOFR: {error.Message}");
            return 1;
        }
    }

    private static async Task<int> FetchOis(string[] args, DataRetrieval retrieval, Uri url)
    {
        if (args.Length != 2 || !DateOnly.TryParseExact(args[1], "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date))
        {
            return UsageError($"{args[0]} requires a date in YYYY-MM-DD format");
        }

        if (args[0] == "--market-date")
        {
            var report = await Guard.ReplayMarket(date, retrieval, url);
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return report.Decision == "PASS" ? 0 : 1;
        }

        try
        {
            var snapshot = await retrieval.GetOisSwapRatesAsync(url, date);
            Console.WriteLine(JsonSerializer.Serialize(snapshot, JsonOptions));
            return 0;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException
            or IOException or FormatException)
        {
            Console.Error.WriteLine($"Could not retrieve CheckMySwap OIS rates: {error.Message}");
            return 1;
        }
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"CurveGuard: {message}");
        Console.Error.WriteLine(Help);
        return 2;
    }
}
