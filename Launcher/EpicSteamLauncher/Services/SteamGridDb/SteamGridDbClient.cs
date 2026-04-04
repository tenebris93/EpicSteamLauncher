using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EpicSteamLauncher.Services.SteamGridDb
{
    /// <summary>
    /// Wraps SteamGridDB HTTP APIs used to search games and fetch artwork assets.
    /// </summary>
    internal sealed class SteamGridDbClient
    {
        private static readonly Uri BaseUri = new("https://www.steamgriddb.com/api/v2/");
        private readonly HttpClient _http;
        private readonly JsonSerializerOptions _json;

        /// <summary>
        /// Initializes a new SteamGridDB API client.
        /// </summary>
        /// <param name="http">HTTP client instance used for API calls.</param>
        /// <param name="apiKey">SteamGridDB API key.</param>
        public SteamGridDbClient(HttpClient http, string apiKey)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _http.BaseAddress = BaseUri;
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            _json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        }

        /// <summary>
        ///     Validates the API key by calling a lightweight authenticated endpoint.
        /// </summary>
        /// <param name="apiKey">API key to validate.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if the key is valid, false if invalid.</returns>
        /// <exception cref="HttpRequestException">On network errors.</exception>
        /// <exception cref="TaskCanceledException">On timeout or cancellation.</exception>
        public static async Task<bool> ValidateApiKeyAsync(string apiKey, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return false;
            }

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var resp = await http.GetAsync(
                "https://www.steamgriddb.com/api/v2/search/autocomplete/test",
                ct
            ).ConfigureAwait(false);

            switch (resp.StatusCode)
            {
                case HttpStatusCode.OK:
                    return true;
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    return false;
                default:
                    // Any other status: treat as invalid for safety
                    return false;
            }
        }

        /// <summary>
        /// Searches SteamGridDB and returns the first matching game ID.
        /// </summary>
        /// <param name="name">Game name query.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>First matching game ID, or <see langword="null" /> when none is found.</returns>
        public async Task<int?> SearchFirstGameIdAsync(string name, CancellationToken ct)
        {
            using var resp = await _http.GetAsync($"search/autocomplete/{Uri.EscapeDataString(name)}", ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            string payload = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<SgdbResponse<SgdbGameSearchItem[]>>(payload, _json);

            if (result?.Success != true || result.Data == null || result.Data.Length == 0)
            {
                return null;
            }

            return result.Data[0].Id;
        }

        /// <summary>
        /// Gets icon assets for a specific game ID.
        /// </summary>
        /// <param name="gameId">SteamGridDB game ID.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Icon asset array or <see langword="null" /> when unavailable.</returns>
        public Task<SgdbAssetItem[]?> GetIconsAsync(int gameId, CancellationToken ct)
        {
            return GetAssetsAsync($"icons/game/{gameId}", ct);
        }

        /// <summary>
        /// Gets grid assets for a specific game ID.
        /// </summary>
        /// <param name="gameId">SteamGridDB game ID.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Grid asset array or <see langword="null" /> when unavailable.</returns>
        public Task<SgdbAssetItem[]?> GetGridsAsync(int gameId, CancellationToken ct)
        {
            return GetAssetsAsync($"grids/game/{gameId}", ct);
        }

        /// <summary>
        /// Gets hero assets for a specific game ID.
        /// </summary>
        /// <param name="gameId">SteamGridDB game ID.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Hero asset array or <see langword="null" /> when unavailable.</returns>
        public Task<SgdbAssetItem[]?> GetHeroesAsync(int gameId, CancellationToken ct)
        {
            return GetAssetsAsync($"heroes/game/{gameId}", ct);
        }

        /// <summary>
        /// Gets logo assets for a specific game ID.
        /// </summary>
        /// <param name="gameId">SteamGridDB game ID.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Logo asset array or <see langword="null" /> when unavailable.</returns>
        public Task<SgdbAssetItem[]?> GetLogosAsync(int gameId, CancellationToken ct)
        {
            return GetAssetsAsync($"logos/game/{gameId}", ct);
        }

        /// <summary>
        /// Fetches and deserializes an artwork asset endpoint response.
        /// </summary>
        /// <param name="relativeUrl">Relative endpoint path.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Asset array or <see langword="null" /> when request is unsuccessful.</returns>
        private async Task<SgdbAssetItem[]?> GetAssetsAsync(string relativeUrl, CancellationToken ct)
        {
            using var resp = await _http.GetAsync(relativeUrl, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            string payload = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<SgdbResponse<SgdbAssetItem[]>>(payload, _json);

            if (result?.Success != true)
            {
                return null;
            }

            return result.Data;
        }

        /// <summary>
        /// Generic SteamGridDB response envelope.
        /// </summary>
        private sealed class SgdbResponse<T>
        {
            /// <summary>
            /// Gets or sets a value indicating whether the API call succeeded.
            /// </summary>
            [JsonPropertyName("success")] public bool Success { get; set; }

            /// <summary>
            /// Gets or sets the response data payload.
            /// </summary>
            [JsonPropertyName("data")] public T? Data { get; set; }
        }

        /// <summary>
        /// Represents a game search result item.
        /// </summary>
        private sealed class SgdbGameSearchItem
        {
            /// <summary>
            /// Gets or sets the game ID.
            /// </summary>
            [JsonPropertyName("id")] public int Id { get; set; }

            /// <summary>
            /// Gets or sets the game display name.
            /// </summary>
            [JsonPropertyName("name")] public string? Name { get; set; }
        }

        /// <summary>
        /// Represents an artwork asset item returned by SteamGridDB.
        /// </summary>
        public sealed class SgdbAssetItem
        {
            /// <summary>
            /// Gets or sets the asset ID.
            /// </summary>
            [JsonPropertyName("id")] public int Id { get; set; }

            /// <summary>
            /// Gets or sets the asset URL.
            /// </summary>
            [JsonPropertyName("url")] public string? Url { get; set; }

            /// <summary>
            /// Gets or sets the asset width in pixels.
            /// </summary>
            [JsonPropertyName("width")] public int Width { get; set; }

            /// <summary>
            /// Gets or sets the asset height in pixels.
            /// </summary>
            [JsonPropertyName("height")] public int Height { get; set; }
        }
    }
}

