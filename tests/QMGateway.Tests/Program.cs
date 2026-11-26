// Test runner with no dependencies:  dotnet run --project tests/QMGateway.Tests            (offline tests)
//                                    dotnet run --project tests/QMGateway.Tests -- live    (against a real server)
using System.Text.Json.Nodes;
using QMGateway;
using QMGateway.Testing;

if (args.Contains("live")) return await Live.RunAsync();

int pass = 0, failed = 0;
async Task Check(string name, Func<Task> fn)
{
    try { await fn(); pass++; Console.WriteLine("  ok  " + name); }
    catch (Exception e) { failed++; Console.WriteLine($"FAIL  {name}: {e.Message}"); }
}
static void Same<T>(T actual, T expected, string what = "") { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"{what} expected {expected}, got {actual}"); }
static void True(bool c, string m = "unexpected result") { if (!c) throw new Exception(m); }
static async Task<T> Raises<T>(Func<Task> fn) where T : Exception
{
    try { await fn(); } catch (T e) { return e; } catch (Exception e) { throw new Exception($"expected {typeof(T).Name}, got {e.GetType().Name}: {e.Message}"); }
    throw new Exception($"expected {typeof(T).Name}, nothing was thrown");
}

var dir = Environment.GetEnvironmentVariable("QMGATEWAY_CONTRACT_DIR") ?? FindContract();
static string FindContract()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
    {
        if (Directory.Exists(Path.Combine(d.FullName, "sdk-contract"))) return Path.Combine(d.FullName, "sdk-contract");
        if (Directory.Exists(Path.Combine(d.FullName, "tests", "contract"))) return Path.Combine(d.FullName, "tests", "contract");
    }
    throw new DirectoryNotFoundException("Contract folder not found. Set QMGATEWAY_CONTRACT_DIR.");
}
JsonObject Contract(string f) => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, f)))!;

JsonObject Sent() => (JsonObject)JsonNode.Parse("""{"success":true,"data":{"message_ids":[7],"recipients":1,"sms_units":1,"status":"Queued","remaining_units":99,"idempotent_replay":false,"numbers":["+265999123456"]}}""")!;
(QMGatewayClient, List<double>) Make(FakeTransport fake, int retries = 4)
{
    var sleeps = new List<double>();
    return (new QMGatewayClient("qm_test_abc", new QMGatewayOptions { Transport = fake, MaxRetries = retries, Delay = (t, _) => { sleeps.Add(t.TotalSeconds); return Task.CompletedTask; } }), sleeps);
}

Console.WriteLine("Requests");
await Check("send builds the request and the result", async () =>
{
    var fake = new FakeTransport().QueueJson(Sent(), 202);
    var r = await Make(fake).Item1.Messages.SendAsync("+265999123456", "Hi", new SendOptions { ScheduledAt = new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.FromHours(2)) });
    Same(r.MessageId, 7); Same(r.RemainingUnits, 99);
    var q = fake.Last;
    Same(q.Method, "POST"); Same(q.Url, "https://qmgateway.com/api/v1/messages"); Same(q.Headers["x-api-key"], "qm_test_abc");
    var body = JsonNode.Parse(q.Body!)!;
    Same(body["phone"]!.ToString(), "+265999123456"); Same(body["scheduled_at"]!.ToString(), "2026-12-01 08:00:00");
    Same(q.Headers["idempotency-key"].Length, 32);
});
await Check("UTC time is converted to Malawi time", async () =>
{
    var fake = new FakeTransport().QueueJson(Sent(), 202);
    await Make(fake).Item1.Messages.SendAsync("+265999123456", "Hi", new SendOptions { ScheduledAt = new DateTimeOffset(2026, 12, 1, 6, 0, 0, TimeSpan.Zero) });
    Same(JsonNode.Parse(fake.Last.Body!)!["scheduled_at"]!.ToString(), "2026-12-01 08:00:00");
});
await Check("many recipients, query encoding", async () =>
{
    var fake = new FakeTransport().QueueJson(Sent(), 202).QueueJson(new JsonObject { ["success"] = true, ["data"] = new JsonArray(), ["next_before_id"] = null });
    var qm = Make(fake).Item1;
    await qm.Messages.SendAsync(["+265999123456", "+265888123456"], "Hi");
    Same(JsonNode.Parse(fake.Last.Body!)!["recipients"]!.AsArray().Count, 2);
    await qm.Messages.ListAsync(status: "Failed", phone: "+265999123456");
    Same(fake.Last.Url, "https://qmgateway.com/api/v1/messages?status=Failed&phone=%2B265999123456");
});
await Check("unknown fields are ignored and kept in Raw", async () =>
{
    var s = Sent(); s["data"]!["brand_new"] = "x";
    Same((await Make(new FakeTransport().QueueJson(s, 202)).Item1.Messages.SendAsync("+265999123456", "Hi")).Raw["brand_new"]!.ToString(), "x");
});
await Check("options are checked", () =>
{
    foreach (var o in new[] { new QMGatewayOptions { BaseUrl = "http://qmgateway.com" }, new QMGatewayOptions { Timeout = TimeSpan.Zero }, new QMGatewayOptions { MaxRetries = 11 } })
        try { _ = new QMGatewayClient("k", o); throw new Exception("accepted"); } catch (ArgumentException) { }
    try { _ = new QMGatewayClient("bad key"); throw new Exception("accepted"); } catch (ArgumentException) { }
    _ = new QMGatewayClient("k", new QMGatewayOptions { BaseUrl = "http://127.0.0.1:8120" });
    return Task.CompletedTask;
});

