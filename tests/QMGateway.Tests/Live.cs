using QMGateway;

/// <summary>Runs the SDK against a real server: QMGATEWAY_BASE, QMGATEWAY_LIVE_KEY, QMGATEWAY_TEST_KEY.</summary>
static class Live
{
    public static async Task<int> RunAsync()
    {
        var baseUrl = Environment.GetEnvironmentVariable("QMGATEWAY_BASE") ?? "https://qmgateway.com";
        var live = Environment.GetEnvironmentVariable("QMGATEWAY_LIVE_KEY"); var test = Environment.GetEnvironmentVariable("QMGATEWAY_TEST_KEY");
        if (live is null || test is null) { Console.Error.WriteLine("Set QMGATEWAY_LIVE_KEY and QMGATEWAY_TEST_KEY."); return 2; }
        var o = new QMGatewayOptions { BaseUrl = baseUrl, Timeout = TimeSpan.FromSeconds(20), MaxRetries = 1 };
        var qm = new QMGatewayClient(live, o); var qt = QMGatewayClient.ForTest(test, o);
        int n = 0, bad = 0;
        async Task Step(string name, Func<Task> fn)
        {
            n++;
            try { await fn(); Console.WriteLine("  ok  " + name); } catch (Exception e) { bad++; Console.WriteLine($"FAIL  {name}: {e.GetType().Name}: {e.Message}"); }
        }
        static void Ok(bool c, string m = "unexpected result") { if (!c) throw new Exception(m); }

        await Step("account", async () => { var a = await qm.Account.GetAsync(); Ok(a.OrganisationName.Length > 0 && !a.IsTestKey && a.Service("sms") is not null); });
        await Step("balance", async () => Ok((await qm.Account.BalanceAsync()).Quota > 0));
        await Step("devices", async () => Ok((await qm.Devices.ListAsync()).Count > 0));
        await Step("statistics", async () => Ok((await qm.Account.StatisticsAsync()).Overview.Count > 0));
        await Step("inbox pages", async () => { var c = 0; await foreach (var _ in qm.Inbox.EachAsync(pageSize: 1)) c++; Ok(c >= 1); });
        await Step("test-key send then read it back", async () => { var r = await qt.Messages.SendAsync("+265991234567", "SDK live test"); var m = await qt.Messages.GetAsync(r.MessageId); Ok(m.PhoneNumber.Length > 0 && m.Mode == "test"); });
        await Step("same Idempotency-Key does not send twice", async () =>
        {
            var k = "sdk-live-" + Guid.NewGuid().ToString("N")[..10]; var opt = new SendOptions { IdempotencyKey = k };
            var a = await qt.Messages.SendAsync("+265991234567", "Once", opt); var b = await qt.Messages.SendAsync("+265991234567", "Once", opt);
            Ok(a.MessageId == b.MessageId && b.IdempotentReplay);
        });
        await Step("list messages with auto-pagination", async () =>
        {
            for (var i = 0; i < 3; i++) await qt.Messages.SendAsync("+265991234567", "Page test " + i);
            var c = 0; await foreach (var _ in qt.Messages.EachAsync(pageSize: 2)) if (++c >= 5) break; Ok(c >= 3);
        });
        await Step("schedule, list, reschedule, cancel (live key)", async () =>
        {
            var r = await qm.Messages.SendAsync("+265991234567", "SDK scheduled", new SendOptions { ScheduledAt = DateTimeOffset.UtcNow.AddDays(3) }); var id = r.MessageId;
            var found = false; await foreach (var s in qm.Messages.EachScheduledAsync(5)) if (s.Id == id) found = true; Ok(found, "not listed");
            Ok((await qm.Messages.RescheduleAsync(id, DateTimeOffset.UtcNow.AddDays(4))).Id == id);
            Ok((await qm.Messages.CancelAsync(id)).Status.Length > 0);
        });
        await Step("errors are typed", async () =>
        {
            try { await new QMGatewayClient("qm_live_" + new string('0', 48), o).Account.GetAsync(); Ok(false, "bad key accepted"); } catch (AuthenticationException e) { Ok(e.Status == 401 && e.RequestId is not null); }
            try { await qm.Messages.SendAsync("not-a-number", "x"); Ok(false, "bad number accepted"); } catch (InvalidRequestException e) { Ok(e.Code.Length > 0); }
        });
        await Step("test key is refused on account endpoints", async () =>
        {
            try { await qt.Account.BalanceAsync(); Ok(false, "allowed"); } catch (AuthenticationException e) { Ok(e.Code == "TEST_KEY_NOT_ALLOWED", e.Code); }
        });
        Console.WriteLine($"\n{n - bad} passed, {bad} failed");
        return bad > 0 ? 1 : 0;
    }
}
