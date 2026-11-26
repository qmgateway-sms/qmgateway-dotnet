using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace QMGateway;

/// <summary>A verified webhook delivery. Types: sms.incoming, sms.sent, sms.delivered, sms.failed, webhook.test.</summary>
public sealed record WebhookEvent(string Type, string Id, int Attempt, JsonObject Data)
{
    public int? MessageId => Data["message_id"].NI();
    public string? PhoneNumber => Data["phone_number"].N();
    public string? Status => Data["status"].N();
    public DateTimeOffset? UpdatedAt => Data["updated_at"].T();
    public bool IsTest => Type == "webhook.test";
}

public static class Webhook
{
    public const int Tolerance = 300;

    /// <summary>
    /// Checks that a webhook really came from QMGateway. Pass the RAW body exactly as received, not re-encoded JSON.
    /// Throws <see cref="SignatureException"/> when it is not genuine. <paramref name="now"/> is unix seconds (tests pass a fixed one).
    /// </summary>
    public static WebhookEvent Verify(string body, IReadOnlyDictionary<string, string> headers, string secret, int tolerance = Tolerance, long? now = null)
        => Verify(Encoding.UTF8.GetBytes(body), headers, secret, tolerance, now);

    public static WebhookEvent Verify(ReadOnlySpan<byte> body, IReadOnlyDictionary<string, string> headers, string secret, int tolerance = Tolerance, long? now = null)
    {
        var h = headers.ToDictionary(p => p.Key.ToLowerInvariant(), p => p.Value);
        h.TryGetValue("x-qmgateway-signature", out var signature); h.TryGetValue("x-qmgateway-timestamp", out var timestamp);
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(timestamp)) throw new SignatureException(SignatureFailure.MissingHeader);
        if (!timestamp.All(char.IsAsciiDigit) || !long.TryParse(timestamp, out var t) || Math.Abs((now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()) - t) > tolerance) throw new SignatureException(SignatureFailure.StaleTimestamp);
        var expected = Encoding.ASCII.GetBytes(Signature(timestamp, body, secret));
        if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(signature))) throw new SignatureException(SignatureFailure.BadSignature);
        JsonObject? data;
        try { data = JsonNode.Parse(body) as JsonObject; } catch (System.Text.Json.JsonException) { data = null; }
        if (data is null) throw new SignatureException(SignatureFailure.BadSignature);
        return new WebhookEvent(h.GetValueOrDefault("x-qmgateway-event", ""), h.GetValueOrDefault("x-qmgateway-event-id", ""), int.TryParse(h.GetValueOrDefault("x-qmgateway-attempt"), out var a) ? a : 1, data);
    }

    private static string Signature(string timestamp, ReadOnlySpan<byte> body, string secret)
    {
        var payload = new byte[timestamp.Length + 1 + body.Length];
        Encoding.ASCII.GetBytes(timestamp, payload);
        payload[timestamp.Length] = (byte)'.';
        body.CopyTo(payload.AsSpan(timestamp.Length + 1));
        return "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload)).ToLowerInvariant();
    }

    /// <summary>Makes a correctly signed request, for testing your own webhook handler.</summary>
    public static (string Body, Dictionary<string, string> Headers) Sign(string body, string secret, string @event = "sms.delivered", long? timestamp = null, string eventId = "evt_test")
    {
        var t = (timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString();
        return (body, new Dictionary<string, string>
        {
            ["X-QMGateway-Timestamp"] = t, ["X-QMGateway-Signature"] = Signature(t, Encoding.UTF8.GetBytes(body), secret),
            ["X-QMGateway-Event"] = @event, ["X-QMGateway-Event-ID"] = eventId, ["X-QMGateway-Attempt"] = "1",
        });
    }
}