await Check("an idempotency key with a line break is refused", async () => { try { await Make(new FakeTransport()).Item1.Messages.SendAsync("+265999123456", "x", new SendOptions { IdempotencyKey = "a\r\nX-Evil: 1" }); throw new Exception("accepted"); } catch (ArgumentException) { } });

Console.WriteLine("Errors");
await Check("statuses map to typed errors", async () =>
{
    foreach (var (status, type) in new (int, Type)[] { (401, typeof(AuthenticationException)), (403, typeof(PermissionDeniedException)), (404, typeof(NotFoundException)), (422, typeof(InvalidRequestException)), (400, typeof(InvalidRequestException)) })
    {
        var fake = new FakeTransport().QueueError(status, "SOME_CODE", "Bad thing");
        var e = await Raises<ApiException>(() => Make(fake).Item1.Account.GetAsync());
        True(e.GetType() == type, $"{status} gave {e.GetType().Name}");
        Same(e.Code, "SOME_CODE"); Same(e.RequestId, "req_fake"); Same(e.Status, status); Same(e.PlainMessage, "Bad thing"); Same(fake.Requests.Count, 1, "no retry on " + status);
    }
});
await Check("details are kept; unreadable answers are typed", async () =>
{
    var e = await Raises<ApiException>(() => Make(new FakeTransport().QueueError(402, "INSUFFICIENT_BALANCE", "No units", details: new JsonObject { ["remaining_units"] = 0 })).Item1.Messages.SendAsync("+265999123456", "x"));
    Same(e.Details["remaining_units"]!.ToString(), "0");
    Same((await Raises<ApiException>(() => Make(new FakeTransport().QueueRaw(200, "<html>oops</html>")).Item1.Account.GetAsync())).Code, "INVALID_RESPONSE");
});

Console.WriteLine("Retries");
await Check("429, 503 then ok: waits Retry-After, same idempotency key", async () =>
{
    var fake = new FakeTransport().QueueError(429, "RATE_LIMITED", headers: new Dictionary<string, string> { ["Retry-After"] = "1" }).QueueError(503, "SERVICE_UNAVAILABLE").QueueJson(Sent(), 202);
    var (qm, sleeps) = Make(fake);
    await qm.Messages.SendAsync("+265999123456", "Hi");
    Same(fake.Requests.Count, 3); Same(sleeps[0], 1.0); True(sleeps[1] is >= 2.0 and <= 2.4);
    Same(fake.Requests.Select(r => r.Headers["idempotency-key"]).Distinct().Count(), 1);
});
await Check("always unavailable: 5 attempts then ServerException", async () =>
{
    var fake = new FakeTransport(); for (var i = 0; i < 5; i++) fake.QueueError(503, "SERVICE_UNAVAILABLE");
    var e = await Raises<ServerException>(() => Make(fake).Item1.Messages.SendAsync("+265999123456", "Hi"));
    Same(fake.Requests.Count, 5); Same(e.Status, 503);
});
await Check("Retry-After is capped at 60", async () =>
{
    var fake = new FakeTransport().QueueError(429, "RATE_LIMITED", headers: new Dictionary<string, string> { ["Retry-After"] = "3600" }).QueueJson(Sent(), 202);
    var (qm, sleeps) = Make(fake); await qm.Messages.SendAsync("+265999123456", "Hi"); Same(sleeps.Single(), 60.0);
});
await Check("network errors are retried; zero retries turns it off", async () =>
{
    var fake = new FakeTransport(); for (var i = 0; i < 3; i++) fake.QueueNetworkError();
    await Raises<ConnectionException>(() => Make(fake, 2).Item1.Messages.SendAsync("+265999123456", "Hi")); Same(fake.Requests.Count, 3);
    var f2 = new FakeTransport().QueueError(503, "X"); await Raises<ServerException>(() => Make(f2, 0).Item1.Account.GetAsync()); Same(f2.Requests.Count, 1);
});
await Check("codes are not repeated after 5xx or network errors, but are after 429", async () =>
{
    var f = new FakeTransport().QueueError(503, "X"); await Raises<ServerException>(() => Make(f).Item1.Otp.SendAsync(phone: "+265999123456")); Same(f.Requests.Count, 1);
    f = new FakeTransport().QueueNetworkError(); await Raises<ConnectionException>(() => Make(f).Item1.Otp.SendAsync(phone: "+265999123456")); Same(f.Requests.Count, 1);
    f = new FakeTransport().QueueError(429, "R", headers: new Dictionary<string, string> { ["Retry-After"] = "1" }).QueueJson(JsonNode.Parse("""{"success":true,"data":{"request_reference":"r1","expires_in":300,"status":"sent","channel":"sms"}}""")!);
    Same((await Make(f).Item1.Otp.SendAsync(phone: "+265999123456")).RequestReference, "r1");
});
await Check("a final rate limit carries RetryAfter", async () =>
{
    var fake = new FakeTransport(); for (var i = 0; i < 5; i++) fake.QueueError(429, "R", headers: new Dictionary<string, string> { ["Retry-After"] = "7" });
    Same((await Raises<RateLimitException>(() => Make(fake).Item1.Account.GetAsync())).RetryAfter, 7);
});

