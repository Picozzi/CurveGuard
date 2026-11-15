// using System.Globalization;
// using System.Security.Cryptography;
// using System.Text.Json;
// using System.Text.Json.Serialization;
// using System.Text.RegularExpressions;

// namespace CurveGuard;

// internal sealed record Quote(string Id, string Tenor, decimal Rate, DateTimeOffset? ObservedAt);
// internal sealed record Snapshot(string Source, string Sha256, IReadOnlyDictionary<string, Quote> Quotes)
// {
//     public object Provenance => ArchiveDetails ?? new { Source, Sha256 };

//     [JsonIgnore]
//     public object? ArchiveDetails { get; init; }
// }

// internal static class MarketData
// {
//     public const string CurveId = "USD-SOFR-OIS";
//     public static readonly string[] Tenors = ["1M", "3M", "6M", "1Y", "2Y", "3Y", "5Y", "7Y", "10Y", "20Y", "30Y"];
//     private static readonly string[] ArchiveTenors = Tenors.Where(tenor => tenor.EndsWith('Y')).ToArray();
//     private const decimal MaxAbsoluteRate = 0.25m;

//     public static DateTimeOffset ParseTimestamp(string value)
//     {
//         if (!Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}.*(?:Z|[+-]\d{2}:\d{2})$") ||
//             !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
//         {
//             throw new FormatException("expected an ISO timestamp with a UTC offset");
//         }

//         return timestamp.ToUniversalTime();
//     }

//     public static string DateText(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

//     public static Snapshot Load(string path)
//     {
//         var content = File.ReadAllBytes(path);
//         using var document = JsonDocument.Parse(content);
//         var data = document.RootElement;
//         if (RequiredText(data, "curve_id") != CurveId)
//             throw new FormatException($"expected curve_id {CurveId}");
//         if (RequiredText(data, "rate_unit") != "decimal")
//             throw new FormatException("rate_unit must be decimal (4.12% is 0.0412)");

//         var source = RequiredText(data, "source");
//         var quotes = new Dictionary<string, Quote>();
//         var quoteIds = new HashSet<string>();
//         foreach (var row in RequiredArray(data, "quotes").EnumerateArray())
//         {
//             var tenor = RequiredText(row, "tenor");
//             var id = RequiredText(row, "quote_id");
//             if (!Tenors.Contains(tenor))
//                 throw new FormatException($"unsupported tenor {tenor}");
//             if (quotes.ContainsKey(tenor) || !quoteIds.Add(id))
//                 throw new FormatException($"duplicate tenor or quote ID at {tenor} ({id})");
//             var rate = ReadRate(row, id);
//             var observedAt = ParseTimestamp(RequiredText(row, "observed_at"));
//             quotes.Add(tenor, new Quote(id, tenor, rate, observedAt));
//         }

//         RequireTenors(quotes, Tenors);
//         return new Snapshot(source, Hash(content), quotes);
//     }

//     public static Snapshot ForReplay(OisSwapSnapshot archive)
//     {
//         var date = DateText(archive.ObservationDate);
//         var points = archive.Quotes.Where(point => ArchiveTenors.Contains(point.Tenor))
//             .ToDictionary(point => point.Tenor);
//         var quotes = new Dictionary<string, Quote>();
//         foreach (var point in points.Values)
//         {
//             var id = $"checkmyswap:USD:{date}:{point.Tenor}";
//             quotes.Add(point.Tenor, new Quote(id, point.Tenor, RequireRate(point.RateDecimal, id), null));
//         }

//         RequireTenors(quotes, ArchiveTenors);
//         return new Snapshot(archive.Source, archive.Sha256, quotes)
//         {
//             ArchiveDetails = new
//             {
//                 archive.Source, archive.Sha256, archive.Url, ObservationDate = date, archive.ProviderCollectionTime,
//                 Points = ArchiveTenors.Select(tenor => points[tenor]).Select(point => new
//                 {
//                     point.Tenor, point.Source, point.Method, point.RatePercent, point.AcceptedRecords, point.CloseRecords,
//                 }).ToArray(),
//             },
//         };
//     }

//     private static void RequireTenors(Dictionary<string, Quote> quotes, IEnumerable<string> required)
//     {
//         var missing = required.Where(tenor => !quotes.ContainsKey(tenor)).ToArray();
//         if (missing.Length > 0)
//             throw new FormatException($"missing required tenors: {string.Join(", ", missing)}");
//     }

//     private static string Hash(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

//     private static string RequiredText(JsonElement data, string key)
//     {
//         if (OptionalText(data, key) is { } value && !string.IsNullOrWhiteSpace(value))
//             return value.Trim();
//         throw new FormatException($"{key} must be a nonempty string");
//     }

//     private static string? OptionalText(JsonElement data, string key)
//     {
//         if (data.ValueKind != JsonValueKind.Object)
//             throw new FormatException($"expected an object containing {key}");
//         if (!data.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null)
//             return null;
//         if (value.ValueKind != JsonValueKind.String)
//             throw new FormatException($"{key} must be a string");
//         return value.GetString();
//     }

//     private static JsonElement RequiredArray(JsonElement data, string key)
//     {
//         if (!data.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Array)
//             throw new FormatException($"{key} must be an array");
//         return value;
//     }

//     private static decimal ReadRate(JsonElement row, string id)
//     {
//         if (!row.TryGetProperty("rate", out var value) || value.ValueKind != JsonValueKind.Number ||
//             !value.TryGetDecimal(out var rate))
//         {
//             throw new FormatException($"{id}: rate must be a finite decimal");
//         }

//         return RequireRate(rate, id);
//     }

//     private static decimal RequireRate(decimal rate, string id)
//     {
//         if (Math.Abs(rate) > MaxAbsoluteRate)
//             throw new FormatException($"{id}: decimal rate must be within ±{MaxAbsoluteRate}");

//         return rate;
//     }
// }
