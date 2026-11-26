// Command-line tool.  QMGATEWAY_API_KEY=qm_live_... qmgateway account
// The key is read from the environment on purpose: a key typed as an argument stays in your shell history.
using System.Text.Json;
using QMGateway;

const string Help = """
qmgateway command-line tool

Set QMGATEWAY_API_KEY (and QMGATEWAY_BASE_URL for a non-default server), then:
  account                       who the key belongs to and which services are ready
  balance                       what is left of your plan
  devices                       your gateway phones
  send <phone> <message>        queue a message (add --at="2026-12-01 08:00:00" to schedule it)
  status <id>                   where a message is now
  messages [--status=Failed]    the latest messages
  verify-webhook --secret=...   check a webhook: raw body on standard input, --header="Name: value" for each header
Add --json for machine-readable output.
""";

var argv = args.ToList();
var cmd = argv.Count > 0 ? argv[0] : "help"; if (argv.Count > 0) argv.RemoveAt(0);
var asJson = argv.Remove("--json");
string? Opt(string name) => argv.FirstOrDefault(a => a.StartsWith($"--{name}=", StringComparison.Ordinal))?[(name.Length + 3)..];
void Out(object o, System.Text.Json.Nodes.JsonNode? raw = null)
{
    if (asJson && raw is not null) Console.WriteLine(raw.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    else if (o is IEnumerable<KeyValuePair<string, string>> kv) foreach (var (k, v) in kv) Console.WriteLine(k.PadRight(18) + v);
    else Console.WriteLine(o);
}

if (cmd is "help" or "--help" or "-h") { Console.WriteLine(Help); return 0; }
if (cmd == "verify-webhook")
{
    var secret = Opt("secret") ?? Environment.GetEnvironmentVariable("QMGATEWAY_WEBHOOK_SECRET");
    if (secret is null) { Console.Error.WriteLine("Give --secret=... or set QMGATEWAY_WEBHOOK_SECRET."); return 2; }
    var headers = argv.Where(a => a.StartsWith("--header=") && a.Contains(':')).Select(a => a[9..].Split(':', 2)).ToDictionary(p => p[0].Trim(), p => p[1].Trim());
    using var stdin = Console.OpenStandardInput(); using var ms = new MemoryStream(); stdin.CopyTo(ms);
    try { var e = Webhook.Verify(ms.ToArray(), headers, secret); Out(new Dictionary<string, string> { ["verified"] = "True", ["event"] = e.Type, ["event_id"] = e.Id, ["message_id"] = e.MessageId?.ToString() ?? "" }); return 0; }
    catch (SignatureException e) { Out(new Dictionary<string, string> { ["verified"] = "False", ["reason"] = e.ReasonCode }); return 3; }
}
var key = Environment.GetEnvironmentVariable("QMGATEWAY_API_KEY");
if (string.IsNullOrEmpty(key)) { Console.Error.WriteLine("Set the QMGATEWAY_API_KEY environment variable."); return 2; }
try
{
    var baseUrl = Environment.GetEnvironmentVariable("QMGATEWAY_BASE_URL");
    var qm = new QMGatewayClient(key, baseUrl is null ? null : new QMGatewayOptions { BaseUrl = baseUrl });
    switch (cmd)
    {
        case "account": { var a = await qm.Account.GetAsync(); Out(new Dictionary<string, string> { ["organisation"] = a.OrganisationName, ["key"] = a.KeyMode, ["sms_ready"] = (a.Service("sms")?.ReadyToSend ?? false).ToString() }, a.Raw); break; }
        case "balance": { var b = await qm.Account.BalanceAsync(); Out(new Dictionary<string, string> { ["plan"] = b.Plan ?? "", ["remaining"] = $"{b.Remaining} of {b.Quota}", ["level"] = b.Level, ["expires_at"] = b.ExpiresAt ?? "" }, b.Raw); break; }
        case "devices": foreach (var d in await qm.Devices.ListAsync()) Out($"#{d.Id} {d.Name} ({d.Status}, {d.Queued} queued)", d.Raw); break;
        case "send":
        {
            var at = Opt("at"); var p = argv.Where(a => !a.StartsWith("--at=")).ToList();
            if (p.Count < 2) { Console.Error.WriteLine("Usage: qmgateway send <phone> <message> [--at=\"YYYY-MM-DD HH:MM:SS\"]"); return 2; }
            DateTimeOffset? when = at is null ? null : MalawiTime.Parse(at) ?? throw new ArgumentException("--at must look like 2026-12-01 08:00:00");
            var r = await qm.Messages.SendAsync(p[0], string.Join(' ', p.Skip(1)), new SendOptions { ScheduledAt = when });
            Out($"Queued message {r.MessageId} ({r.RemainingUnits} units left)", r.Raw); break;
        }
        case "status": { var m = await qm.Messages.GetAsync(int.Parse(argv.FirstOrDefault() ?? "0")); Out($"#{m.Id} {m.PhoneNumber}: {m.Status}" + (m.FailureReason is null ? "" : $" ({m.FailureReason})"), m.Raw); break; }
        case "messages": foreach (var m in (await qm.Messages.ListAsync(status: Opt("status"), limit: 20)).Items) Out($"#{m.Id} {m.PhoneNumber} {m.Status}", m.Raw); break;
        default: Console.Error.WriteLine($"Unknown command \"{cmd}\". Run qmgateway help."); return 2;
    }
}
catch (QMGatewayException e) { Console.Error.WriteLine($"Error: {e.Message}" + (e is ApiException a ? $" [{a.Code}]" : "")); return 1; }
return 0;
