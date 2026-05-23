using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AutoPinner.Models;
using AutoPinner.Utils;

namespace AutoPinner;

/// <summary>
/// Thin client over the Pinterest v5 Pins API. Handles bearer auth, JSON
/// serialization, exponential backoff on 429 + 5xx, and surfaces a structured
/// exception (PinterestApiException) on non-transient failures that the
/// notifier can extract a fingerprint from.
/// </summary>
public sealed class PinterestClient : IDisposable
{
    private const string ApiBase = "https://api.pinterest.com/v5";
    private const string CreatePinPath = "/pins";

    private readonly HttpClient _http;
    private readonly RetryPolicy _retry;

    public PinterestClient(string accessToken, RetryPolicy? retry = null)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression =
                DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(ApiBase),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _retry = retry ?? new RetryPolicy();
    }

    public void Dispose() => _http.Dispose();

    public async Task<string> CreatePinAsync(PinterestCreatePinRequest request, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(request);

        var response = await _retry.ExecuteAsync(
            async _ =>
            {
                // HttpContent can't be re-sent — build a fresh one per attempt.
                using var msg = new HttpRequestMessage(HttpMethod.Post, CreatePinPath)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
                var resp = await _http.SendAsync(msg, ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                    return body;

                if (IsTransient(resp.StatusCode))
                    throw new TransientPinterestException(resp.StatusCode, body);

                throw new PinterestApiException(resp.StatusCode, body);
            },
            ex => ex is TransientPinterestException,
            ct).ConfigureAwait(false);

        var parsed = JsonSerializer.Deserialize<PinterestCreatePinResponse>(response);
        if (parsed?.Id is null or "")
            throw new PinterestApiException(HttpStatusCode.OK, $"Pin created but response had no id. Body: {Truncate(response)}");
        return parsed.Id;
    }

    private static bool IsTransient(HttpStatusCode code)
        => (int)code == 429 || ((int)code >= 500 && (int)code < 600);

    private static string Truncate(string s)
        => s.Length > 500 ? s[..500] : s;
}

public class PinterestApiException : Exception
{
    public HttpStatusCode Status { get; }
    public string ResponseBodySnippet { get; }
    public PinterestApiException(HttpStatusCode status, string body)
        : base($"Pinterest API {(int)status}: {Truncate(body)}")
    {
        Status = status;
        ResponseBodySnippet = Truncate(body);
    }

    private static string Truncate(string s) => s.Length > 500 ? s[..500] : s;
}

/// <summary>
/// 429 / 5xx — caller retries with backoff via RetryPolicy.
/// </summary>
public sealed class TransientPinterestException : PinterestApiException
{
    public TransientPinterestException(HttpStatusCode status, string body) : base(status, body) { }
}
