using System.Text.Json.Nodes;

namespace QMGateway.Testing;

/// <summary>
/// A pretend network for your own tests: queue answers, run your code, then look at <see cref="Requests"/>.
/// <code>var fake = new FakeTransport().QueueJson(new JsonObject { ["success"] = true }, 202);
/// var qm = new QMGatewayClient("qm_test_x", new QMGatewayOptions { Transport = fake });</code>
/// </summary>
public sealed class FakeTransport : ITransport
{
    private readonly Queue<object> _answers = new();
    public List<TransportRequest> Requests { get; } = [];

    public FakeTransport QueueRaw(int status, string body, IDictionary<string, string>? headers = null)
    {
        _answers.Enqueue(new TransportResponse(status, (headers ?? new Dictionary<string, string>()).ToDictionary(p => p.Key.ToLowerInvariant(), p => p.Value), body));
        return this;
    }
    public FakeTransport QueueJson(JsonNode json, int status = 200, IDictionary<string, string>? headers = null) => QueueRaw(status, json.ToJsonString(), headers);
    public FakeTransport QueueError(int status, string code, string message = "Error", IDictionary<string, string>? headers = null, JsonObject? details = null) =>
        QueueJson(new JsonObject { ["success"] = false, ["error"] = new JsonObject { ["code"] = code, ["message"] = message, ["request_id"] = "req_fake", ["details"] = details ?? new JsonObject() } }, status, headers);
    public FakeTransport QueueNetworkError(string message = "Could not connect") { _answers.Enqueue(new ConnectionException(message)); return this; }

    public Task<TransportResponse> SendAsync(TransportRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (!_answers.TryDequeue(out var next)) throw new InvalidOperationException($"FakeTransport has no answer queued for {request.Method} {request.Url}");
        return next is ConnectionException c ? throw c : Task.FromResult((TransportResponse)next);
    }
    public TransportRequest Last => Requests.Count > 0 ? Requests[^1] : throw new InvalidOperationException("Nothing was sent yet.");
}
