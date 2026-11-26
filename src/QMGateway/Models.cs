using System.Globalization;
using System.Text.Json.Nodes;

namespace QMGateway;

/// <summary>Reads the dates QMGateway sends. "2026-11-19 10:29:41" is Malawi time (UTC+2); "2026-12-01T08:00:00+02:00" has its own offset.</summary>
public static class MalawiTime
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(2);

    public static DateTimeOffset? Parse(string? v)
    {
        if (string.IsNullOrEmpty(v)) return null;
        if (DateTimeOffset.TryParseExact(v, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var plain))
            return new DateTimeOffset(plain.DateTime, Offset);
        return DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }

    /// <summary>Malawi time as the API wants it when you schedule: "2026-12-01 08:00:00".</summary>
    public static string Format(DateTimeOffset d) => d.ToOffset(Offset).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}

internal static class J
{
    public static string S(this JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToString() ?? "";
    public static string? N(this JsonNode? n) => n is null ? null : n.S();
    public static int I(this JsonNode? n, int d = 0) => n is null ? d : (int)Convert.ToInt64(n.ToString(), CultureInfo.InvariantCulture);
    public static int? NI(this JsonNode? n) => n is null ? null : n.I();
    public static bool B(this JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    public static DateTimeOffset? T(this JsonNode? n) => MalawiTime.Parse(n?.S());
    public static IReadOnlyList<string> Strs(this JsonNode? n) => (n as JsonArray)?.Select(x => x.S()).ToList() ?? [];
    public static IEnumerable<JsonObject> Objs(this JsonNode? n) => (n as JsonArray)?.OfType<JsonObject>() ?? [];
}

/// <summary>One message: queued, sent, delivered or failed. <c>Raw</c> holds everything the API sent, including fields newer than this SDK.</summary>
public sealed record Message(int Id, string PhoneNumber, string Status, string MessageType, int SmsUnits, DateTimeOffset? ScheduledTime, DateTimeOffset? CreatedAt,
    DateTimeOffset? SentAt, DateTimeOffset? DeliveredAt, DateTimeOffset? FailedAt, string? FailureReason, string? Mode, JsonObject Raw)
{
    internal static Message From(JsonObject d) => new(d["id"].I(), d["phone_number"].S(), d["status"].S(), d["message_type"].S(), d["sms_units"].I(1), d["scheduled_time"].T(), d["created_at"].T(),
        d["sent_at"].T(), d["delivered_at"].T(), d["failed_at"].T(), d["failure_reason"].N(), d["mode"].N(), d);
    public bool IsDelivered => Status == "Delivered";
    public bool IsFailed => Status == "Failed";
    /// <summary>Delivered, failed or cancelled: nothing more will happen to it.</summary>
    public bool IsFinished => Status is "Delivered" or "Failed" or "Cancelled";
}

/// <summary>What sending answers: the messages are queued, not yet sent.</summary>
public sealed record SendResult(IReadOnlyList<int> MessageIds, int Recipients, int SmsUnits, string Status, int RemainingUnits, bool IdempotentReplay, IReadOnlyList<string> Numbers, JsonObject Raw)
{
    internal static SendResult From(JsonObject d) => new(((d["message_ids"] as JsonArray)?.Select(x => x.I()).ToList() ?? []), d["recipients"].I(), d["sms_units"].I(), d["status"].S(), d["remaining_units"].I(), d["idempotent_replay"].B(), d["numbers"].Strs(), d);
    /// <summary>The id of the message, when you sent to one number.</summary>
    public int MessageId => MessageIds.Count > 0 ? MessageIds[0] : throw new InvalidOperationException("No message was queued.");
}

/// <summary>A reply received on one of your gateway phones.</summary>
public sealed record InboxMessage(int Id, string From, string Message, DateTimeOffset? ReceivedAt, JsonObject Raw)
{
    internal static InboxMessage Of(JsonObject d) => new(d["id"].I(), d["from"].S(), d["message"].S(), d["received_at"].T(), d);
}

public sealed record ScheduledMessage(int Id, string PhoneNumber, string Message, int SmsUnits, DateTimeOffset? ScheduledAt, DateTimeOffset? CreatedAt, JsonObject Raw)
{
    internal static ScheduledMessage From(JsonObject d) => new(d["id"].I(), d["phone_number"].S(), d["message"].S(), d["sms_units"].I(1), d["scheduled_at"].T(), d["created_at"].T(), d);
}

public sealed record Device(int Id, string Name, string Status, DateTimeOffset? LastSeenAt, IReadOnlyList<string> Networks, int Capacity, int Queued, JsonObject Raw)
{
    internal static Device From(JsonObject d) => new(d["id"].I(), d["name"].S(), d["status"].S(), d["last_seen_at"].T(), d["networks"].Strs(), d["capacity"].I(1), d["queued"].I(), d);
    public bool IsOnline => Status == "online";
}

public sealed record EmailOtpBalance(bool Included, int Quota, int Used, int Remaining, string? PeriodStart, string? ResetsAt);

public sealed record Balance(bool Active, string? Plan, int Quota, int Used, int Reserved, int Remaining, int PercentLeft, string Level, string? StartsAt, string? ExpiresAt, int? DaysLeft, EmailOtpBalance? EmailOtp, JsonObject Raw)
{
    internal static Balance From(JsonObject d) => new(d["active"].B(), d["plan"].N(), d["quota"].I(), d["used"].I(), d["reserved"].I(), d["remaining"].I(), d["percent_left"].I(), d["level"].S(), d["starts_at"].N(), d["expires_at"].N(), d["days_left"].NI(),
        d["email_otp"] is JsonObject e ? new EmailOtpBalance(e["included"].B(), e["quota"].I(), e["used"].I(), e["remaining"].I(), e["period_start"].N(), e["resets_at"].N()) : null, d);
    public bool IsLow => Level is "low" or "critical" or "empty";
}

public sealed record Service(string Key, string Name, string Status, JsonObject Details)
{
    /// <summary>True when a message could go out right now (plan active, SMS left, a phone online).</summary>
    public bool ReadyToSend => Details["ready_to_send"].B();
}

/// <summary>Who the API key belongs to and which services are ready. Call it at start-up or in a health check.</summary>
public sealed record AccountInfo(int OrganisationId, string OrganisationName, string KeyMode, IReadOnlyList<string> Scopes, IReadOnlyList<Service> Services, JsonObject Raw)
{
    internal static AccountInfo From(JsonObject d) => new(d["organisation"]?["id"].I() ?? 0, d["organisation"]?["name"].S() ?? "", d["key"]?["mode"]?.S() ?? "live", d["key"]?["scopes"].Strs() ?? [],
        d["services"].Objs().Select(s => new Service(s["key"].S(), s["name"].S(), s["status"].S(), s["details"] as JsonObject ?? new JsonObject())).ToList(), d);
    public Service? Service(string key) => Services.FirstOrDefault(s => s.Key == key);
    public bool IsTestKey => KeyMode == "test";
}

public sealed record OtpRequest(string RequestReference, int ExpiresIn, string Status, string Channel, int? EmailOtpRemaining, string? EmailOtpResetsAt, JsonObject Raw)
{
    internal static OtpRequest From(JsonObject d) => new(d["request_reference"].S(), d["expires_in"].I(), d["status"].S(), d["channel"].S(), d["email_otp_remaining"].NI(), d["email_otp_resets_at"].N(), d);
}

/// <summary>The answer to a code check. A wrong code is a normal answer (Verified false), not an error.</summary>
public sealed record OtpVerification(bool Verified, string Status)
{
    internal static OtpVerification From(JsonObject d) => new(d["verified"].B(), d["status"].S());
}

public sealed record Statistics(JsonObject Overview, JsonObject Quota, JsonArray RecentMessages, JsonArray Daily, JsonArray StatusDistribution, JsonArray TopRecipients, JsonObject Counts, JsonObject Period, JsonObject Raw)
{
    internal static Statistics From(JsonObject d)
    {
        var s = d["statistics"] as JsonObject ?? new JsonObject();
        JsonObject O(string k, JsonObject src) => src[k] as JsonObject ?? new JsonObject();
        JsonArray A(string k) => s[k] as JsonArray ?? new JsonArray();
        return new(O("overview", s), O("quota", s), A("recent_messages"), A("daily"), A("status_distribution"), A("top_recipients"), O("counts", s), O("period", d), d);
    }
}

/// <summary>One page of a list, and where the next one starts (null when there is no more).</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, int? NextCursor)
{
    public bool HasMore => NextCursor is not null;
}

public sealed record CancelResult(int Id, string Status, int RemainingUnits);
public sealed record RescheduleResult(int Id, string Status, string ScheduledAt);
