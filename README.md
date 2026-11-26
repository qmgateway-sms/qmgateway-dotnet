# QMGateway for .NET

Send SMS and one-time passwords from C# (and any .NET language). Supports **.NET 8, 9 and 10**. It has no dependencies.

```
dotnet add package QMGateway
```

## Send a message

```csharp
using QMGateway;

var qm = new QMGatewayClient(Environment.GetEnvironmentVariable("QMGATEWAY_API_KEY")!);

var sent = await qm.Messages.SendAsync("+265999123456", "Your order is ready.");
Console.WriteLine($"Queued message {sent.MessageId}, {sent.RemainingUnits} SMS left");

Console.WriteLine((await qm.Messages.GetAsync(sent.MessageId)).Status);
```

Use a test key (it starts with `qm_test_`; create one on the API keys page of your account) while you build: nothing reaches a phone and no SMS are used. Keep keys out of your code: read them from the environment or a secret store.
Create one client and reuse it for the life of your app.

## What you can do

| | |
|---|---|
| `qm.Messages.SendAsync(to, text, new SendOptions { ScheduledAt = new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.FromHours(2)) })` | one number, or a list for several |
| `qm.Messages.GetAsync(id)` / `ListAsync(status: "Failed")` / `EachAsync(status: "Failed")` | track delivery; `EachAsync()` fetches more pages for you |
| `qm.Messages.ScheduledAsync()` / `CancelAsync(id)` / `RescheduleAsync(id, time)` | scheduled messages |
| `qm.Inbox.ListAsync()` / `EachAsync()` | replies received |
| `qm.Otp.SendAsync(phone: "+265999123456")` / `VerifyAsync(ref, code)` | one-time passwords |
| `qm.Devices.ListAsync()` | your gateway phones |
| `qm.Account.GetAsync()` / `BalanceAsync()` / `StatisticsAsync()` | account and usage |

```csharp
await foreach (var m in qm.Messages.EachAsync(status: "Failed"))
    Console.WriteLine($"{m.Id} to {m.PhoneNumber} failed: {m.FailureReason}");
```

Every method takes a `CancellationToken`. The `Raw` property of each result holds the full JSON, including fields newer than this SDK.

## Errors

Everything the SDK throws extends `QMGatewayException`. API errors are `ApiException`s with a stable `Code`, the HTTP `Status` and a `RequestId` to quote to support.

```csharp
try { await qm.Messages.SendAsync(phone, text); }
catch (RateLimitException e) { /* still limited after the automatic retries; e.RetryAfter seconds */ }
catch (ApiException e) { Console.Error.WriteLine($"{e.Code} {e.Status} {e.RequestId}"); }
catch (ConnectionException) { /* no answer: the message may or may not be queued; resend with the same IdempotencyKey */ }
```

## Retries and duplicates

Network errors, rate limits (429) and server errors (5xx) are retried up to 4 times, waiting for `Retry-After` when given, otherwise 1, 2, 4, 8 seconds. Other errors are never retried. Every send carries an `Idempotency-Key` (made for you), kept the same across retries, so a retry can never send twice. For jobs that may run again, pass your own: `new SendOptions { IdempotencyKey = $"order-{orderId}" }`. Sending a one-time password is retried only after a 429.

```csharp
var qm = new QMGatewayClient(key, new QMGatewayOptions { Timeout = TimeSpan.FromSeconds(10), MaxRetries = 2 });
```

## Webhooks

```csharp
// ASP.NET Core minimal API
app.MapPost("/webhooks/qmgateway", async (HttpRequest req) =>
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);                       // the raw body, untouched
    var headers = req.Headers.ToDictionary(h => h.Key, h => h.Value.ToString());
    try
    {
        var e = Webhook.Verify(ms.ToArray(), headers, Environment.GetEnvironmentVariable("QMGATEWAY_WEBHOOK_SECRET")!);
        if (e.Type == "sms.delivered") { /* e.MessageId */ }
        return Results.Ok();
    }
    catch (SignatureException) { return Results.Unauthorized(); }
});
```

Use the raw body exactly as received. Requests older than 5 minutes are refused.

## Testing your own code

```csharp
using QMGateway.Testing;

var fake = new FakeTransport().QueueJson(System.Text.Json.Nodes.JsonNode.Parse("""{"success":true,"data":{"message_ids":[7],"recipients":1,"sms_units":1,"status":"Queued","remaining_units":99,"idempotent_replay":false,"numbers":["+265999123456"]}}""")!, 202);
var qm = new QMGatewayClient("qm_test_x", new QMGatewayOptions { Transport = fake });

var sent = await qm.Messages.SendAsync("+265999123456", "Hello");
Console.WriteLine(sent.MessageId);                       // 7
Console.WriteLine(fake.Last.Headers["x-api-key"]);       // qm_test_x
```

`QueueError()` and `QueueNetworkError()` play failures; `fake.Requests` shows what was sent. `Webhook.Sign()` builds a correctly signed request for testing your handler.

## Command line

```
dotnet tool install -g QMGateway.Cli
# paste a test key from the API keys page of your QMGateway account (it starts with qm_test_)
export QMGATEWAY_API_KEY="<your test key>"      # Windows PowerShell: $env:QMGATEWAY_API_KEY = "<your test key>"
qmgateway send +265999123456 "Hello"
```

## Developing this SDK

`dotnet run --project tests/QMGateway.Tests` runs the tests (no test packages needed). They use the shared contract files in `tests/contract` (or `QMGATEWAY_CONTRACT_DIR`). Add `-- live` with `QMGATEWAY_LIVE_KEY` and `QMGATEWAY_TEST_KEY` set to check against a real server.

Security problems: see [SECURITY.md](SECURITY.md). Licence: MIT.
