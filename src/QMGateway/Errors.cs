using System.Text.Json.Nodes;

namespace QMGateway;

/// <summary>Everything this SDK throws extends this class, so one catch can handle it all.</summary>
public class QMGatewayException : Exception
{
    public QMGatewayException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>QMGateway answered with an error. Branch on <see cref="Code"/> (stable); show <see cref="PlainMessage"/> to people (it may be reworded).</summary>
public class ApiException : QMGatewayException
{
    public ApiException(string message, int status, string code, string? requestId = null, JsonObject? details = null)
        : base(requestId is null ? message : $"{message} (request {requestId})")
    {
        PlainMessage = message; Status = status; Code = code; RequestId = requestId; Details = details ?? new JsonObject();
    }
    public string PlainMessage { get; }
    public int Status { get; }
    public string Code { get; }
    public string? RequestId { get; }
    /// <summary>Extra facts for some codes, for example remaining_units for INSUFFICIENT_BALANCE.</summary>
    public JsonObject Details { get; }
}

public sealed class AuthenticationException(string m, int s, string c, string? r, JsonObject? d) : ApiException(m, s, c, r, d);
public sealed class PermissionDeniedException(string m, int s, string c, string? r, JsonObject? d) : ApiException(m, s, c, r, d);
public sealed class InvalidRequestException(string m, int s, string c, string? r, JsonObject? d) : ApiException(m, s, c, r, d);
public sealed class NotFoundException(string m, int s, string c, string? r, JsonObject? d) : ApiException(m, s, c, r, d);
public sealed class ConflictException(string m, int s, string c, string? r, JsonObject? d) : ApiException(m, s, c, r, d);
public sealed class ServerException(string m, int s, string c, string? r, JsonObject? d) : ApiException(m, s, c, r, d);

public sealed class RateLimitException(string m, int s, string c, string? r, JsonObject? d, int? retryAfter) : ApiException(m, s, c, r, d)
{
    /// <summary>Seconds the server asked you to wait, when it said.</summary>
    public int? RetryAfter { get; } = retryAfter;
}

/// <summary>No answer could be read (DNS, connection, TLS or timeout), after the retries were used up.</summary>
public sealed class ConnectionException(string message, Exception? inner = null) : QMGatewayException(message, inner);

public enum SignatureFailure { BadSignature, StaleTimestamp, MissingHeader }

/// <summary>A webhook request is not genuine. Answer 401 and do nothing else.</summary>
public sealed class SignatureException(SignatureFailure reason) : QMGatewayException($"The webhook could not be verified: {reason}")
{
    public SignatureFailure Reason { get; } = reason;
    /// <summary>The reason in the contract's wording: bad_signature, stale_timestamp or missing_header.</summary>
    public string ReasonCode => Reason switch { SignatureFailure.BadSignature => "bad_signature", SignatureFailure.StaleTimestamp => "stale_timestamp", _ => "missing_header" };
}
