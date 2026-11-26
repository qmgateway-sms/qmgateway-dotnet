using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QMGateway;

public sealed record TransportRequest(string Method, string Url, IReadOnlyDictionary<string, string> Headers, string? Body, TimeSpan Timeout, bool Retryable);
public sealed record TransportResponse(int Status, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>Sends one request. Throw <see cref="ConnectionException"/> when no answer could be read.</summary>
public interface ITransport
{
    Task<TransportResponse> SendAsync(TransportRequest request, CancellationToken cancellationToken);
}

/// <summary>How a client behaves. Immutable: use <c>with</c> to change a copy.</summary>
public sealed record QMGatewayOptions
{
    /// <summary>Where the API lives. https only; plain http is accepted for localhost (testing).</summary>
    public string BaseUrl { get; init; } = "https://qmgateway.com";
    /// <summary>Time to wait for one request. A timeout counts as a network error.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Extra tries after the first, for network errors, 429 and 5xx. 0 turns retries off.</summary>
    public int MaxRetries { get; init; } = 4;
    /// <summary>Replaces the network (tests use <c>QMGateway.Testing.FakeTransport</c>).</summary>
    public ITransport? Transport { get; init; }
    /// <summary>Replaces the real pause between retries. Tests use it to run fast.</summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
    /// <summary>Appended to the SDK's User-Agent.</summary>
    public string? UserAgent { get; init; }

    internal void Validate()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var u)) throw new ArgumentException("BaseUrl is not a valid web address.");
        var local = u.IsLoopback;
        if (!(u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && local))) throw new ArgumentException("BaseUrl must use https (plain http is only allowed for localhost).");
        if (u.UserInfo.Length > 0) throw new ArgumentException("BaseUrl must not contain a user name or password.");
        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromSeconds(300)) throw new ArgumentException("Timeout must be between 0 and 300 seconds.");
        if (MaxRetries is < 0 or > 10) throw new ArgumentException("MaxRetries must be from 0 to 10.");
        if (UserAgent is not null && UserAgent.AsSpan().IndexOfAny('\r', '\n') >= 0) throw new ArgumentException("UserAgent must be one line.");
    }
    internal string Root => BaseUrl.TrimEnd('/');
}

/// <summary>The built-in transport: <see cref="HttpClient"/>, no redirects, certificates always checked.</summary>
public sealed class HttpClientTransport : ITransport, IDisposable
{
    private readonly HttpClient _client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    public async Task<TransportResponse> SendAsync(TransportRequest r, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(new HttpMethod(r.Method), r.Url);
        if (r.Body is not null) req.Content = new StringContent(r.Body, Encoding.UTF8, "application/json");
        foreach (var (k, v) in r.Headers) if (!k.Equals("content-type", StringComparison.OrdinalIgnoreCase)) req.Headers.TryAddWithoutValidation(k, v);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(r.Timeout);
        try
        {
            using var res = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var headers = new Dictionary<string, string>();
            foreach (var h in res.Headers.Concat(res.Content.Headers)) headers[h.Key.ToLowerInvariant()] = string.Join(", ", h.Value);
            return new TransportResponse((int)res.StatusCode, headers, await res.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ConnectionException("The request timed out."); }
        catch (HttpRequestException e) { throw new ConnectionException($"Could not reach QMGateway ({e.InnerException?.Message ?? e.Message}).", e); }
    }
    public void Dispose() => _client.Dispose();
}

/// <summary>
/// Sends requests and does what every call needs: the key, the retries, and turning an error answer into the right exception.
/// Retry rules (the shared SDK contract): network errors, 429 and 5xx are retried, other 4xx never; wait Retry-After (max 60 s) or 1, 2, 4, 8 s
/// plus up to 20% jitter; same Idempotency-Key every try. A request that is not safe to repeat is retried only on 429.
/// </summary>
internal sealed class HttpCore
{
    public const string Version = "0.1.0";
    private const double MaxWait = 60;
    private readonly string _key;
    private readonly ITransport _transport;
    public QMGatewayOptions Options { get; }