Console.WriteLine("Pagination (shared cases)");
foreach (var c in Contract("pagination-cases.json")["cases"]!.AsArray().Cast<JsonObject>())
    await Check("pagination: " + c["name"], async () =>
    {
        var fake = new FakeTransport(); var inbox = c["operationId"]!.ToString() == "get_inbox"; var pages = c["pages"]!.AsArray();
        for (var i = 0; i < pages.Count; i++)
        {
            var ids = pages[i]!.AsArray().Select(x => (int)x!).ToList();
            var items = new JsonArray(ids.Select(id => (JsonNode?)(inbox ? JsonNode.Parse($$"""{"id":{{id}},"from":"+265999","message":"m","received_at":"2026-11-19 10:29:41"}""") : JsonNode.Parse($$"""{"id":{{id}},"phone_number":"+265999","status":"Sent","message_type":"sms","sms_units":1}"""))).ToArray());
            var page = new JsonObject { ["success"] = true, ["data"] = items }; page[c["next_field"]!.ToString()] = i + 1 < pages.Count ? ids[^1] : null;
            fake.QueueJson(page);
        }
        var qm = Make(fake).Item1; var got = new List<int>();
        if (inbox) await foreach (var m in qm.Inbox.EachAsync()) got.Add(m.Id); else await foreach (var m in qm.Messages.EachAsync()) got.Add(m.Id);
        True(got.SequenceEqual(c["expect_ids"]!.AsArray().Select(x => (int)x!)), "ids " + string.Join(",", got)); Same(fake.Requests.Count, pages.Count);
        if (pages.Count > 1) True(fake.Requests[1].Url.Contains(c["param"] + "="), "cursor missing");
    });

Console.WriteLine("Webhooks (shared cases)");
var wc = Contract("webhook-cases.json");
foreach (var c in wc["cases"]!.AsArray().Cast<JsonObject>())
    await Check("webhook: " + c["id"], () =>
    {
        var headers = c["headers"]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.ToString());
        Func<WebhookEvent> run = () => Webhook.Verify(c["body"]!.ToString(), headers, wc["secret"]!.ToString(), (int)wc["tolerance_seconds"]!, (long)wc["now"]!);
        if ((bool)c["expect_verified"]!) { var e = run(); Same(e.Type, headers["X-QMGateway-Event"]); Same(e.MessageId, 1052); }
        else { try { run(); throw new Exception("should have been rejected"); } catch (SignatureException e) { Same(e.ReasonCode, c["reason"]!.ToString()); } }
        return Task.CompletedTask;
    });
await Check("Webhook.Sign is accepted by Verify", () =>
{
    var (body, headers) = Webhook.Sign("""{"message_id":5,"status":"Failed"}""", "whsec_x", "sms.failed");
    var e = Webhook.Verify(System.Text.Encoding.UTF8.GetBytes(body), headers, "whsec_x"); Same(e.Type, "sms.failed"); Same(e.Status, "Failed");
    return Task.CompletedTask;
});

Console.WriteLine("Models");
await Check("account, devices and message parse", async () =>
{
    var a = await Make(new FakeTransport().QueueJson(JsonNode.Parse("""{"success":true,"data":{"organisation":{"id":3,"name":"Acme"},"key":{"mode":"test","scopes":["messages:send"]},"services":[{"key":"sms","name":"SMS","status":"active","details":{"ready_to_send":true}}]}}""")!)).Item1.Account.GetAsync();
    True(a.IsTestKey && a.Service("sms")!.ReadyToSend); Same(a.OrganisationName, "Acme");
    var d = await Make(new FakeTransport().QueueJson(JsonNode.Parse("""{"success":true,"data":[{"id":1,"name":"Phone","status":"online","last_seen_at":"2026-11-19 10:29:41","networks":["Airtel"],"capacity":2,"queued":0}]}""")!)).Item1.Devices.ListAsync();
    True(d[0].IsOnline); Same(d[0].LastSeenAt!.Value.UtcDateTime.Hour, 8);
    var m = await Make(new FakeTransport().QueueJson(JsonNode.Parse("""{"success":true,"data":{"id":9,"phone_number":"+265999123456","status":"Delivered","message_type":"sms","sms_units":1}}""")!)).Item1.Messages.GetAsync(9);
    True(m.IsDelivered && m.IsFinished);
});

Console.WriteLine($"\n{pass} passed, {failed} failed");
return failed > 0 ? 1 : 0;
