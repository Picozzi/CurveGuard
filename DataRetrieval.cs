using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CurveGuard;

public sealed record SofrFixing(DateOnly EffectiveDate, decimal RatePercent)
{
    public decimal RateDecimal => RatePercent / 100m;
}

public sealed record OisSwapQuote(string Tenor, decimal RatePercent, string Source, string Method,
    int? AcceptedRecords, int? CloseRecords)
{
    public decimal RateDecimal => RatePercent / 100m;
}

public sealed record OisSwapSnapshot(DateOnly ObservationDate, string Source, string Sha256, string Url,
    string? ProviderCollectionTime, IReadOnlyList<OisSwapQuote> Quotes);

public sealed class DataRetrieval(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions OisJsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.Strict,
    };

    /// <summary>Fetches JSON from a caller supplied URL and deserializes it to the requested type.</summary>
    public async Task<T> GetJsonAsync<T>(Uri url, CancellationToken cancellationToken = default)
    {
        var content = await DownloadAsync(url, cancellationToken);
        return JsonSerializer.Deserialize<T>(content, JsonSerializerOptions.Web)
            ?? throw new JsonException("expected a JSON response with a value");
    }

    /// <summary>Fetches a SOFR fixing from a New York Fed compatible URL and optionally checks its date.</summary>
    /// <returns>The published fixing, or null if the API has no observation for the request.</returns>
    public async Task<SofrFixing?> GetSofrFixingAsync(Uri url, DateOnly? effectiveDate = null,
        CancellationToken cancellationToken = default)
    {
        var payload = await GetJsonAsync<SofrResponse>(url, cancellationToken);
        if (payload.Rates is not { } rates)
            throw new JsonException("New York Fed SOFR response is missing refRates");
        if (rates.Length == 0)
            return null;

        if (rates is not [{ Type: "SOFR", EffectiveDate: { } dateOfFixing, PercentRate: { } rate }] ||
            (effectiveDate is { } expectedDate && dateOfFixing != expectedDate))
        {
            throw new JsonException("New York Fed response did not contain the expected SOFR fixing");
        }

        return new SofrFixing(dateOfFixing, rate);
    }

    /// <summary>Fetches a CheckMySwap compatible USD OIS snapshot and checks its observation date.</summary>
    public async Task<OisSwapSnapshot> GetOisSwapRatesAsync(Uri url, DateOnly observationDate,
        CancellationToken cancellationToken = default)
    {
        var date = observationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        byte[] content;
        try
        {
            content = await DownloadAsync(url, cancellationToken);
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FormatException($"The endpoint has no USD OIS snapshot for {date}", error);
        }
        var payload = JsonSerializer.Deserialize<OisResponse>(content, OisJsonOptions);
        if (payload is not { Currency: "USD", Curve: { Length: > 0 } rates } || payload.Date != observationDate)
            throw new JsonException($"expected a nonempty USD OIS curve for {date}");

        var quotes = new List<OisSwapQuote>(rates.Length);
        var tenors = new HashSet<string>();
        foreach (var row in rates)
        {
            if (row is not { Tenor: { } tenor, Rate: { } rate, Source: { } source, Method: { } method } ||
                row.Date != observationDate || string.IsNullOrWhiteSpace(tenor) ||
                string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(method) ||
                row.Trades is < 0 || row.CloseTrades is < 0 || !tenors.Add(tenor))
            {
                throw new JsonException($"invalid or duplicate OIS quote for {row?.Tenor ?? "unknown tenor"} on {date}");
            }

            quotes.Add(new OisSwapQuote(tenor, rate, source, method, row.Trades, row.CloseTrades));
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        return new OisSwapSnapshot(observationDate, "CheckMySwap", hash, url.AbsoluteUri, payload.FetchedAt,
            quotes.AsReadOnly());
    }

    private async Task<byte[]> DownloadAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https"))
            throw new ArgumentException("expected an absolute HTTP or HTTPS URL", nameof(url));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private sealed record SofrResponse([property: JsonPropertyName("refRates")] SofrApiRate?[]? Rates);
    private sealed record SofrApiRate(DateOnly? EffectiveDate, string? Type, decimal? PercentRate);
    private sealed record OisResponse(string? Currency, DateOnly? Date, OisApiQuote?[]? Curve, string? FetchedAt);
    private sealed record OisApiQuote(string? Tenor, decimal? Rate, DateOnly? Date, string? Source,
        string? Method, int? Trades, int? CloseTrades);
}