    public HttpCore(string apiKey, QMGatewayOptions options)
    {
        if (string.IsNullOrEmpty(apiKey) || apiKey.Any(char.IsWhiteSpace)) throw new ArgumentException("The API key is missing or has spaces in it.", nameof(apiKey));
        options.Validate();
        _key = apiKey; Options = options; _transport = options.Transport ?? new HttpClientTransport();
    }

    public async Task<JsonObject> RequestAsync(string method, string path, IEnumerable<KeyValuePair<string, string?>>? query = null, JsonObject? body = null,
        IReadOnlyDictionary<string, string>? headers = null, bool safeToRepeat = true, CancellationToken ct = default)
    {
        var qs = string.Join('&', (query ?? []).Where(p => p.Value is not null).Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value!)}"));
        var h = new Dictionary<string, string>
        {
            ["x-api-key"] = _key, ["accept"] = "application/json",
            ["user-agent"] = $"qmgateway-dotnet/{Version} dotnet/{Environment.Version}" + (Options.UserAgent is null ? "" : " " + Options.UserAgent),
        };
        foreach (var (k, v) in headers ?? new Dictionary<string, string>()) h[k.ToLowerInvariant()] = v;
        if (body is not null) h["content-type"] = "application/json";
        var request = new TransportRequest(method, Options.Root + path + (qs.Length > 0 ? "?" + qs : ""), h, body?.ToJsonString(JsonOpts), Options.Timeout, safeToRepeat);

        for (var attempt = 0; ; attempt++)
        {
            var last = attempt >= Options.MaxRetries;
            TransportResponse res;
            try { res = await _transport.SendAsync(request, ct).ConfigureAwait(false); }
            catch (ConnectionException) when (!last && request.Retryable) { await PauseAsync(Backoff(attempt), ct).ConfigureAwait(false); continue; }
            if ((res.Status == 429 || (res.Status >= 500 && request.Retryable)) && !last) { await PauseAsync(WaitFor(res, attempt), ct).ConfigureAwait(false); continue; }
            return Decode(res);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private Task PauseAsync(double seconds, CancellationToken ct) => (Options.Delay ?? Task.Delay).Invoke(TimeSpan.FromSeconds(seconds), ct);
    private static double Backoff(int attempt) => Math.Min(Math.Pow(2, attempt), MaxWait) * (1 + RandomNumberGenerator.GetInt32(0, 201) / 1000.0);
    private static double WaitFor(TransportResponse r, int attempt) =>
        r.Headers.TryGetValue("retry-after", out var a) && a.All(char.IsAsciiDigit) && a.Length > 0 ? Math.Min(double.Parse(a, CultureInfo.InvariantCulture), MaxWait) : Backoff(attempt);

    private static JsonObject Decode(TransportResponse res)
    {
        JsonObject? obj = null;
        try { obj = JsonNode.Parse(res.Body) as JsonObject; } catch (JsonException) { }
        if (res.Status is >= 200 and < 300 && obj?["success"]?.GetValueKind() == JsonValueKind.True) return obj;
        var e = obj?["error"] as JsonObject;
        var s = res.Status;
        var code = e?["code"] is JsonValue c && c.TryGetValue<string>(out var cs) ? cs : s >= 500 ? "SERVER_ERROR" : "INVALID_RESPONSE";
        var message = e?["message"] is JsonValue m && m.TryGetValue<string>(out var ms) ? ms : s >= 500 ? "QMGateway had a problem. Please try again shortly." : "QMGateway sent an answer this SDK cannot read.";
        var id = e?["request_id"] is JsonValue r && r.TryGetValue<string>(out var rs) ? rs : res.Headers.GetValueOrDefault("x-request-id");
        var details = (e?["details"] as JsonObject)?.DeepClone() as JsonObject;
        throw s switch
        {
            401 => new AuthenticationException(message, s, code, id, details),
            403 => new PermissionDeniedException(message, s, code, id, details),
            404 => new NotFoundException(message, s, code, id, details),
            409 => new ConflictException(message, s, code, id, details),
            429 => new RateLimitException(message, s, code, id, details, res.Headers.TryGetValue("retry-after", out var ra) && int.TryParse(ra, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null),
            >= 500 => new ServerException(message, s, code, id, details),
            >= 400 => new InvalidRequestException(message, s, code, id, details),
            _ => new ApiException(message, s, code, id, details),
        };
    }
}
