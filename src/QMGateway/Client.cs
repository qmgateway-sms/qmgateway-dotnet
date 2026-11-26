using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace QMGateway;

internal static class Util
{
    public static JsonObject Data(JsonObject a) => a["data"] as JsonObject ?? new JsonObject();
    public static IEnumerable<JsonObject> Rows(JsonObject a) => a["data"].Objs();
    public static KeyValuePair<string, string?> Q(string k, object? v) => new(k, v switch { null => null, IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), _ => v.ToString() });
}

/// <summary>Options for sending a message.</summary>
public sealed record SendOptions
{
    public string? Type { get; init; }
    public int? DeviceId { get; init; }
    /// <summary>Malawi time. A DateTimeOffset is converted for you.</summary>
    public DateTimeOffset? ScheduledAt { get; init; }
    /// <summary>Pass your own for jobs that may run again; otherwise one is made for you and kept the same across retries.</summary>
    public string? IdempotencyKey { get; init; }
}

public sealed class Messages
{
    private readonly HttpCore _http;
    internal Messages(HttpCore http) => _http = http;

    /// <summary>Queue a message for one number.</summary>
    public Task<SendResult> SendAsync(string to, string message, SendOptions? options = null, CancellationToken ct = default) =>
        SendCoreAsync(new JsonObject { ["phone"] = to, ["message"] = message }, options, ct);

    /// <summary>Queue a message for several numbers.</summary>
    public Task<SendResult> SendAsync(IEnumerable<string> to, string message, SendOptions? options = null, CancellationToken ct = default) =>
        SendCoreAsync(new JsonObject { ["recipients"] = new JsonArray(to.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()), ["message"] = message }, options, ct);

    private async Task<SendResult> SendCoreAsync(JsonObject body, SendOptions? o, CancellationToken ct)
    {
        o ??= new();
        if (o.Type is not null) body["type"] = o.Type;
        if (o.DeviceId is not null) body["device_id"] = o.DeviceId;
        if (o.ScheduledAt is not null) body["scheduled_at"] = MalawiTime.Format(o.ScheduledAt.Value);
        if (o.IdempotencyKey is not null && !(o.IdempotencyKey.Length is >= 1 and <= 255 && o.IdempotencyKey.All(ch => ch is >= '!' and <= '~'))) throw new ArgumentException("IdempotencyKey must be 1 to 255 visible characters with no spaces.");
        var key = o.IdempotencyKey ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var a = await _http.RequestAsync("POST", "/api/v1/messages", body: body, headers: new Dictionary<string, string> { ["idempotency-key"] = key }, ct: ct).ConfigureAwait(false);
        return SendResult.From(Util.Data(a));
    }

    /// <summary>One message and where it is now.</summary>
    public async Task<Message> GetAsync(int id, CancellationToken ct = default) =>
        Message.From(Util.Data(await _http.RequestAsync("GET", "/api/v1/message_status", [Util.Q("id", id)], ct: ct).ConfigureAwait(false)));

    /// <summary>One page of messages, newest first. Pass <c>NextCursor</c> as <paramref name="beforeId"/> for the next page.</summary>
    public async Task<Page<Message>> ListAsync(string? status = null, string? phone = null, string? since = null, int? limit = null, int? beforeId = null, CancellationToken ct = default)
    {
        var a = await _http.RequestAsync("GET", "/api/v1/messages", [Util.Q("status", status), Util.Q("phone", phone), Util.Q("since", since), Util.Q("limit", limit), Util.Q("before_id", beforeId)], ct: ct).ConfigureAwait(false);
        return new Page<Message>(Util.Rows(a).Select(Message.From).ToList(), a["next_before_id"].NI());
    }

    /// <summary>Every matching message, newest first: <c>await foreach (var m in qm.Messages.EachAsync())</c>.</summary>
    public async IAsyncEnumerable<Message> EachAsync(string? status = null, string? phone = null, string? since = null, int pageSize = 100, [EnumeratorCancellation] CancellationToken ct = default)
    {
        int? cursor = null;
        do
        {
            var page = await ListAsync(status, phone, since, pageSize, cursor, ct).ConfigureAwait(false);
            foreach (var m in page.Items) yield return m;
            cursor = page.NextCursor;
        } while (cursor is not null);
    }

    public async Task<Page<ScheduledMessage>> ScheduledAsync(int? limit = null, int? afterId = null, CancellationToken ct = default)
    {
        var a = await _http.RequestAsync("GET", "/api/v1/scheduled", [Util.Q("limit", limit), Util.Q("after_id", afterId)], ct: ct).ConfigureAwait(false);
        return new Page<ScheduledMessage>(Util.Rows(a).Select(ScheduledMessage.From).ToList(), a["next_after_id"].NI());
    }

    public async IAsyncEnumerable<ScheduledMessage> EachScheduledAsync(int pageSize = 100, [EnumeratorCancellation] CancellationToken ct = default)
    {
        int? cursor = null;
        do
        {
            var page = await ScheduledAsync(pageSize, cursor, ct).ConfigureAwait(false);
            foreach (var m in page.Items) yield return m;
            cursor = page.NextCursor;
        } while (cursor is not null);
    }

    /// <summary>Cancel a scheduled message. Its SMS units go back to your balance.</summary>
    public async Task<CancelResult> CancelAsync(int id, CancellationToken ct = default)
    {
        var d = Util.Data(await _http.RequestAsync("POST", "/api/v1/message_cancel", body: new JsonObject { ["id"] = id }, ct: ct).ConfigureAwait(false));
        return new CancelResult(d["id"].I(), d["status"].S(), d["remaining_units"].I());
    }

