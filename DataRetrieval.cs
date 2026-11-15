using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CurveGuard
{
    public sealed class DataRetrieval(HttpClient httpClient, JsonSerializerOptions? serializerOptions = null)
    {
        public async Task<T> GetJsonAsync<T>(Uri url, CancellationToken cancellationToken = default)
        {
            var content = await DownloadAsync(url, cancellationToken);
            return JsonSerializer.Deserialize<T>(content, serializerOptions)
                ?? throw new JsonException("expected a JSON response with a value");
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
    }
}