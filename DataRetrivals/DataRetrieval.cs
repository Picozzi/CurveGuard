using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CurveGuard
{
    public class DataRetrieval
    {
        private HttpClient httpClient = null!;
        private JsonSerializerOptions serializerOptions;

        public DataRetrieval(HttpClient httpClient, JsonSerializerOptions serializerOptions)
        {
            this.httpClient = httpClient;
            this.serializerOptions = serializerOptions;
        }

        public DataRetrieval(JsonSerializerOptions serializerOptions)
        {
            this.serializerOptions = serializerOptions;
        }

        public async Task<T> GetJsonAsync<T>(Uri url)
        {
            if (httpClient is null)
                throw new InvalidOperationException("HttpClient must be provided for URL retrieval");

            var content = await DownloadAsync(url);
            return JsonSerializer.Deserialize<T>(content, serializerOptions)
                ?? throw new JsonException("expected a JSON response with a value");
        }

        private async Task<byte[]> DownloadAsync(Uri url)
        {
            ArgumentNullException.ThrowIfNull(url);
            if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https"))
                throw new ArgumentException("expected an absolute HTTP or HTTPS URL", nameof(url));

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync();
        }
        
        public async Task<T> GetJsonAsync<T>(string path)
        {
            var content = await DownloadAsync(path);
            return JsonSerializer.Deserialize<T>(content, serializerOptions)
                ?? throw new JsonException("expected a JSON response with a value");
        }

        public static async Task<byte[]> DownloadAsync(string path)
        {
            ArgumentNullException.ThrowIfNull(path);
            return await File.ReadAllBytesAsync(path);
        }
    }
}