    /// <summary>Move a scheduled message to a new time (Malawi time).</summary>
    public async Task<RescheduleResult> RescheduleAsync(int id, DateTimeOffset scheduledAt, CancellationToken ct = default)
    {
        var d = Util.Data(await _http.RequestAsync("POST", "/api/v1/message_reschedule", body: new JsonObject { ["id"] = id, ["scheduled_at"] = MalawiTime.Format(scheduledAt) }, ct: ct).ConfigureAwait(false));
        return new RescheduleResult(d["id"].I(), d["status"].S(), d["scheduled_at"].S());
    }
}

/// <summary>Replies received on your gateway phones, oldest first.</summary>
public sealed class Inbox
{
    private readonly HttpCore _http;
    internal Inbox(HttpCore http) => _http = http;

    public async Task<Page<InboxMessage>> ListAsync(int? afterId = null, string? from = null, int? limit = null, CancellationToken ct = default)
    {
        var a = await _http.RequestAsync("GET", "/api/v1/inbox", [Util.Q("after_id", afterId), Util.Q("from", from), Util.Q("limit", limit)], ct: ct).ConfigureAwait(false);
        return new Page<InboxMessage>(Util.Rows(a).Select(InboxMessage.Of).ToList(), a["next_after_id"].NI());
    }

    public async IAsyncEnumerable<InboxMessage> EachAsync(string? from = null, int? afterId = null, int pageSize = 100, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var cursor = afterId;
        do
        {
            var page = await ListAsync(cursor, from, pageSize, ct).ConfigureAwait(false);
            foreach (var m in page.Items) yield return m;
            cursor = page.NextCursor;
        } while (cursor is not null);
    }
}

public sealed class Devices
{
    private readonly HttpCore _http;
    internal Devices(HttpCore http) => _http = http;
    public async Task<IReadOnlyList<Device>> ListAsync(CancellationToken ct = default) =>
        Util.Rows(await _http.RequestAsync("GET", "/api/v1/devices", ct: ct).ConfigureAwait(false)).Select(Device.From).ToList();
}

/// <summary>One-time passwords by SMS or email.</summary>
public sealed class Otp
{
    private readonly HttpCore _http;
    internal Otp(HttpCore http) => _http = http;

    /// <summary>Send a code. Retried only after a 429, so a code is never sent twice by accident.</summary>
    public async Task<OtpRequest> SendAsync(string channel = "sms", string? phone = null, string? email = null, string? purpose = null, int? expirySeconds = null, CancellationToken ct = default)
    {
        var body = new JsonObject { ["channel"] = channel };
        if (phone is not null) body["phone"] = phone;
        if (email is not null) body["email"] = email;
        if (purpose is not null) body["purpose"] = purpose;
        if (expirySeconds is not null) body["expiry_seconds"] = expirySeconds;
        return OtpRequest.From(Util.Data(await _http.RequestAsync("POST", "/api/v1/otp/send", body: body, safeToRepeat: false, ct: ct).ConfigureAwait(false)));
    }

    /// <summary>A wrong or expired code gives Verified = false. It is only an error when the request itself is wrong.</summary>
    public async Task<OtpVerification> VerifyAsync(string requestReference, string code, CancellationToken ct = default) =>
        OtpVerification.From(Util.Data(await _http.RequestAsync("POST", "/api/v1/otp/verify", body: new JsonObject { ["request_reference"] = requestReference, ["code"] = code }, safeToRepeat: false, ct: ct).ConfigureAwait(false)));
}

public sealed class Account
{
    private readonly HttpCore _http;
    internal Account(HttpCore http) => _http = http;
    public async Task<AccountInfo> GetAsync(CancellationToken ct = default) => AccountInfo.From(Util.Data(await _http.RequestAsync("GET", "/api/v1/account", ct: ct).ConfigureAwait(false)));
    public async Task<Balance> BalanceAsync(CancellationToken ct = default) => Balance.From(Util.Data(await _http.RequestAsync("GET", "/api/v1/balance", ct: ct).ConfigureAwait(false)));
    public async Task<Statistics> StatisticsAsync(string? period = null, string? startDate = null, string? endDate = null, CancellationToken ct = default) =>
        Statistics.From(await _http.RequestAsync("GET", "/api/v1/statistics", [Util.Q("period", period), Util.Q("start_date", startDate), Util.Q("end_date", endDate)], ct: ct).ConfigureAwait(false));
}

/// <summary>
/// The entry point.
/// <code>var qm = new QMGatewayClient(Environment.GetEnvironmentVariable("QMGATEWAY_API_KEY")!);
/// var sent = await qm.Messages.SendAsync("+265999123456", "Your order is ready.");</code>
/// </summary>
public sealed class QMGatewayClient
{
    public Messages Messages { get; }
    public Inbox Inbox { get; }
    public Devices Devices { get; }
    public Otp Otp { get; }
    public Account Account { get; }
    public QMGatewayOptions Options { get; }

    public QMGatewayClient(string apiKey, QMGatewayOptions? options = null)
    {
        var http = new HttpCore(apiKey, options ?? new QMGatewayOptions());
        Options = http.Options;
        Messages = new Messages(http); Inbox = new Inbox(http); Devices = new Devices(http); Otp = new Otp(http); Account = new Account(http);
    }

    /// <summary>Same as the constructor; reads better with a test key (qm_test_...): nothing reaches a phone and no units are used.</summary>
    public static QMGatewayClient ForTest(string apiKey, QMGatewayOptions? options = null) => new(apiKey, options);
}
